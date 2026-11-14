/* Microsoft sign-in for Outlook.com: the OAuth 2.0 device code flow (the app shows a short code,
   the user enters it at microsoft.com/link in any browser), refresh tokens, and the XOAUTH2 string
   IMAP and SMTP log in with. Talks only to login.microsoftonline.com over TLS.
   RB_OAUTH_SERVER=host:port (plain HTTP) points it at a test server instead. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"

#define MS_HOST "login.microsoftonline.com"
#define MS_TENANT "/consumers"
#define MS_SCOPE "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access openid profile"

volatile int g_store_dirty;

/* ---------------- JSON (flat objects are all we need) ---------------- */

static const char *skip_ws(const char *p)
{
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
    return p;
}

static void put_utf8(buf_t *b, unsigned cp)
{
    if (cp < 0x80) buf_addc(b, (int)cp);
    else if (cp < 0x800) { buf_addc(b, 0xC0 | (cp >> 6)); buf_addc(b, 0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) {
        buf_addc(b, 0xE0 | (cp >> 12)); buf_addc(b, 0x80 | ((cp >> 6) & 0x3F)); buf_addc(b, 0x80 | (cp & 0x3F));
    } else {
        buf_addc(b, 0xF0 | (cp >> 18)); buf_addc(b, 0x80 | ((cp >> 12) & 0x3F));
        buf_addc(b, 0x80 | ((cp >> 6) & 0x3F)); buf_addc(b, 0x80 | (cp & 0x3F));
    }
}

/* Reads a JSON string at p (on the opening quote); returns the position after it, or NULL. */
static const char *read_str(const char *p, buf_t *out)
{
    if (*p != '"') return NULL;
    for (p++; *p && *p != '"'; p++) {
        if (*p != '\\') { if (out) buf_addc(out, *p); continue; }
        p++;
        if (!*p) return NULL;
        if (*p == 'u') {
            unsigned cp = 0;
            for (int i = 1; i <= 4; i++) {
                char c = p[i];
                if (!c) return NULL;
                cp = cp * 16 + (unsigned)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0');
            }
            p += 4;
            if (cp >= 0xD800 && cp < 0xDC00 && p[1] == '\\' && p[2] == 'u') {
                unsigned lo = 0;
                for (int i = 3; i <= 6; i++) {
                    char c = p[i];
                    if (!c) return NULL;
                    lo = lo * 16 + (unsigned)(c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0');
                }
                if (lo >= 0xDC00 && lo < 0xE000) { cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00); p += 6; }
            }
            if (out) put_utf8(out, cp);
        } else if (out) {
            char c = *p;
            buf_addc(out, c == 'n' ? '\n' : c == 't' ? '\t' : c == 'r' ? '\r' : c == 'b' ? '\b' : c == 'f' ? '\f' : c);
        }
    }
    return *p == '"' ? p + 1 : NULL;
}

/* Skips any JSON value; returns the position after it, or NULL. */
static const char *skip_value(const char *p)
{
    p = skip_ws(p);
    if (*p == '"') return read_str(p, NULL);
    if (*p == '{' || *p == '[') {
        int depth = 0;
        for (; *p; p++) {
            if (*p == '"') { p = read_str(p, NULL); if (!p) return NULL; p--; continue; }
            if (*p == '{' || *p == '[') depth++;
            else if ((*p == '}' || *p == ']') && --depth == 0) return p + 1;
        }
        return NULL;
    }
    while (*p && *p != ',' && *p != '}' && *p != ']') p++;
    return p;
}

char *json_get(const char *json, const char *key)
{
    const char *p = skip_ws(json ? json : "");
    if (*p != '{') return NULL;
    p++;
    for (;;) {
        buf_t k;
        int match;
        p = skip_ws(p);
        if (*p != '"') return NULL;
        buf_init(&k);
        p = read_str(p, &k);
        match = p && !strcmp(buf_str(&k), key);
        buf_free(&k);
        if (!p) return NULL;
        p = skip_ws(p);
        if (*p != ':') return NULL;
        p = skip_ws(p + 1);
        if (match) {
            buf_t v;
            buf_init(&v);
            if (*p == '"') {
                if (!read_str(p, &v)) { buf_free(&v); return NULL; }
                if (!v.p) buf_adds(&v, "");
                return buf_detach(&v);
            }
            if (*p == '{' || *p == '[' || !strncmp(p, "null", 4)) return NULL;
            {
                const char *e = skip_value(p);
                if (!e) return NULL;
                while (e > p && (e[-1] == ' ' || e[-1] == '\n' || e[-1] == '\r' || e[-1] == '\t')) e--;
                return xstrndup(p, (size_t)(e - p));
            }
        }
        p = skip_value(p);
        if (!p) return NULL;
        p = skip_ws(p);
        if (*p == ',') { p++; continue; }
        return NULL;
    }
}

/* ---------------- HTTPS POST ---------------- */

static void form_add(buf_t *b, const char *k, const char *v)
{
    static const char hex[] = "0123456789ABCDEF";
    if (b->len) buf_addc(b, '&');
    buf_adds(b, k);
    buf_addc(b, '=');
    for (const unsigned char *p = (const unsigned char *)v; *p; p++) {
        if ((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') ||
            *p == '-' || *p == '_' || *p == '.' || *p == '~') buf_addc(b, *p);
        else { buf_addc(b, '%'); buf_addc(b, hex[*p >> 4]); buf_addc(b, hex[*p & 15]); }
    }
}

/* POSTs a form to the Microsoft sign-in server; *status gets the HTTP status and body the
   response body. 0, or -1 with err when the server couldn't be reached. */
static int post_form(const char *path, const char *form, int *status, buf_t *body, char *err, size_t errlen)
{
    char host[256] = MS_HOST;
    int port = 443, sec = SEC_TLS, chunked = 0;
    long clen = -1;
    const char *test = getenv("RB_OAUTH_SERVER");
    conn *c;
    buf_t req, line;
    if (test && *test) {
        const char *colon = strrchr(test, ':');
        snprintf(host, sizeof(host), "%.*s", colon ? (int)(colon - test) : (int)strlen(test), test);
        port = colon ? atoi(colon + 1) : 80;
        sec = SEC_PLAIN;
    }
    *status = 0;
    buf_clear(body);
    c = conn_open(host, port, sec, NULL, err, errlen);
    if (!c) return -1;
    conn_set_timeout(c, 30);
    buf_init(&req);
    buf_init(&line);
    buf_printf(&req, "POST %s HTTP/1.1\r\nHost: %s\r\nUser-Agent: Retrobird\r\nAccept: application/json\r\n"
               "Content-Type: application/x-www-form-urlencoded\r\nContent-Length: %u\r\nConnection: close\r\n\r\n%s",
               path, host, (unsigned)strlen(form), form);
    if (conn_write(c, req.p, req.len) != 0 || conn_readline(c, &line) != 0) goto ioerr;
    if (strncmp(buf_str(&line), "HTTP/1.", 7) || line.len < 12) {
        snprintf(err, errlen, "Microsoft's sign-in server sent something unexpected");
        goto fail;
    }
    *status = atoi(line.p + 9);
    for (;;) {
        if (conn_readline(c, &line) != 0) goto ioerr;
        if (!line.len) break;
        if (starts_with_i(line.p, "content-length:")) clen = atol(line.p + 15);
        else if (starts_with_i(line.p, "transfer-encoding:") && stristr(line.p, "chunked")) chunked = 1;
    }
    if (chunked) {
        for (;;) {
            long n;
            if (conn_readline(c, &line) != 0) goto ioerr;
            n = strtol(buf_str(&line), NULL, 16);
            if (n <= 0) break;
            if (body->len + (size_t)n > 1024 * 1024 || conn_read(c, body, (size_t)n) != 0) goto ioerr;
            if (conn_readline(c, &line) != 0) goto ioerr;
        }
    } else if (clen >= 0) {
        if (clen > 1024 * 1024 || (clen > 0 && conn_read(c, body, (size_t)clen) != 0)) goto ioerr;
    } else {
        while (body->len < 1024 * 1024 && conn_read(c, body, 4096) == 0) {}
    }
    if (!body->p) buf_adds(body, "");
    conn_close(c);
    buf_free(&req);
    buf_free(&line);
    return 0;
ioerr:
    snprintf(err, errlen, "couldn't reach Microsoft's sign-in server (%s)", conn_error(c));
fail:
    conn_close(c);
    if (req.p) memset(req.p, 0, req.len);
    buf_free(&req);
    buf_free(&line);
    return -1;
}

/* Turns an OAuth error reply into a sentence. */
static void oauth_err(const char *json, char *err, size_t errlen)
{
    char *code = json_get(json, "error"), *desc = json_get(json, "error_description");
    const char *c = code ? code : "";
    if (!strcmp(c, "invalid_grant") || !strcmp(c, "interaction_required"))
        snprintf(err, errlen, "the Microsoft sign-in has expired or was removed, so sign in again");
    else if (!strcmp(c, "expired_token") || !strcmp(c, "code_expired"))
        snprintf(err, errlen, "The code expired before it was entered. Start again for a new one.");
    else if (!strcmp(c, "authorization_declined") || !strcmp(c, "access_denied"))
        snprintf(err, errlen, "The sign-in was declined on Microsoft's page.");
    else if (!strcmp(c, "invalid_client") || !strcmp(c, "unauthorized_client") || (desc && stristr(desc, "AADSTS700016")))
        snprintf(err, errlen, "Microsoft doesn't recognize this app's ID. The app registration needs checking.");
    else if (desc && stristr(desc, "AADSTS7000218"))
        snprintf(err, errlen, "The app registration needs \"Allow public client flows\" turned on.");
    else if (desc && *desc) {
        char *nl = strpbrk(desc, "\r\n");
        if (nl) *nl = '\0';
        snprintf(err, errlen, "Microsoft said: %.300s", desc);
    } else
        snprintf(err, errlen, "Microsoft's sign-in server didn't accept the request%s%s", *c ? ": " : "", c);
    free(code);
    free(desc);
}

/* ---------------- device code flow ---------------- */

int oauth_device_start(const char *client, oauth_device *d, char *err, size_t errlen)
{
    buf_t form, body;
    int status, rc = -1;
    memset(d, 0, sizeof(*d));
    if (!client || !*client) { snprintf(err, errlen, "This build has no Microsoft app ID for Outlook sign-in yet."); return -1; }
    buf_init(&form);
    buf_init(&body);
    form_add(&form, "client_id", client);
    form_add(&form, "scope", MS_SCOPE);
    if (post_form(MS_TENANT "/oauth2/v2.0/devicecode", form.p, &status, &body, err, errlen) == 0) {
        if (status == 200) {
            char *iv = json_get(body.p, "interval"), *ex = json_get(body.p, "expires_in");
            d->device_code = json_get(body.p, "device_code");
            d->user_code = json_get(body.p, "user_code");
            d->verify_uri = json_get(body.p, "verification_uri");
            d->interval = iv ? atoi(iv) : 5;
            d->expires_in = ex ? atoi(ex) : 900;
            free(iv);
            free(ex);
            if (d->interval < 1) d->interval = 5;
            if (d->device_code && d->user_code) {
                if (!d->verify_uri) d->verify_uri = xstrdup("https://microsoft.com/link");
                rc = 0;
            } else {
                snprintf(err, errlen, "Microsoft's sign-in server sent an incomplete reply");
                oauth_device_free(d);
            }
        } else {
            oauth_err(body.p, err, errlen);
        }
    }
    buf_free(&form);
    buf_free(&body);
    return rc;
}

void oauth_device_free(oauth_device *d)
{
    free(d->device_code);
    free(d->user_code);
    free(d->verify_uri);
    memset(d, 0, sizeof(*d));
}

/* The signed-in address from the id_token (a JWT): preferred_username or email. */
static char *id_token_email(const char *jwt)
{
    const char *a = jwt ? strchr(jwt, '.') : NULL, *b = a ? strchr(a + 1, '.') : NULL;
    buf_t s, raw;
    char *email;
    if (!b) return NULL;
    buf_init(&s);
    buf_init(&raw);
    for (const char *p = a + 1; p < b; p++) buf_addc(&s, *p == '-' ? '+' : *p == '_' ? '/' : *p);
    while (s.len % 4) buf_addc(&s, '=');
    b64_decode(&raw, s.p, s.len);
    email = json_get(raw.p, "email");
    if (!email || !strchr(email, '@')) { free(email); email = json_get(raw.p, "preferred_username"); }
    if (email && !strchr(email, '@')) { free(email); email = NULL; }
    buf_free(&s);
    buf_free(&raw);
    return email;
}

/* Token endpoint call; on success sets *access, *refresh (if sent), *exp_in and *email (if asked). */
static int token_call(const char *form, char **access, char **refresh, long *exp_in, char **email,
                      int *pending, char *err, size_t errlen)
{
    buf_t body;
    int status, rc = -1;
    buf_init(&body);
    if (pending) *pending = 0;
    if (post_form(MS_TENANT "/oauth2/v2.0/token", form, &status, &body, err, errlen) == 0) {
        if (status == 200) {
            char *ex = json_get(body.p, "expires_in");
            *access = json_get(body.p, "access_token");
            *refresh = json_get(body.p, "refresh_token");
            *exp_in = ex ? atol(ex) : 3600;
            free(ex);
            if (email) {
                char *idt = json_get(body.p, "id_token");
                *email = id_token_email(idt);
                free(idt);
            }
            if (*access) rc = 0;
            else snprintf(err, errlen, "Microsoft's sign-in server sent an incomplete reply");
        } else {
            char *code = json_get(body.p, "error");
            if (pending && code && (!strcmp(code, "authorization_pending") || !strcmp(code, "slow_down"))) {
                *pending = 1;
                rc = 1;
            } else {
                oauth_err(body.p, err, errlen);
            }
            free(code);
        }
    }
    if (body.p) memset(body.p, 0, body.len);
    buf_free(&body);
    return rc;
}

int oauth_device_poll(const char *client, const char *device_code, char **refresh, char **email,
                      char *err, size_t errlen)
{
    buf_t form;
    char *access = NULL;
    long exp_in = 0;
    int pending = 0, rc;
    *refresh = *email = NULL;
    buf_init(&form);
    form_add(&form, "grant_type", "urn:ietf:params:oauth:grant-type:device_code");
    form_add(&form, "client_id", client ? client : "");
    form_add(&form, "device_code", device_code ? device_code : "");
    rc = token_call(form.p, &access, refresh, &exp_in, email, &pending, err, errlen);
    buf_free(&form);
    if (access) { memset(access, 0, strlen(access)); free(access); }
    if (rc == 0 && !*refresh) {
        free(*email);
        *email = NULL;
        snprintf(err, errlen, "Microsoft didn't allow staying signed in (offline_access is missing from the app registration)");
        return -1;
    }
    return rc;
}

int oauth_ensure(account *a, char *err, size_t errlen)
{
    buf_t form;
    char *access = NULL, *refresh = NULL;
    long exp_in = 0;
    int rc;
    if (a->auth != AUTH_MICROSOFT) return 0;
    if (a->access && time(NULL) < a->access_exp - 120) return 0;
    if (!a->pass || !*a->pass) {
        snprintf(err, errlen, "not signed in to Microsoft yet (edit the account to sign in)");
        return -1;
    }
    buf_init(&form);
    form_add(&form, "grant_type", "refresh_token");
    form_add(&form, "client_id", a->oauth_client ? a->oauth_client : "");
    form_add(&form, "refresh_token", a->pass);
    form_add(&form, "scope", MS_SCOPE);
    rc = token_call(form.p, &access, &refresh, &exp_in, NULL, NULL, err, errlen);
    memset(form.p, 0, form.len);
    buf_free(&form);
    if (rc != 0) return -1;
    if (a->access) { memset(a->access, 0, strlen(a->access)); free(a->access); }
    a->access = access;
    a->access_exp = time(NULL) + (exp_in > 0 ? exp_in : 3600);
    if (refresh && strcmp(refresh, a->pass)) {
        memset(a->pass, 0, strlen(a->pass));
        free(a->pass);
        a->pass = refresh;
        g_store_dirty = 1;
    } else if (refresh) {
        free(refresh);
    }
    return 0;
}

char *oauth_xoauth2(const char *user, const char *token)
{
    buf_t raw, b64;
    buf_init(&raw);
    buf_init(&b64);
    buf_printf(&raw, "user=%s\001auth=Bearer %s\001\001", user ? user : "", token ? token : "");
    b64_encode(&b64, raw.p, raw.len, 0);
    memset(raw.p, 0, raw.len);
    buf_free(&raw);
    if (!b64.p) buf_adds(&b64, "");
    return buf_detach(&b64);
}
