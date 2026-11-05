/* SMTP submission: implicit TLS (465) or STARTTLS (587), AUTH PLAIN / LOGIN, dot-stuffed DATA. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"

typedef struct {
    conn *c;
    buf_t ehlo;      /* capability lines from the last EHLO */
    char err[512];
} smtp;

/* Reads a (multi-line) reply; returns the code or -1. The text goes to *text if given. */
static int reply(smtp *s, buf_t *text)
{
    buf_t line;
    int code = -1;
    buf_init(&line);
    if (text) buf_clear(text);
    for (;;) {
        if (conn_readline(s->c, &line) != 0) {
            snprintf(s->err, sizeof(s->err), "%s", conn_error(s->c));
            buf_free(&line);
            return -1;
        }
        if (rb_debug) rb_log("smtp S: %.300s", buf_str(&line));
        if (line.len < 3) continue;
        code = atoi(line.p);
        if (text) {
            buf_add(text, line.len > 4 ? line.p + 4 : "", line.len > 4 ? line.len - 4 : 0);
            buf_addc(text, '\n');
        }
        if (line.len == 3 || line.p[3] != '-') break;
    }
    buf_free(&line);
    return code;
}

static int cmd(smtp *s, buf_t *text, const char *fmt, ...) RB_PRINTF(3, 4);
static int cmd(smtp *s, buf_t *text, const char *fmt, ...)
{
    buf_t b;
    va_list ap;
    buf_init(&b);
    va_start(ap, fmt);
    buf_vprintf(&b, fmt, ap);
    va_end(ap);
    if (rb_debug) rb_log("smtp C: %s", starts_with_i(b.p, "AUTH") ? "AUTH <hidden>" : b.p);
    buf_adds(&b, "\r\n");
    if (conn_write(s->c, b.p, b.len) != 0) {
        snprintf(s->err, sizeof(s->err), "%s", conn_error(s->c));
        buf_free(&b);
        return -1;
    }
    memset(b.p, 0, b.len);
    buf_free(&b);
    return reply(s, text);
}

static void fail_text(smtp *s, const char *what, int code, buf_t *t)
{
    char *txt = str_trim(t->p ? t->p : (char *)"");
    for (char *p = txt; *p; p++)
        if (*p == '\n') *p = ' ';
    if (code < 0) return;   /* s->err already says what went wrong */
    snprintf(s->err, sizeof(s->err), "%s: %d %.400s", what, code, txt);
}

static int has_ext(smtp *s, const char *ext)
{
    const char *p = buf_str(&s->ehlo);
    size_t l = strlen(ext);
    while (*p) {
        if (strnieq(p, ext, l) && (p[l] == ' ' || p[l] == '\n' || p[l] == '=' || !p[l])) return 1;
        p = strchr(p, '\n');
        if (!p) break;
        p++;
    }
    return 0;
}

static int auth_has(smtp *s, const char *mech)
{
    const char *p = buf_str(&s->ehlo);
    while (*p) {
        const char *e = strchr(p, '\n');
        size_t n = e ? (size_t)(e - p) : strlen(p);
        if (n > 5 && (starts_with_i(p, "AUTH ") || starts_with_i(p, "AUTH="))) {
            buf_t line;
            char k[40];
            int found;
            buf_init(&line);
            buf_addc(&line, ' ');
            buf_add(&line, p + 5, n - 5);
            buf_addc(&line, ' ');
            snprintf(k, sizeof(k), " %s ", mech);
            found = stristr(line.p, k) != NULL;
            buf_free(&line);
            if (found) return 1;
        }
        if (!e) break;
        p = e + 1;
    }
    return 0;
}

static void smtp_close(smtp *s, int quit)
{
    if (s->c && quit) {
        conn_set_timeout(s->c, 3);
        cmd(s, NULL, "QUIT");
    }
    conn_close(s->c);
    buf_free(&s->ehlo);
}

static int smtp_open(smtp *s, account *a)
{
    buf_t t;
    int code;
    const char *user = a->smtp_user && *a->smtp_user ? a->smtp_user : (a->user && *a->user ? a->user : a->email);
    memset(s, 0, sizeof(*s));
    buf_init(&t);
    s->c = conn_open(a->smtp_host, a->smtp_port, a->smtp_sec, a->smtp_pin, s->err, sizeof(s->err));
    if (!s->c) return -1;
    code = reply(s, &t);
    if (code != 220) { fail_text(s, "server greeting", code, &t); goto fail; }
    code = cmd(s, &s->ehlo, "EHLO [127.0.0.1]");
    if (code != 250) {
        code = cmd(s, &t, "HELO [127.0.0.1]");
        if (code != 250) { fail_text(s, "HELO", code, &t); goto fail; }
    }
    if (a->smtp_sec == SEC_STARTTLS) {
        if (!has_ext(s, "STARTTLS")) {
            snprintf(s->err, sizeof(s->err), "%s doesn't offer STARTTLS on port %d", a->smtp_host, a->smtp_port);
            goto fail;
        }
        code = cmd(s, &t, "STARTTLS");
        if (code != 220) { fail_text(s, "STARTTLS", code, &t); goto fail; }
        if (conn_starttls(s->c, s->err, sizeof(s->err)) != 0) goto fail;
        code = cmd(s, &s->ehlo, "EHLO [127.0.0.1]");
        if (code != 250) { fail_text(s, "EHLO", code, &s->ehlo); goto fail; }
    }
    if (a->pass && *a->pass) {
        buf_t raw, b64;
        buf_init(&raw);
        buf_init(&b64);
        if (auth_has(s, "PLAIN") || !auth_has(s, "LOGIN")) {
            buf_addc(&raw, 0);
            buf_adds(&raw, user);
            buf_addc(&raw, 0);
            buf_adds(&raw, a->pass);
            b64_encode(&b64, raw.p, raw.len, 0);
            code = cmd(s, &t, "AUTH PLAIN %s", b64.p);
        } else {
            b64_encode(&b64, user, strlen(user), 0);
            code = cmd(s, &t, "AUTH LOGIN");
            if (code == 334) code = cmd(s, &t, "%s", b64.p);
            buf_clear(&b64);
            b64_encode(&b64, a->pass, strlen(a->pass), 0);
            if (code == 334) code = cmd(s, &t, "%s", b64.p);
        }
        if (raw.p) memset(raw.p, 0, raw.len);
        if (b64.p) memset(b64.p, 0, b64.len);
        buf_free(&raw);
        buf_free(&b64);
        if (code != 235) { fail_text(s, "login failed", code, &t); goto fail; }
    }
    buf_free(&t);
    return 0;
fail:
    buf_free(&t);
    smtp_close(s, 0);
    s->c = NULL;
    return -1;
}

int smtp_test(account *a, char *err, size_t errlen)
{
    smtp s;
    if (smtp_open(&s, a) != 0) { snprintf(err, errlen, "%s", s.err); return -1; }
    smtp_close(&s, 1);
    return 0;
}

int smtp_send(account *a, const char *from, char **rcpts, int nrcpt, const char *data, size_t len,
              char *err, size_t errlen)
{
    smtp s;
    buf_t t, body;
    int code, ok = 0;
    if (nrcpt == 0) { snprintf(err, errlen, "no recipients"); return -1; }
    if (smtp_open(&s, a) != 0) { snprintf(err, errlen, "%s", s.err); return -1; }
    buf_init(&t);
    buf_init(&body);
    if (has_ext(&s, "SIZE")) code = cmd(&s, &t, "MAIL FROM:<%s> SIZE=%lu", from, (unsigned long)len);
    else code = cmd(&s, &t, "MAIL FROM:<%s>", from);
    if (code != 250) { fail_text(&s, "sender refused", code, &t); goto done; }
    for (int i = 0; i < nrcpt; i++) {
        code = cmd(&s, &t, "RCPT TO:<%s>", rcpts[i]);
        if (code != 250 && code != 251) {
            char what[300];
            snprintf(what, sizeof(what), "recipient %s refused", rcpts[i]);
            fail_text(&s, what, code, &t);
            goto done;
        }
    }
    code = cmd(&s, &t, "DATA");
    if (code != 354) { fail_text(&s, "DATA", code, &t); goto done; }
    {
        /* dot-stuff and make every line end in CRLF */
        size_t i = 0;
        int bol = 1;
        while (i < len) {
            char c = data[i++];
            if (bol && c == '.') buf_addc(&body, '.');
            bol = 0;
            if (c == '\r') continue;
            if (c == '\n') { buf_adds(&body, "\r\n"); bol = 1; continue; }
            buf_addc(&body, c);
        }
        if (!bol) buf_adds(&body, "\r\n");
        buf_adds(&body, ".\r\n");
    }
    if (conn_write(s.c, body.p, body.len) != 0) { snprintf(s.err, sizeof(s.err), "%s", conn_error(s.c)); goto done; }
    code = reply(&s, &t);
    if (code != 250) { fail_text(&s, "message refused", code, &t); goto done; }
    ok = 1;
done:
    if (!ok) snprintf(err, errlen, "%s", s.err);
    buf_free(&t);
    buf_free(&body);
    smtp_close(&s, ok);
    return ok ? 0 : -1;
}
