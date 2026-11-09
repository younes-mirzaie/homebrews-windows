/* Network connections over NSPR with NSS TLS (implicit TLS or STARTTLS), certificate pinning,
   and small crypto helpers (hashing, random, sealing secrets). */
#ifdef _WIN32
#include <winsock2.h>   /* before windows.h */
#include <ws2tcpip.h>
#else
#include <sys/types.h>
#include <sys/socket.h>
#include <netdb.h>
#include <netinet/in.h>
#endif
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"

#include "nspr.h"
#include "nss.h"
#include "pk11pub.h"
#include "secmod.h"
#include "ssl.h"
#include "sslproto.h"
#include "secerr.h"
#include "sslerr.h"
#include "cert.h"
#include "pkcs11t.h"
#include "pkcs11n.h"

#ifdef _WIN32
#include <windows.h>
#endif

struct conn {
    PRFileDesc *fd;
    int tls;
    char host[256];
    char pin[72];
    char certfp[65];
    int cert_err;
    char err[384];
    char tlsinfo[96];
    char rbuf[16384];
    size_t rstart, rend;
    PRIntervalTime timeout;
};

static SECMODModule *g_roots;

static const char *err_text(PRErrorCode e)
{
    switch (e) {
    case PR_CONNECT_REFUSED_ERROR: return "connection refused";
    case PR_IO_TIMEOUT_ERROR: return "timed out";
    case PR_CONNECT_RESET_ERROR: return "connection reset";
    case PR_NETWORK_UNREACHABLE_ERROR: return "network unreachable (is Wi-Fi or data on?)";
    case PR_HOST_UNREACHABLE_ERROR: return "server unreachable";
    case PR_DIRECTORY_LOOKUP_ERROR: return "server name not found";
    case PR_END_OF_FILE_ERROR: return "server closed the connection";
    case SSL_ERROR_BAD_CERT_DOMAIN: return "certificate is for a different server name";
    case SEC_ERROR_EXPIRED_CERTIFICATE: return "certificate expired";
    case SEC_ERROR_UNKNOWN_ISSUER: return "certificate issuer unknown";
    case SEC_ERROR_CA_CERT_INVALID: return "certificate is self-signed or its issuer is invalid";
    case SEC_ERROR_UNTRUSTED_ISSUER: return "certificate issuer not trusted";
    case SSL_ERROR_NO_CYPHER_OVERLAP: return "server only offers old encryption (no common cipher)";
    case SSL_ERROR_UNSUPPORTED_VERSION: return "server only supports an old TLS version";
    case SSL_ERROR_PROTOCOL_VERSION_ALERT: return "server rejected the TLS version";
    case SSL_ERROR_RX_RECORD_TOO_LONG: return "server isn't speaking TLS on this port (try STARTTLS)";
    default: {
        const char *n = PORT_ErrorToName(e);
        return n ? n : "network error";
    }
    }
}

static void set_err(conn *c, const char *what)
{
    PRErrorCode e = PR_GetError();
    if (c->cert_err) {
        /* "CERT|<fingerprint>|text": the app offers to trust this certificate */
        snprintf(c->err, sizeof(c->err), "CERT|%s|%s: %s", c->certfp, c->host, err_text(c->cert_err));
        return;
    }
    snprintf(c->err, sizeof(c->err), "%s %s: %s", what, c->host, err_text(e));
}

int net_init(const char *roots_module)
{
    char spec[1200];
    SSLVersionRange range = {SSL_LIBRARY_VERSION_TLS_1_2, SSL_LIBRARY_VERSION_TLS_1_3};
    PR_Init(PR_USER_THREAD, PR_PRIORITY_NORMAL, 1);
    if (NSS_NoDB_Init(NULL) != SECSuccess) {
        rb_log("NSS_NoDB_Init failed: %s", err_text(PR_GetError()));
        return -1;
    }
    if (roots_module && *roots_module) {
        char esc[1024];
        size_t n = 0;
        for (const char *p = roots_module; *p && n + 2 < sizeof(esc); p++) {
            if (*p == '\\') esc[n++] = '\\';   /* module specs treat '\' as an escape */
            esc[n++] = *p;
        }
        esc[n] = '\0';
        snprintf(spec, sizeof(spec), "name=\"Root Certs\" library=\"%s\"", esc);
        g_roots = SECMOD_LoadUserModule(spec, NULL, PR_FALSE);
        if (!g_roots || !g_roots->loaded) {
            rb_log("can't load root store %s: %s", roots_module, err_text(PR_GetError()));
            g_roots = NULL;
        }
    }
    if (NSS_SetDomesticPolicy() != SECSuccess) return -1;
    SSL_VersionRangeSetDefault(ssl_variant_stream, &range);
    SSL_OptionSetDefault(SSL_ENABLE_SESSION_TICKETS, PR_TRUE);
    return 0;
}

void net_shutdown(void)
{
    SSL_ClearSessionCache();
    if (g_roots) SECMOD_DestroyModule(g_roots);
    NSS_Shutdown();
}

void sha256_hex(const void *data, size_t n, char out[65])
{
    unsigned char h[32];
    buf_t b;
    buf_init(&b);
    PK11_HashBuf(SEC_OID_SHA256, h, (const unsigned char *)data, (PRInt32)n);
    hex_encode(&b, h, 32);
    memcpy(out, b.p, 65);
    buf_free(&b);
}

int random_bytes(void *out, size_t n)
{
    return PK11_GenerateRandom((unsigned char *)out, (int)n) == SECSuccess ? 0 : -1;
}

static SECStatus auth_cert(void *arg, PRFileDesc *fd, PRBool checksig, PRBool isserver)
{
    conn *c = arg;
    CERTCertificate *cert = SSL_PeerCertificate(fd);
    SECStatus rv;
    if (cert) {
        sha256_hex(cert->derCert.data, cert->derCert.len, c->certfp);
        CERT_DestroyCertificate(cert);
    }
    rv = SSL_AuthCertificate(CERT_GetDefaultCertDB(), fd, checksig, isserver);
    if (rv != SECSuccess) {
        if (c->pin[0] && cert && strieq(c->pin, c->certfp)) {
            PR_SetError(0, 0);
            return SECSuccess;   /* the user chose to trust exactly this certificate */
        }
        c->cert_err = PR_GetError();
    }
    return rv;
}

static int start_tls(conn *c)
{
    PRFileDesc *ssl = SSL_ImportFD(NULL, c->fd);
    SSLChannelInfo ch;
    SSLCipherSuiteInfo su;
    if (!ssl) { set_err(c, "TLS with"); return -1; }
    c->fd = ssl;
    SSL_SetURL(ssl, c->host);
    SSL_OptionSet(ssl, SSL_HANDSHAKE_AS_CLIENT, PR_TRUE);
    SSL_AuthCertificateHook(ssl, auth_cert, c);
    SSL_ResetHandshake(ssl, PR_FALSE);
    if (SSL_ForceHandshakeWithTimeout(ssl, c->timeout) != SECSuccess) {
        set_err(c, "TLS with");
        return -1;
    }
    c->tls = 1;
    if (SSL_GetChannelInfo(ssl, &ch, sizeof(ch)) == SECSuccess &&
        SSL_GetCipherSuiteInfo(ch.cipherSuite, &su, sizeof(su)) == SECSuccess) {
        const char *v = ch.protocolVersion == SSL_LIBRARY_VERSION_TLS_1_3 ? "TLS 1.3" :
                        ch.protocolVersion == SSL_LIBRARY_VERSION_TLS_1_2 ? "TLS 1.2" : "TLS";
        snprintf(c->tlsinfo, sizeof(c->tlsinfo), "%s %s", v, su.cipherSuiteName);
    }
    return 0;
}

/* Host name to addresses. NSPR's lookup failed inside the phone app ("can't find server"), so
   this tries, in order: an IP literal, the system's getaddrinfo directly (Winsock on the phone),
   then NSPR's PR_GetAddrInfoByName and PR_GetHostByName. Returns how many addresses it found;
   on 0, err says what each method answered. */
static int resolve(const char *host, int port, PRNetAddr *out, int max, char *err, size_t errlen)
{
    int n = 0;
    char why[200] = "";
    PRNetAddr a;
    if (PR_StringToNetAddr(host, &a) == PR_SUCCESS) {
        a.inet.port = PR_htons((PRUint16)port);   /* same offset for inet and ipv6 */
        out[0] = a;
        return 1;
    }
    {
        struct addrinfo hints, *res = NULL, *p;
        int rc;
#ifdef _WIN32
        static int wsa;
        if (!wsa) { WSADATA d; wsa = WSAStartup(MAKEWORD(2, 2), &d) == 0 ? 1 : -1; }
#endif
        memset(&hints, 0, sizeof(hints));
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;
        hints.ai_protocol = IPPROTO_TCP;
        rc = getaddrinfo(host, NULL, &hints, &res);
        for (p = rc == 0 ? res : NULL; p && n < max; p = p->ai_next) {
            if (p->ai_family == AF_INET) {
                const struct sockaddr_in *sin = (const struct sockaddr_in *)p->ai_addr;
                PR_SetNetAddr(PR_IpAddrNull, PR_AF_INET, (PRUint16)port, &out[n]);
                out[n++].inet.ip = sin->sin_addr.s_addr;
            } else if (p->ai_family == AF_INET6) {
                const struct sockaddr_in6 *sin6 = (const struct sockaddr_in6 *)p->ai_addr;
                PR_SetNetAddr(PR_IpAddrNull, PR_AF_INET6, (PRUint16)port, &out[n]);
                memcpy(&out[n++].ipv6.ip, &sin6->sin6_addr, 16);
            }
        }
        if (res) freeaddrinfo(res);
        if (n) return n;
#ifdef _WIN32
        snprintf(why, sizeof(why), "getaddrinfo %d", rc ? WSAGetLastError() : 0);
#else
        snprintf(why, sizeof(why), "getaddrinfo: %s", rc ? gai_strerror(rc) : "no addresses");
#endif
    }
    {
        PRAddrInfo *ai = PR_GetAddrInfoByName(host, PR_AF_UNSPEC, PR_AI_ADDRCONFIG);
        void *it = NULL;
        if (ai) {
            while (n < max && (it = PR_EnumerateAddrInfo(it, ai, (PRUint16)port, &out[n])) != NULL) n++;
            PR_FreeAddrInfo(ai);
            if (n) return n;
        }
        snprintf(why + strlen(why), sizeof(why) - strlen(why), "; NSPR %d", (int)PR_GetError());
    }
    {
        char buf[PR_NETDB_BUF_SIZE];
        PRHostEnt he;
        if (PR_GetHostByName(host, buf, sizeof(buf), &he) == PR_SUCCESS) {
            int i = 0;
            while (n < max && (i = PR_EnumerateHostEnt(i, &he, (PRUint16)port, &out[n])) > 0) n++;
            if (n) return n;
        }
        snprintf(why + strlen(why), sizeof(why) - strlen(why), "; hostbyname %d", (int)PR_GetError());
    }
    snprintf(err, errlen, "can't find server %s (check the name and your connection; %s)", host, why);
    rb_log("lookup %s failed: %s", host, why);
    return 0;
}

conn *conn_open(const char *host, int port, int tls, const char *pin, char *err, size_t errlen)
{
    conn *c = xcalloc(1, sizeof(*c));
    PRNetAddr addrs[16];
    int naddr, connected = 0;
    snprintf(c->host, sizeof(c->host), "%s", host);
    snprintf(c->pin, sizeof(c->pin), "%s", pin ? pin : "");
    c->timeout = PR_SecondsToInterval(60);
    naddr = resolve(host, port, addrs, 16, err, errlen);
    if (!naddr) { free(c); return NULL; }
    for (int i = 0; i < naddr && !connected; i++) {
        c->fd = PR_OpenTCPSocket(addrs[i].raw.family);
        if (!c->fd) continue;
        if (PR_Connect(c->fd, &addrs[i], PR_SecondsToInterval(20)) == PR_SUCCESS) { connected = 1; break; }
        set_err(c, "connecting to");
        PR_Close(c->fd);
        c->fd = NULL;
    }
    if (!connected) {
        if (!c->err[0]) snprintf(c->err, sizeof(c->err), "can't connect to %s:%d", host, port);
        snprintf(err, errlen, "%s", c->err);
        free(c);
        return NULL;
    }
    {
        PRSocketOptionData o;
        o.option = PR_SockOpt_NoDelay;
        o.value.no_delay = PR_TRUE;
        PR_SetSocketOption(c->fd, &o);
    }
    if (tls == SEC_TLS && start_tls(c) != 0) {
        snprintf(err, errlen, "%s", c->err);
        conn_close(c);
        return NULL;
    }
    return c;
}

int conn_starttls(conn *c, char *err, size_t errlen)
{
    c->rstart = c->rend = 0;   /* nothing read before the handshake may be trusted */
    if (start_tls(c) != 0) {
        snprintf(err, errlen, "%s", c->err);
        return -1;
    }
    return 0;
}

void conn_set_timeout(conn *c, int seconds) { c->timeout = PR_SecondsToInterval((PRUint32)seconds); }

int conn_write(conn *c, const void *data, size_t n)
{
    const char *p = data;
    while (n > 0) {
        PRInt32 w = PR_Send(c->fd, p, (PRInt32)(n > 65536 ? 65536 : n), 0, c->timeout);
        if (w <= 0) { set_err(c, "sending to"); return -1; }
        p += w;
        n -= (size_t)w;
    }
    return 0;
}

int conn_puts(conn *c, const char *s) { return conn_write(c, s, strlen(s)); }

static int fill(conn *c)
{
    PRInt32 n;
    if (c->rstart == c->rend) c->rstart = c->rend = 0;
    if (c->rend == sizeof(c->rbuf)) {
        memmove(c->rbuf, c->rbuf + c->rstart, c->rend - c->rstart);
        c->rend -= c->rstart;
        c->rstart = 0;
    }
    n = PR_Recv(c->fd, c->rbuf + c->rend, (PRInt32)(sizeof(c->rbuf) - c->rend), 0, c->timeout);
    if (n <= 0) {
        if (n == 0) PR_SetError(PR_END_OF_FILE_ERROR, 0);
        set_err(c, "reading from");
        return -1;
    }
    c->rend += (size_t)n;
    return 0;
}

int conn_readline(conn *c, buf_t *line)
{
    buf_clear(line);
    for (;;) {
        char *nl = memchr(c->rbuf + c->rstart, '\n', c->rend - c->rstart);
        if (nl) {
            size_t l = (size_t)(nl - (c->rbuf + c->rstart));
            buf_add(line, c->rbuf + c->rstart, l);
            c->rstart += l + 1;
            if (line->len && line->p[line->len - 1] == '\r') line->p[--line->len] = '\0';
            if (!line->p) buf_adds(line, "");
            return 0;
        }
        buf_add(line, c->rbuf + c->rstart, c->rend - c->rstart);
        c->rstart = c->rend;
        if (line->len > 16 * 1024 * 1024) { snprintf(c->err, sizeof(c->err), "line too long from %s", c->host); return -1; }
        if (fill(c) != 0) return -1;
    }
}

int conn_read(conn *c, buf_t *out, size_t n)
{
    while (n > 0) {
        size_t avail = c->rend - c->rstart;
        if (!avail) {
            if (fill(c) != 0) return -1;
            continue;
        }
        if (avail > n) avail = n;
        buf_add(out, c->rbuf + c->rstart, avail);
        c->rstart += avail;
        n -= avail;
    }
    return 0;
}

void conn_close(conn *c)
{
    if (!c) return;
    if (c->fd) PR_Close(c->fd);
    free(c);
}

const char *conn_error(conn *c) { return c->err; }
const char *conn_tls_info(conn *c) { return c->tlsinfo; }

/* ---------------- secrets at rest ---------------- */

static PK11SymKey *secret_key(const char *token, CK_ATTRIBUTE_TYPE op)
{
    unsigned char k[32];
    buf_t seed;
    SECItem item;
    PK11SlotInfo *slot = PK11_GetInternalSlot();
    PK11SymKey *key;
    buf_init(&seed);
    buf_adds(&seed, "retrobird-secret-v1:");
    buf_adds(&seed, token ? token : "");
    PK11_HashBuf(SEC_OID_SHA256, k, (unsigned char *)seed.p, (PRInt32)seed.len);
    buf_free(&seed);
    item.type = siBuffer;
    item.data = k;
    item.len = 32;
    key = PK11_ImportSymKey(slot, CKM_AES_GCM, PK11_OriginUnwrap, op, &item, NULL);
    PK11_FreeSlot(slot);
    memset(k, 0, sizeof(k));
    return key;
}

char *secret_seal(const char *token, const char *plain)
{
    unsigned char iv[12];
    CK_NSS_GCM_PARAMS gcm;
    SECItem param;
    PK11SymKey *key = secret_key(token, CKA_ENCRYPT);
    size_t pl = strlen(plain);
    unsigned char *out = xmalloc(pl + 16);
    unsigned int outlen = 0;
    buf_t hex;
    if (!key) { free(out); return NULL; }
    random_bytes(iv, sizeof(iv));
    memset(&gcm, 0, sizeof(gcm));
    gcm.pIv = iv;
    gcm.ulIvLen = sizeof(iv);
    gcm.ulTagBits = 128;
    param.type = siBuffer;
    param.data = (unsigned char *)&gcm;
    param.len = sizeof(gcm);
    if (PK11_Encrypt(key, CKM_AES_GCM, &param, out, &outlen, (unsigned int)(pl + 16),
                     (const unsigned char *)plain, (unsigned int)pl) != SECSuccess) {
        rb_log("seal failed: %s", err_text(PR_GetError()));
        PK11_FreeSymKey(key);
        free(out);
        return NULL;
    }
    PK11_FreeSymKey(key);
    buf_init(&hex);
    hex_encode(&hex, iv, sizeof(iv));
    hex_encode(&hex, out, outlen);
    free(out);
    return buf_detach(&hex);
}

char *secret_open(const char *token, const char *sealed)
{
    buf_t raw;
    CK_NSS_GCM_PARAMS gcm;
    SECItem param;
    PK11SymKey *key;
    unsigned char *out;
    unsigned int outlen = 0;
    char *r = NULL;
    buf_init(&raw);
    if (!sealed || hex_decode(&raw, sealed) != 0 || raw.len < 12 + 16) { buf_free(&raw); return NULL; }
    key = secret_key(token, CKA_DECRYPT);
    if (!key) { buf_free(&raw); return NULL; }
    memset(&gcm, 0, sizeof(gcm));
    gcm.pIv = (unsigned char *)raw.p;
    gcm.ulIvLen = 12;
    gcm.ulTagBits = 128;
    param.type = siBuffer;
    param.data = (unsigned char *)&gcm;
    param.len = sizeof(gcm);
    out = xmalloc(raw.len);
    if (PK11_Decrypt(key, CKM_AES_GCM, &param, out, &outlen, (unsigned int)raw.len,
                     (unsigned char *)raw.p + 12, (unsigned int)(raw.len - 12)) == SECSuccess)
        r = xstrndup((char *)out, outlen);
    PK11_FreeSymKey(key);
    free(out);
    buf_free(&raw);
    return r;
}
