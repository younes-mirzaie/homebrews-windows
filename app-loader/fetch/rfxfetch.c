/* rfxfetch: download one file over HTTPS (NSS, TLS 1.2/1.3, strict certificate checks) and verify it.
 *
 *   rfxfetch <https-url> <out-file> [--sha256 HEX] [--size N] [--max-bytes N]
 *            [--roots MODULE] [--ca PEM] [--timeout SECONDS] [--quiet]
 *
 * Follows up to 5 redirects (https only). Writes <out-file>.part, then renames it to <out-file>
 * only when the size and SHA-256 match (when given). On any failure the partial file is deleted.
 * stdout, one line each:  PROGRESS <bytes> <total or -1>   then   OK <sha256> <bytes>  |  ERROR <text>
 * Exit codes: 0 ok, 1 network/HTTP/TLS error, 2 verification failed, 3 usage.
 *
 * --roots: NSS root store module (default: nssckbi.dll next to rfxfetch.exe on Windows).
 * --ca:    extra trusted root in PEM (tests only). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <errno.h>

#include "nspr.h"
#include "nss.h"
#include "pk11pub.h"
#include "secmod.h"
#include "ssl.h"
#include "sslproto.h"
#include "secerr.h"
#include "sslerr.h"
#include "cert.h"
#include "certdb.h"
#include "base64.h"

#ifdef _WIN32
#include <windows.h>
#define strcasecmp _stricmp
#define strncasecmp _strnicmp
#else
#include <strings.h>
#endif

#define VERSION "0.1"
#define MAX_REDIRECTS 5
#define HDR_MAX 16384

static int g_quiet;
static PRIntervalTime g_timeout;
static SECMODModule *g_roots;
static char g_err[512];

static const char *err_text(PRErrorCode e)
{
    switch (e) {
    case PR_CONNECT_REFUSED_ERROR: return "connection refused";
    case PR_IO_TIMEOUT_ERROR: return "timed out";
    case PR_CONNECT_RESET_ERROR: return "connection reset";
    case PR_NETWORK_UNREACHABLE_ERROR: return "network unreachable (is Wi-Fi or mobile data on?)";
    case PR_HOST_UNREACHABLE_ERROR: return "server unreachable";
    case PR_DIRECTORY_LOOKUP_ERROR: return "server name not found";
    case PR_END_OF_FILE_ERROR: return "server closed the connection";
    case SSL_ERROR_BAD_CERT_DOMAIN: return "certificate is for a different server name";
    case SEC_ERROR_EXPIRED_CERTIFICATE: return "certificate expired (is the phone's clock right?)";
    case SEC_ERROR_UNKNOWN_ISSUER: return "certificate issuer unknown";
    case SEC_ERROR_CA_CERT_INVALID: return "certificate issuer is invalid";
    case SEC_ERROR_UNTRUSTED_ISSUER: return "certificate issuer not trusted";
    case SSL_ERROR_NO_CYPHER_OVERLAP: return "no common cipher with the server";
    default: { const char *n = PORT_ErrorToName(e); return n ? n : "network error"; }
    }
}

static int fail(const char *fmt, const char *a, const char *b)
{
    snprintf(g_err, sizeof(g_err), fmt, a ? a : "", b ? b : "");
    return -1;
}

static int fail_nspr(const char *what, const char *host)
{
    PRErrorCode e = PR_GetError();
    snprintf(g_err, sizeof(g_err), "%s %s: %s", what, host, err_text(e));
    return -1;
}

/* ---- URL ---- */
typedef struct { char host[256]; int port; char path[2048]; } url_t;

static int parse_url(const char *u, url_t *out)
{
    const char *p, *slash, *colon;
    size_t hl;
    if (strncasecmp(u, "https://", 8) != 0) return fail("only https:// URLs are allowed: %s", u, NULL);
    p = u + 8;
    slash = strchr(p, '/');
    if (!slash) slash = p + strlen(p);
    if (memchr(p, '@', slash - p)) return fail("URLs with user names are not allowed%s", NULL, NULL);
    colon = memchr(p, ':', slash - p);
    hl = (colon ? colon : slash) - p;
    if (hl == 0 || hl >= sizeof(out->host)) return fail("bad host in %s", u, NULL);
    memcpy(out->host, p, hl);
    out->host[hl] = '\0';
    for (size_t i = 0; i < hl; i++)
        if (!(isalnum((unsigned char)out->host[i]) || out->host[i] == '.' || out->host[i] == '-'))
            return fail("bad host in %s", u, NULL);
    out->port = 443;
    if (colon) {
        char *end;
        long port = strtol(colon + 1, &end, 10);
        if (end != slash || port <= 0 || port > 65535) return fail("bad port in %s", u, NULL);
        out->port = (int)port;
    }
    if (*slash == '\0') strcpy(out->path, "/");
    else {
        if (strlen(slash) >= sizeof(out->path)) return fail("URL too long%s", NULL, NULL);
        strcpy(out->path, slash);
    }
    for (const char *c = out->path; *c; c++)
        if ((unsigned char)*c <= ' ' || *c == '#') {
            if (*c == '#') { out->path[c - out->path] = '\0'; break; }
            return fail("URL has spaces or control characters%s", NULL, NULL);
        }
    return 0;
}

/* ---- connection with a read buffer ---- */
typedef struct { PRFileDesc *fd; char buf[32768]; size_t s, e; } conn_t;

static int conn_fill(conn_t *c)
{
    PRInt32 n;
    if (c->s == c->e) c->s = c->e = 0;
    if (c->e == sizeof(c->buf)) {
        memmove(c->buf, c->buf + c->s, c->e - c->s);
        c->e -= c->s; c->s = 0;
    }
    n = PR_Recv(c->fd, c->buf + c->e, (PRInt32)(sizeof(c->buf) - c->e), 0, g_timeout);
    if (n < 0) return -1;
    if (n == 0) { PR_SetError(PR_END_OF_FILE_ERROR, 0); return 0; }
    c->e += (size_t)n;
    return (int)n;
}

/* one header line, CRLF stripped; returns length or -1 */
static int conn_line(conn_t *c, char *out, size_t cap)
{
    for (;;) {
        char *nl = memchr(c->buf + c->s, '\n', c->e - c->s);
        if (nl) {
            size_t n = (size_t)(nl - (c->buf + c->s));
            size_t keep = n;
            if (keep && c->buf[c->s + keep - 1] == '\r') keep--;
            if (keep >= cap) { fail("response header line too long%s", NULL, NULL); return -1; }
            memcpy(out, c->buf + c->s, keep);
            out[keep] = '\0';
            c->s += n + 1;
            return (int)keep;
        }
        if (c->e - c->s >= cap) { fail("response header line too long%s", NULL, NULL); return -1; }
        if (conn_fill(c) <= 0) { fail_nspr("reading from", ""); return -1; }
    }
}

static PRFileDesc *connect_tls(const url_t *u)
{
    PRAddrInfo *ai = PR_GetAddrInfoByName(u->host, PR_AF_UNSPEC, PR_AI_ADDRCONFIG);
    PRNetAddr addr;
    void *it = NULL;
    PRFileDesc *fd = NULL, *ssl;
    if (!ai) { fail_nspr("looking up", u->host); return NULL; }
    while ((it = PR_EnumerateAddrInfo(it, ai, (PRUint16)u->port, &addr)) != NULL) {
        fd = PR_OpenTCPSocket(addr.raw.family);
        if (!fd) continue;
        if (PR_Connect(fd, &addr, g_timeout) == PR_SUCCESS) break;
        PR_Close(fd);
        fd = NULL;
    }
    PR_FreeAddrInfo(ai);
    if (!fd) { fail_nspr("connecting to", u->host); return NULL; }
    ssl = SSL_ImportFD(NULL, fd);
    if (!ssl) { PR_Close(fd); fail_nspr("TLS setup for", u->host); return NULL; }
    SSL_OptionSet(ssl, SSL_SECURITY, PR_TRUE);
    SSL_OptionSet(ssl, SSL_HANDSHAKE_AS_CLIENT, PR_TRUE);
    SSL_SetURL(ssl, u->host);   /* SNI + the name the default certificate check verifies */
    if (SSL_ResetHandshake(ssl, PR_FALSE) != SECSuccess ||
        SSL_ForceHandshakeWithTimeout(ssl, g_timeout) != SECSuccess) {
        fail_nspr("secure connection to", u->host);
        PR_Close(ssl);
        return NULL;
    }
    return ssl;
}

static int send_all(PRFileDesc *fd, const char *p, size_t n)
{
    while (n) {
        PRInt32 w = PR_Send(fd, p, (PRInt32)n, 0, g_timeout);
        if (w <= 0) return -1;
        p += w; n -= (size_t)w;
    }
    return 0;
}

/* ---- body sink: file + hash + limits + progress ---- */
typedef struct {
    FILE *f;
    PK11Context *sha;
    long long got, total, max;
    long long next_progress;
} sink_t;

static int sink_write(sink_t *k, const char *p, size_t n)
{
    if (k->got + (long long)n > k->max) return fail("file is larger than the limit%s", NULL, NULL);
    if (fwrite(p, 1, n, k->f) != n) return fail("can't write the file: %s", strerror(errno), NULL);
    PK11_DigestOp(k->sha, (const unsigned char *)p, (unsigned)n);
    k->got += (long long)n;
    if (!g_quiet && k->got >= k->next_progress) {
        printf("PROGRESS %lld %lld\n", k->got, k->total);
        fflush(stdout);
        k->next_progress = k->got + 256 * 1024;
    }
    return 0;
}

/* copies exactly n bytes of body (n < 0: until the server closes) */
static int body_copy(conn_t *c, sink_t *k, long long n)
{
    for (;;) {
        size_t have = c->e - c->s;
        if (n >= 0 && (long long)have > n) have = (size_t)n;
        if (have) {
            if (sink_write(k, c->buf + c->s, have) < 0) return -1;
            c->s += have;
            if (n >= 0) n -= (long long)have;
        }
        if (n == 0) return 0;
        int r = conn_fill(c);
        if (r < 0) return fail_nspr("downloading from", "");
        if (r == 0) {
            if (n < 0) return 0;
            return fail("the connection closed before the whole file arrived%s", NULL, NULL);
        }
    }
}

static int body_chunked(conn_t *c, sink_t *k)
{
    char line[256];
    for (;;) {
        char *end;
        unsigned long long sz;
        if (conn_line(c, line, sizeof(line)) < 0) return -1;
        sz = strtoull(line, &end, 16);
        if (end == line) return fail("bad chunked encoding%s", NULL, NULL);
        if (sz == 0) {
            while (conn_line(c, line, sizeof(line)) > 0) {}   /* trailers */
            return 0;
        }
        if ((long long)sz > k->max) return fail("file is larger than the limit%s", NULL, NULL);
        if (body_copy(c, k, (long long)sz) < 0) return -1;
        if (conn_line(c, line, sizeof(line)) != 0) return fail("bad chunked encoding%s", NULL, NULL);
    }
}

/* returns 0 done, 1 redirect (next filled), -1 error */
static int fetch_once(const url_t *u, sink_t *k, url_t *next)
{
    char req[3072], line[HDR_MAX], location[2048] = "";
    long long clen = -1;
    int status = 0, chunked = 0, rc = -1;
    conn_t *c = calloc(1, sizeof(*c));
    if (!c) return fail("out of memory%s", NULL, NULL);
    c->fd = connect_tls(u);
    if (!c->fd) { free(c); return -1; }
    snprintf(req, sizeof(req),
             "GET %s HTTP/1.1\r\nHost: %s\r\nUser-Agent: rfxfetch/" VERSION " (Windows Phone 8.1)\r\n"
             "Accept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n", u->path, u->host);
    if (send_all(c->fd, req, strlen(req)) < 0) { fail_nspr("sending to", u->host); goto out; }
    if (conn_line(c, line, sizeof(line)) < 0) goto out;
    if (sscanf(line, "HTTP/%*d.%*d %d", &status) != 1) { fail("not an HTTP response from %s", u->host, NULL); goto out; }
    for (;;) {
        int n = conn_line(c, line, sizeof(line));
        char *v;
        if (n < 0) goto out;
        if (n == 0) break;
        v = strchr(line, ':');
        if (!v) continue;
        *v++ = '\0';
        while (*v == ' ' || *v == '\t') v++;
        if (!strcasecmp(line, "Content-Length")) {
            char *end;
            clen = strtoll(v, &end, 10);
            if (end == v || clen < 0) { fail("bad Content-Length%s", NULL, NULL); goto out; }
        } else if (!strcasecmp(line, "Transfer-Encoding")) {
            chunked = strstr(v, "chunked") != NULL;
        } else if (!strcasecmp(line, "Location")) {
            snprintf(location, sizeof(location), "%s", v);
        }
    }
    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308) {
        if (!location[0]) { fail("redirect without a Location%s", NULL, NULL); goto out; }
        if (location[0] == '/' && location[1] != '/') {
            *next = *u;
            if (strlen(location) >= sizeof(next->path)) { fail("redirect URL too long%s", NULL, NULL); goto out; }
            strcpy(next->path, location);
            rc = 1;
        } else if (parse_url(location, next) == 0) {
            rc = 1;
        }
        goto out;
    }
    if (status != 200) {
        char s[16];
        snprintf(s, sizeof(s), "%d", status);
        fail("server answered HTTP %s for %s", s, u->path);
        goto out;
    }
    if (clen >= 0 && clen > k->max) { fail("file is larger than the limit%s", NULL, NULL); goto out; }
    k->total = chunked ? -1 : clen;
    if (chunked) rc = body_chunked(c, k);
    else rc = body_copy(c, k, clen);
out:
    PR_Close(c->fd);
    free(c);
    return rc;
}

/* ---- trust ---- */
static int add_ca(const char *pem_path)
{
    FILE *f = fopen(pem_path, "rb");
    char *pem, *b, *e;
    long n;
    unsigned int dlen;
    unsigned char *der;
    SECItem item;
    CERTCertificate *cert;
    CERTCertTrust trust;
    if (!f) return fail("can't open %s", pem_path, NULL);
    fseek(f, 0, SEEK_END); n = ftell(f); fseek(f, 0, SEEK_SET);
    pem = calloc(1, (size_t)n + 1);
    if (!pem || fread(pem, 1, (size_t)n, f) != (size_t)n) { fclose(f); free(pem); return fail("can't read %s", pem_path, NULL); }
    fclose(f);
    b = strstr(pem, "-----BEGIN CERTIFICATE-----");
    e = b ? strstr(b, "-----END CERTIFICATE-----") : NULL;
    if (!b || !e) { free(pem); return fail("%s is not a PEM certificate", pem_path, NULL); }
    b += 27; *e = '\0';
    der = ATOB_AsciiToData(b, &dlen);
    free(pem);
    if (!der) return fail("%s: bad base64", pem_path, NULL);
    item.type = siDERCertBuffer; item.data = der; item.len = dlen;
    cert = CERT_NewTempCertificate(CERT_GetDefaultCertDB(), &item, NULL, PR_FALSE, PR_TRUE);
    PORT_Free(der);
    if (!cert) return fail_nspr("loading", pem_path);
    CERT_DecodeTrustString(&trust, "C,,");
    if (CERT_ChangeCertTrust(CERT_GetDefaultCertDB(), cert, &trust) != SECSuccess) {
        CERT_DestroyCertificate(cert);
        return fail_nspr("trusting", pem_path);
    }
    /* kept for the life of the process */
    return 0;
}

static int load_roots(const char *module)
{
    char spec[1200], esc[1024];
    size_t n = 0;
    for (const char *p = module; *p && n + 2 < sizeof(esc); p++) {
        if (*p == '\\' || *p == '"') esc[n++] = '\\';
        esc[n++] = *p;
    }
    esc[n] = '\0';
    snprintf(spec, sizeof(spec), "name=\"Root Certs\" library=\"%s\"", esc);
    g_roots = SECMOD_LoadUserModule(spec, NULL, PR_FALSE);
    if (!g_roots || !g_roots->loaded) return fail_nspr("loading root certificates from", module);
    return 0;
}

static void default_roots(char *out, size_t cap)
{
    out[0] = '\0';
#ifdef _WIN32
    char exe[MAX_PATH];
    DWORD n = GetModuleFileNameA(NULL, exe, MAX_PATH);
    if (n && n < MAX_PATH) {
        char *slash = strrchr(exe, '\\');
        if (slash) { slash[1] = '\0'; snprintf(out, cap, "%snssckbi.dll", exe); }
    }
#else
    (void)cap;
#endif
}

static int is_hex64(const char *s)
{
    if (strlen(s) != 64) return 0;
    for (; *s; s++) if (!isxdigit((unsigned char)*s)) return 0;
    return 1;
}

static int usage(void)
{
    fprintf(stderr, "usage: rfxfetch <https-url> <out-file> [--sha256 HEX] [--size N] [--max-bytes N]\n"
                    "                [--roots MODULE] [--ca PEM] [--timeout SECONDS] [--quiet]\n");
    return 3;
}

int main(int argc, char **argv)
{
    const char *url = NULL, *out = NULL, *want_sha = NULL, *roots = NULL, *ca = NULL;
    long long want_size = -1, max = 64LL * 1024 * 1024;
    int timeout = 30, rc = 1, verify_fail = 0;
    char part[1100], rootbuf[1100], hex[65];
    unsigned char digest[32];
    unsigned int dlen = 0;
    url_t u, next;
    sink_t k;
    memset(&k, 0, sizeof(k));

    for (int i = 1; i < argc; i++) {
        const char *a = argv[i];
        int more = i + 1 < argc;
        if (!strcmp(a, "--sha256") && more) want_sha = argv[++i];
        else if (!strcmp(a, "--size") && more) want_size = strtoll(argv[++i], NULL, 10);
        else if (!strcmp(a, "--max-bytes") && more) max = strtoll(argv[++i], NULL, 10);
        else if (!strcmp(a, "--roots") && more) roots = argv[++i];
        else if (!strcmp(a, "--ca") && more) ca = argv[++i];
        else if (!strcmp(a, "--timeout") && more) timeout = atoi(argv[++i]);
        else if (!strcmp(a, "--quiet")) g_quiet = 1;
        else if (a[0] == '-' && a[1] == '-') return usage();
        else if (!url) url = a;
        else if (!out) out = a;
        else return usage();
    }
    if (!url || !out || max <= 0 || timeout <= 0) return usage();
    if (want_sha && !is_hex64(want_sha)) { fprintf(stderr, "--sha256 needs 64 hex characters\n"); return 3; }
    if (want_size > max) max = want_size;
    if (strlen(out) + 6 > sizeof(part)) return usage();
    snprintf(part, sizeof(part), "%s.part", out);
    g_timeout = PR_SecondsToInterval((PRUint32)timeout);

    if (parse_url(url, &u) < 0) goto done;
    PR_Init(PR_USER_THREAD, PR_PRIORITY_NORMAL, 1);
    if (NSS_NoDB_Init(NULL) != SECSuccess) { fail_nspr("starting NSS", ""); goto done; }
    if (NSS_SetDomesticPolicy() != SECSuccess) { fail_nspr("NSS policy", ""); goto shutdown; }
    {
        SSLVersionRange range = {SSL_LIBRARY_VERSION_TLS_1_2, SSL_LIBRARY_VERSION_TLS_1_3};
        SSL_VersionRangeSetDefault(ssl_variant_stream, &range);
    }
    if (!roots) { default_roots(rootbuf, sizeof(rootbuf)); if (rootbuf[0]) roots = rootbuf; }
    if (roots && load_roots(roots) < 0) goto shutdown;
    if (ca && add_ca(ca) < 0) goto shutdown;

    k.f = fopen(part, "wb");
    if (!k.f) { fail("can't create %s: %s", part, strerror(errno)); goto shutdown; }
    k.sha = PK11_CreateDigestContext(SEC_OID_SHA256);
    PK11_DigestBegin(k.sha);
    k.max = max;
    k.total = -1;

    for (int hop = 0;; hop++) {
        int r = fetch_once(&u, &k, &next);
        if (r < 0) goto cleanup;
        if (r == 0) break;
        if (hop >= MAX_REDIRECTS) { fail("too many redirects%s", NULL, NULL); goto cleanup; }
        if (k.got) { fail("redirect after data%s", NULL, NULL); goto cleanup; }
        u = next;
    }
    PK11_DigestFinal(k.sha, digest, &dlen, sizeof(digest));
    for (int i = 0; i < 32; i++) snprintf(hex + 2 * i, 3, "%02x", digest[i]);
    if (fclose(k.f) != 0) { k.f = NULL; fail("can't write the file%s", NULL, NULL); goto cleanup; }
    k.f = NULL;
    if (want_size >= 0 && k.got != want_size) {
        char a[32], b[32];
        snprintf(a, sizeof(a), "%lld", k.got); snprintf(b, sizeof(b), "%lld", want_size);
        fail("size mismatch: got %s bytes, expected %s", a, b);
        verify_fail = 1;
        goto cleanup;
    }
    if (want_sha && strcasecmp(hex, want_sha) != 0) {
        fail("SHA-256 mismatch: got %s, expected %s", hex, want_sha);
        verify_fail = 1;
        goto cleanup;
    }
    remove(out);
    if (rename(part, out) != 0) { fail("can't rename to %s: %s", out, strerror(errno)); goto cleanup; }
    if (!g_quiet) printf("PROGRESS %lld %lld\n", k.got, k.got);
    printf("OK %s %lld\n", hex, k.got);
    rc = 0;
cleanup:
    if (k.f) fclose(k.f);
    if (k.sha) PK11_DestroyContext(k.sha, PR_TRUE);
    if (rc != 0) remove(part);
shutdown:
    SSL_ClearSessionCache();
    if (g_roots) SECMOD_DestroyModule(g_roots);
    NSS_Shutdown();
done:
    if (rc != 0) {
        printf("ERROR %s\n", g_err[0] ? g_err : "failed");
        rc = verify_fail ? 2 : 1;
    }
    fflush(stdout);
    return rc;
}
