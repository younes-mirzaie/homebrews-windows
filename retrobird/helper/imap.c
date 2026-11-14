/* IMAP4rev1 client: response parser (with literals), login (STARTTLS, AUTH=PLAIN, LOGIN),
   folder listing with special-use roles, and incremental header sync into the local cache. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include "rb.h"

#define FETCH_ITEMS "(UID FLAGS INTERNALDATE RFC822.SIZE ENVELOPE BODY.PEEK[HEADER.FIELDS (CONTENT-TYPE)])"

/* ---------------- response parser ---------------- */

static void inode_free(inode *x)
{
    if (x->t == IN_LIST) inode_free_all(x->kids, x->n);
    free(x->s);
}

void inode_free_all(inode *list, int n)
{
    for (int i = 0; i < n; i++) inode_free(&list[i]);
    free(list);
}

static inode *push(inode **arr, int *n, int *cap)
{
    if (*n == *cap) {
        *cap = *cap ? *cap * 2 : 8;
        *arr = xrealloc(*arr, sizeof(inode) * (size_t)*cap);
    }
    memset(&(*arr)[*n], 0, sizeof(inode));
    return &(*arr)[(*n)++];
}

static void parse_items(const char **pp, const char *end, inode **out, int *n, int depth)
{
    const char *p = *pp;
    int cap = 0;
    *out = NULL;
    *n = 0;
    while (p < end) {
        inode *x;
        while (p < end && (*p == ' ' || *p == '\r' || *p == '\n')) p++;
        if (p >= end) break;
        if (*p == ')') { p++; break; }
        x = push(out, n, &cap);
        if (*p == '(') {
            p++;
            x->t = IN_LIST;
            if (depth < 64) parse_items(&p, end, &x->kids, &x->n, depth + 1);
            continue;
        }
        if (*p == '"') {
            buf_t s;
            buf_init(&s);
            p++;
            while (p < end && *p != '"') {
                if (*p == '\\' && p + 1 < end) p++;
                buf_addc(&s, *p++);
            }
            if (p < end) p++;
            x->t = IN_STR;
            x->len = s.len;
            x->s = buf_detach(&s);
            continue;
        }
        if (*p == '{') {
            const char *q = p + 1;
            size_t len = 0;
            while (q < end && isdigit((unsigned char)*q)) len = len * 10 + (size_t)(*q++ - '0');
            if (q < end && *q == '+') q++;
            if (q < end && *q == '}') {
                q++;
                if (q < end && *q == '\r') q++;
                if (q < end && *q == '\n') q++;
                if ((size_t)(end - q) < len) len = (size_t)(end - q);
                x->t = IN_STR;
                x->s = xstrndup(q, len);
                x->len = len;
                p = q + len;
                continue;
            }
        }
        {
            const char *s = p;
            int br = 0;
            while (p < end) {
                if (*p == '[') br++;
                else if (*p == ']' && br) br--;
                else if (!br && (*p == ' ' || *p == '(' || *p == ')' || *p == '\r' || *p == '\n')) break;
                p++;
            }
            if (p == s) { p++; (*n)--; continue; }
            x->len = (size_t)(p - s);
            x->s = xstrndup(s, x->len);
            x->t = (x->len == 3 && strnieq(s, "NIL", 3)) ? IN_NIL : IN_ATOM;
        }
    }
    *pp = p;
}

const char *inode_str(const inode *x)
{
    if (!x || x->t == IN_NIL || x->t == IN_LIST || !x->s) return "";
    return x->s;
}

inode *inode_get(inode *list, int n, int i) { return i >= 0 && i < n ? &list[i] : NULL; }

inode *fetch_item(inode *list, const char *name)
{
    if (!list || list->t != IN_LIST) return NULL;
    for (int i = 0; i + 1 < list->n; i += 2)
        if (list->kids[i].t == IN_ATOM && strieq(list->kids[i].s, name)) return &list->kids[i + 1];
    return NULL;
}

/* ---------------- protocol ---------------- */

/* Reads one full response, pulling in literals: "...{n}\r\n<n bytes>..." */
static int read_response(imap *m, buf_t *full)
{
    buf_t line;
    buf_init(&line);
    buf_clear(full);
    for (;;) {
        size_t l;
        if (conn_readline(m->c, &line) != 0) {
            m->io_error = 1;
            snprintf(m->err, sizeof(m->err), "%s", conn_error(m->c));
            buf_free(&line);
            return -1;
        }
        buf_add(full, buf_str(&line), line.len);
        l = line.len;
        if (l >= 3 && line.p[l - 1] == '}') {
            const char *o = line.p + l - 2;
            size_t n = 0, mul = 1;
            while (o > line.p && (isdigit((unsigned char)*o) || *o == '+')) {
                if (*o != '+') { n += (size_t)(*o - '0') * mul; mul *= 10; }
                o--;
            }
            if (*o == '{' && mul > 1) {
                buf_adds(full, "\r\n");
                if (conn_read(m->c, full, n) != 0) {
                    m->io_error = 1;
                    snprintf(m->err, sizeof(m->err), "%s", conn_error(m->c));
                    buf_free(&line);
                    return -1;
                }
                continue;   /* the response goes on after the literal */
            }
        }
        break;
    }
    buf_free(&line);
    return 0;
}

static void note_code(imap *m, const char *text)
{
    const char *b = strchr(text, '[');
    if (!b) return;
    if (starts_with_i(b + 1, "UIDVALIDITY ")) m->uidvalidity = (uint32_t)strtoul(b + 13, NULL, 10);
    else if (starts_with_i(b + 1, "UIDNEXT ")) m->uidnext = (uint32_t)strtoul(b + 9, NULL, 10);
    else if (starts_with_i(b + 1, "READ-ONLY")) m->readonly = 1;
    else if (starts_with_i(b + 1, "CAPABILITY ")) {
        const char *e = strchr(b, ']');
        size_t n = e ? (size_t)(e - (b + 12)) : strlen(b + 12);
        if (n >= sizeof(m->caps) - 2) n = sizeof(m->caps) - 3;
        m->caps[0] = ' ';
        memcpy(m->caps + 1, b + 12, n);
        m->caps[n + 1] = ' ';
        m->caps[n + 2] = '\0';
    }
}

static void handle_untagged(imap *m, const char *resp, size_t len, imap_untagged_fn fn, void *ctx)
{
    const char *p = resp + 2, *end = resp + len;
    inode *items;
    int n;
    if (starts_with_i(p, "OK ") || starts_with_i(p, "NO ") || starts_with_i(p, "BAD ")) {
        note_code(m, p);
        if (starts_with_i(p, "NO ") || starts_with_i(p, "BAD ")) rb_log("server: %.200s", p);
        return;
    }
    if (starts_with_i(p, "BYE")) {
        snprintf(m->err, sizeof(m->err), "server closed the session: %.200s", p + 3);
        return;
    }
    if (starts_with_i(p, "CAPABILITY ")) {
        snprintf(m->caps, sizeof(m->caps), " %.*s ", (int)(len - 13 > 1000 ? 1000 : len - 13), p + 11);
        return;
    }
    parse_items(&p, end, &items, &n, 0);
    if (n >= 2 && items[1].t == IN_ATOM && strieq(items[1].s, "EXISTS"))
        m->exists = (uint32_t)strtoul(items[0].s, NULL, 10);
    if (fn) fn(ctx, items, n);
    inode_free_all(items, n);
}

static int wait_tagged(imap *m, const char *tag, imap_untagged_fn fn, void *ctx)
{
    buf_t r;
    size_t tl = strlen(tag);
    buf_init(&r);
    for (;;) {
        if (read_response(m, &r) != 0) { buf_free(&r); return -1; }
        if (rb_debug) rb_log("S: %.300s", buf_str(&r));
        if (r.len > tl && !strncmp(r.p, tag, tl) && r.p[tl] == ' ') {
            const char *st = r.p + tl + 1;
            int ok = starts_with_i(st, "OK");
            note_code(m, st);
            if (!ok) {
                const char *t = strchr(st, ' ');
                t = t ? t + 1 : st;
                if (*t == '[') { const char *e = strchr(t, ']'); if (e) t = e + 1; }
                while (*t == ' ') t++;
                snprintf(m->err, sizeof(m->err), "%s", *t ? t : "command failed");
            }
            buf_free(&r);
            return ok ? 0 : -1;
        }
        if (r.len >= 2 && r.p[0] == '*' && r.p[1] == ' ') handle_untagged(m, r.p, r.len, fn, ctx);
    }
}

int imap_cmd(imap *m, imap_untagged_fn fn, void *ctx, const char *fmt, ...)
{
    buf_t cmd;
    char tag[16];
    va_list ap;
    int rc;
    m->err[0] = '\0';
    if (!m->c || m->io_error) {
        snprintf(m->err, sizeof(m->err), "not connected");
        return -1;
    }
    snprintf(tag, sizeof(tag), "R%04d", ++m->tag);
    buf_init(&cmd);
    buf_printf(&cmd, "%s ", tag);
    va_start(ap, fmt);
    buf_vprintf(&cmd, fmt, ap);
    va_end(ap);
    if (rb_debug) {
        if (stristr(cmd.p, " LOGIN ") || stristr(cmd.p, " AUTHENTICATE ")) rb_log("C: %s <login hidden>", tag);
        else rb_log("C: %s", cmd.p);
    }
    buf_adds(&cmd, "\r\n");
    if (conn_write(m->c, cmd.p, cmd.len) != 0) {
        m->io_error = 1;
        snprintf(m->err, sizeof(m->err), "%s", conn_error(m->c));
        buf_free(&cmd);
        return -1;
    }
    buf_free(&cmd);
    rc = wait_tagged(m, tag, fn, ctx);
    m->last_used = time(NULL);
    return rc;
}

int imap_has_cap(imap *m, const char *cap)
{
    char k[96];
    snprintf(k, sizeof(k), " %s ", cap);
    return stristr(m->caps, k) != NULL;
}

void imap_quote(buf_t *out, const char *s)
{
    buf_addc(out, '"');
    for (; *s; s++) {
        if (*s == '"' || *s == '\\') buf_addc(out, '\\');
        if (*s == '\r' || *s == '\n') continue;
        buf_addc(out, *s);
    }
    buf_addc(out, '"');
}

/* XOAUTH2 with a Microsoft access token. On a refusal the server sends a "+ <details>"
   continuation, which takes an empty line before the tagged NO. */
static int login_xoauth2(imap *m, account *a, const char *user)
{
    char tag[16], *b64;
    buf_t line;
    int rc;
    if (oauth_ensure(a, m->err, sizeof(m->err)) != 0) return -1;
    b64 = oauth_xoauth2(user, a->access);
    buf_init(&line);
    snprintf(tag, sizeof(tag), "R%04d", ++m->tag);
    if (rb_debug) rb_log("C: %s AUTHENTICATE XOAUTH2 <login hidden>", tag);
    if (imap_has_cap(m, "SASL-IR")) buf_printf(&line, "%s AUTHENTICATE XOAUTH2 %s\r\n", tag, b64);
    else buf_printf(&line, "%s AUTHENTICATE XOAUTH2\r\n", tag);
    rc = conn_write(m->c, line.p, line.len);
    memset(line.p, 0, line.len);
    for (int sent = imap_has_cap(m, "SASL-IR"); rc == 0;) {
        rc = conn_readline(m->c, &line);
        if (rc != 0) break;
        if (line.len && line.p[0] == '+') {
            buf_clear(&line);
            if (!sent) { buf_printf(&line, "%s\r\n", b64); sent = 1; }
            else buf_adds(&line, "\r\n");
            rc = conn_write(m->c, line.p, line.len);
            memset(line.p, 0, line.len);
            continue;
        }
        if (line.len > strlen(tag) && !strncmp(line.p, tag, strlen(tag)) && line.p[strlen(tag)] == ' ') {
            const char *st = line.p + strlen(tag) + 1;
            if (starts_with_i(st, "OK")) { rc = 0; break; }
            snprintf(m->err, sizeof(m->err), "%.300s", st);
            rc = -2;
            break;
        }
        /* untagged lines (CAPABILITY) are fine to skip here; capabilities are asked for again */
    }
    if (rc == -1) {
        m->io_error = 1;
        snprintf(m->err, sizeof(m->err), "%s", conn_error(m->c));
    }
    if (rc != 0) {
        /* a rejected token may just be stale: the next attempt refreshes it */
        free(a->access);
        a->access = NULL;
        a->access_exp = 0;
    }
    memset(b64, 0, strlen(b64));
    free(b64);
    buf_free(&line);
    return rc == 0 ? 0 : -1;
}

static int login(imap *m, account *a)
{
    const char *user = a->user && *a->user ? a->user : a->email;
    const char *pass = a->pass ? a->pass : "";
    if (a->auth == AUTH_MICROSOFT) return login_xoauth2(m, a, user);
    if (imap_has_cap(m, "LOGINDISABLED") && !imap_has_cap(m, "AUTH=PLAIN")) {
        snprintf(m->err, sizeof(m->err), "server refuses plain logins on this connection; use TLS or STARTTLS");
        return -1;
    }
    if (imap_has_cap(m, "AUTH=PLAIN")) {
        buf_t raw, b64;
        int rc;
        buf_init(&raw);
        buf_init(&b64);
        buf_addc(&raw, 0);
        buf_adds(&raw, user);
        buf_addc(&raw, 0);
        buf_adds(&raw, pass);
        b64_encode(&b64, raw.p, raw.len, 0);
        if (imap_has_cap(m, "SASL-IR")) {
            rc = imap_cmd(m, NULL, NULL, "AUTHENTICATE PLAIN %s", b64.p);
        } else {
            char tag[16];
            buf_t line;
            buf_init(&line);
            snprintf(tag, sizeof(tag), "R%04d", ++m->tag);
            buf_printf(&line, "%s AUTHENTICATE PLAIN\r\n", tag);
            rc = conn_write(m->c, line.p, line.len);
            if (rc == 0) rc = conn_readline(m->c, &line);
            if (rc == 0 && line.len && line.p[0] == '+') {
                buf_clear(&line);
                buf_printf(&line, "%s\r\n", b64.p);
                rc = conn_write(m->c, line.p, line.len);
                if (rc == 0) rc = wait_tagged(m, tag, NULL, NULL);
            } else if (rc == 0) {
                snprintf(m->err, sizeof(m->err), "%.300s", buf_str(&line));
                rc = -1;
            } else {
                m->io_error = 1;
                snprintf(m->err, sizeof(m->err), "%s", conn_error(m->c));
            }
            buf_free(&line);
        }
        memset(raw.p, 0, raw.len);
        buf_free(&raw);
        buf_free(&b64);
        return rc;
    } else {
        buf_t q;
        int rc;
        buf_init(&q);
        imap_quote(&q, user);
        buf_addc(&q, ' ');
        imap_quote(&q, pass);
        rc = imap_cmd(m, NULL, NULL, "LOGIN %s", q.p);
        memset(q.p, 0, q.len);
        buf_free(&q);
        return rc;
    }
}

imap *imap_open(account *a, char *err, size_t errlen)
{
    imap *m = xcalloc(1, sizeof(*m));
    buf_t greet;
    m->c = conn_open(a->imap_host, a->imap_port, a->imap_sec, a->imap_pin, err, errlen);
    if (!m->c) { free(m); return NULL; }
    buf_init(&greet);
    if (read_response(m, &greet) != 0 || !(starts_with_i(greet.p, "* OK") || starts_with_i(greet.p, "* PREAUTH"))) {
        snprintf(err, errlen, "%s didn't greet as an IMAP server: %.200s", a->imap_host,
                 m->err[0] ? m->err : buf_str(&greet));
        buf_free(&greet);
        imap_close(m);
        return NULL;
    }
    note_code(m, greet.p);
    buf_free(&greet);
    if (!m->caps[0] && imap_cmd(m, NULL, NULL, "CAPABILITY") != 0) goto fail;
    if (a->imap_sec == SEC_STARTTLS) {
        if (!imap_has_cap(m, "STARTTLS")) {
            snprintf(err, errlen, "%s doesn't offer STARTTLS on port %d", a->imap_host, a->imap_port);
            imap_close(m);
            return NULL;
        }
        if (imap_cmd(m, NULL, NULL, "STARTTLS") != 0) goto fail;
        if (conn_starttls(m->c, err, errlen) != 0) { imap_close(m); return NULL; }
        m->caps[0] = '\0';
        if (imap_cmd(m, NULL, NULL, "CAPABILITY") != 0) goto fail;
    }
    if (login(m, a) != 0) {
        snprintf(err, errlen, "login failed: %s", m->err);
        imap_close(m);
        return NULL;
    }
    m->caps[0] = '\0';
    if (imap_cmd(m, NULL, NULL, "CAPABILITY") != 0) goto fail;
    rb_log("imap %s: logged in (%s)", a->imap_host, conn_tls_info(m->c)[0] ? conn_tls_info(m->c) : "no TLS");
    return m;
fail:
    snprintf(err, errlen, "%s: %s", a->imap_host, m->err);
    imap_close(m);
    return NULL;
}

void imap_close(imap *m)
{
    if (!m) return;
    if (m->c && !m->io_error) {
        conn_set_timeout(m->c, 3);
        imap_cmd(m, NULL, NULL, "LOGOUT");
    }
    conn_close(m->c);
    free(m->selected);
    free(m);
}

static int do_select(imap *m, const char *folder)
{
    buf_t q;
    int rc;
    buf_init(&q);
    imap_quote(&q, folder);
    m->uidvalidity = m->uidnext = m->exists = 0;
    m->readonly = 0;
    free(m->selected);
    m->selected = NULL;
    rc = imap_cmd(m, NULL, NULL, "SELECT %s", q.p);
    buf_free(&q);
    if (rc == 0) m->selected = xstrdup(folder);
    return rc;
}

int imap_select(imap *m, const char *folder)
{
    if (m->selected && !strcmp(m->selected, folder)) return 0;
    return do_select(m, folder);
}

int imap_append(imap *m, const char *folder, const char *flags, const char *data, size_t len)
{
    buf_t cmd, line;
    char tag[16];
    int rc;
    buf_init(&cmd);
    buf_init(&line);
    snprintf(tag, sizeof(tag), "R%04d", ++m->tag);
    buf_printf(&cmd, "%s APPEND ", tag);
    imap_quote(&cmd, folder);
    if (flags && *flags) buf_printf(&cmd, " (%s)", flags);
    buf_printf(&cmd, " {%lu}\r\n", (unsigned long)len);
    rc = conn_write(m->c, cmd.p, cmd.len);
    if (rc == 0) {
        /* wait for the continuation */
        for (;;) {
            rc = conn_readline(m->c, &line);
            if (rc != 0) break;
            if (line.len && line.p[0] == '+') break;
            if (line.len > strlen(tag) && !strncmp(line.p, tag, strlen(tag))) {
                snprintf(m->err, sizeof(m->err), "%.300s", line.p + strlen(tag) + 1);
                rc = -1;
                break;
            }
        }
    }
    if (rc == 0) rc = conn_write(m->c, data, len);
    if (rc == 0) rc = conn_puts(m->c, "\r\n");
    if (rc == 0) rc = wait_tagged(m, tag, NULL, NULL);
    else if (!m->err[0]) { m->io_error = 1; snprintf(m->err, sizeof(m->err), "%s", conn_error(m->c)); }
    buf_free(&cmd);
    buf_free(&line);
    return rc;
}

/* ---------------- folders ---------------- */

typedef struct { folder_info *f; int n, cap; } list_ctx;

static const char *role_from_name(const char *display)
{
    static const struct { const char *name, *role; } N[] = {
        {"sent", "sent"}, {"sent items", "sent"}, {"sent mail", "sent"}, {"sent messages", "sent"},
        {"gesendet", "sent"}, {"gesendete elemente", "sent"}, {"envoy\xc3\xa9s", "sent"},
        {"drafts", "drafts"}, {"draft", "drafts"}, {"entw\xc3\xbcrfe", "drafts"}, {"brouillons", "drafts"},
        {"trash", "trash"}, {"deleted items", "trash"}, {"deleted messages", "trash"}, {"bin", "trash"},
        {"papierkorb", "trash"}, {"corbeille", "trash"}, {"junk", "junk"}, {"spam", "junk"},
        {"junk e-mail", "junk"}, {"junk email", "junk"}, {"bulk mail", "junk"}, {"archive", "archive"},
        {"archives", "archive"}, {NULL, NULL}};
    const char *last = display;
    for (const char *p = display; *p; p++)
        if (*p == '/' || *p == '.') last = p + 1;
    for (int i = 0; N[i].name; i++)
        if (strieq(last, N[i].name)) return N[i].role;
    return "";
}

static void on_list(void *ctx, inode *it, int n)
{
    list_ctx *l = ctx;
    folder_info *f;
    if (n < 3 || it[0].t != IN_ATOM) return;
    if (strieq(it[0].s, "STATUS") && it[2].t == IN_LIST) {
        const char *name = inode_str(&it[1]);
        for (int i = 0; i < l->n; i++) {
            if (strcmp(l->f[i].name, name)) continue;
            for (int k = 0; k + 1 < it[2].n; k += 2) {
                if (strieq(inode_str(&it[2].kids[k]), "MESSAGES")) l->f[i].total = atoi(inode_str(&it[2].kids[k + 1]));
                if (strieq(inode_str(&it[2].kids[k]), "UNSEEN")) l->f[i].unseen = atoi(inode_str(&it[2].kids[k + 1]));
            }
        }
        return;
    }
    if (n < 4 || !strieq(it[0].s, "LIST") || it[1].t != IN_LIST) return;
    if (l->n == l->cap) {
        l->cap = l->cap ? l->cap * 2 : 16;
        l->f = xrealloc(l->f, sizeof(folder_info) * (size_t)l->cap);
    }
    f = &l->f[l->n++];
    memset(f, 0, sizeof(*f));
    f->unseen = f->total = -1;
    f->name = xstrdup(inode_str(&it[3]));
    f->delim = it[2].t == IN_NIL ? 0 : inode_str(&it[2])[0];
    f->role = xstrdup("");
    for (int k = 0; k < it[1].n; k++) {
        const char *a = inode_str(&it[1].kids[k]);
        static const char *R[] = {"\\Sent", "sent", "\\Drafts", "drafts", "\\Trash", "trash", "\\Junk", "junk",
                                  "\\Archive", "archive", "\\All", "all", "\\Flagged", "flagged", NULL};
        if (strieq(a, "\\Noselect") || strieq(a, "\\NonExistent")) f->noselect = 1;
        for (int r = 0; R[r]; r += 2)
            if (strieq(a, R[r])) { free(f->role); f->role = xstrdup(R[r + 1]); }
    }
    if (strieq(f->name, "INBOX")) { free(f->role); f->role = xstrdup("inbox"); }
    {
        char *d = imap_mutf7_decode(f->name);
        if (f->delim && f->delim != '/') {
            for (char *p = d; *p; p++)
                if (*p == f->delim) *p = '/';
        }
        f->display = d;
    }
    if (!f->role[0]) { free(f->role); f->role = xstrdup(role_from_name(f->display)); }
    /* LIST-STATUS replies put the STATUS right after; fall through for it */
}

static int role_rank(const char *r)
{
    static const char *O[] = {"inbox", "drafts", "sent", "archive", "all", "flagged", "junk", "trash", NULL};
    for (int i = 0; O[i]; i++)
        if (!strcmp(r, O[i])) return i;
    return 100;
}

static int cmp_folder(const void *x, const void *y)
{
    const folder_info *a = x, *b = y;
    int ra = role_rank(a->role), rb = role_rank(b->role);
    if (ra != rb) return ra - rb;
    return strcmp(a->display, b->display);
}

int imap_list_folders(imap *m, folder_info **out, int *n)
{
    list_ctx l = {0};
    int rc;
    if (imap_has_cap(m, "LIST-STATUS"))
        rc = imap_cmd(m, on_list, &l, "LIST \"\" \"*\" RETURN (STATUS (MESSAGES UNSEEN))");
    else
        rc = imap_cmd(m, on_list, &l, "LIST \"\" \"*\"");
    if (rc != 0) { folder_info_free(l.f, l.n); return -1; }
    for (int i = 0; i < l.n; i++) {
        buf_t q;
        if (l.f[i].noselect || l.f[i].unseen >= 0) continue;
        buf_init(&q);
        imap_quote(&q, l.f[i].name);
        imap_cmd(m, on_list, &l, "STATUS %s (MESSAGES UNSEEN)", q.p);
        buf_free(&q);
        if (m->io_error) break;
    }
    qsort(l.f, (size_t)l.n, sizeof(folder_info), cmp_folder);
    *out = l.f;
    *n = l.n;
    return m->io_error ? -1 : 0;
}

void folder_info_free(folder_info *f, int n)
{
    for (int i = 0; i < n; i++) {
        free(f[i].name);
        free(f[i].display);
        free(f[i].role);
    }
    free(f);
}

/* ---------------- sync ---------------- */

static char *format_addr(inode *a)
{
    /* (name adl mailbox host) */
    char *name, *r;
    const char *mb, *host;
    if (!a || a->t != IN_LIST || a->n < 4) return xstrdup("");
    name = decode_header(inode_str(&a->kids[0]));
    mb = inode_str(&a->kids[2]);
    host = inode_str(&a->kids[3]);
    if (*name && *host) r = xprintf("%s <%s@%s>", name, mb, host);
    else if (*host) r = xprintf("%s@%s", mb, host);
    else r = xstrdup(name);
    free(name);
    return r;
}

static char *format_addr_list(inode *l, int max)
{
    buf_t b;
    if (!l || l->t != IN_LIST) return xstrdup("");
    buf_init(&b);
    for (int i = 0; i < l->n && i < max; i++) {
        char *a = format_addr(&l->kids[i]);
        if (*a) {
            if (b.len) buf_adds(&b, ", ");
            buf_adds(&b, a);
        }
        free(a);
    }
    if (l->n > max) buf_adds(&b, ", ...");
    return buf_detach(&b);
}

static void flags_from(inode *fl, char out[8])
{
    int n = 0;
    out[0] = '\0';
    if (!fl || fl->t != IN_LIST) return;
    for (int i = 0; i < fl->n && n < 7; i++) {
        const char *f = inode_str(&fl->kids[i]);
        char c = strieq(f, "\\Seen") ? 'S' : strieq(f, "\\Flagged") ? 'F' : strieq(f, "\\Answered") ? 'A' :
                 strieq(f, "\\Deleted") ? 'D' : strieq(f, "\\Draft") ? 'T' : 0;
        if (c && !strchr(out, c)) { out[n++] = c; out[n] = '\0'; }
    }
}

typedef struct {
    folder_cache *fc;
    uint32_t min_new;       /* only insert uids >= this */
    int added;
    uint32_t *seen_uids;    /* for flag refresh: uids the server still has */
    int nseen, capseen;
    int flags_only;
    uint32_t *search;
    int nsearch, capsearch;
    buf_t *body;            /* BODY[] for fetch_message */
} sync_ctx;

static void on_fetch(void *ctx, inode *it, int n)
{
    sync_ctx *s = ctx;
    inode *l, *v;
    uint32_t uid;
    msg_entry *e;
    if (n >= 1 && it[0].t == IN_ATOM && strieq(it[0].s, "SEARCH")) {
        for (int i = 1; i < n; i++) {
            if (s->nsearch == s->capsearch) {
                s->capsearch = s->capsearch ? s->capsearch * 2 : 256;
                s->search = xrealloc(s->search, sizeof(uint32_t) * (size_t)s->capsearch);
            }
            s->search[s->nsearch++] = (uint32_t)strtoul(inode_str(&it[i]), NULL, 10);
        }
        return;
    }
    if (n < 3 || !strieq(inode_str(&it[1]), "FETCH") || it[2].t != IN_LIST) return;
    l = &it[2];
    v = fetch_item(l, "UID");
    if (!v) return;
    uid = (uint32_t)strtoul(inode_str(v), NULL, 10);
    if (s->body) {
        for (int i = 0; i + 1 < l->n; i += 2)
            if (starts_with_i(inode_str(&l->kids[i]), "BODY[")) {
                buf_add(s->body, l->kids[i + 1].s ? l->kids[i + 1].s : "", l->kids[i + 1].len);
            }
        return;
    }
    if (s->flags_only) {
        if (s->nseen == s->capseen) {
            s->capseen = s->capseen ? s->capseen * 2 : 256;
            s->seen_uids = xrealloc(s->seen_uids, sizeof(uint32_t) * (size_t)s->capseen);
        }
        s->seen_uids[s->nseen++] = uid;
        e = cache_find(s->fc, uid);
        if (e) flags_from(fetch_item(l, "FLAGS"), e->flags);
        return;
    }
    if (uid < s->min_new) return;
    e = cache_find(s->fc, uid);
    if (!e) { e = cache_insert(s->fc, uid); s->added++; }
    flags_from(fetch_item(l, "FLAGS"), e->flags);
    v = fetch_item(l, "RFC822.SIZE");
    if (v) e->size = (uint32_t)strtoul(inode_str(v), NULL, 10);
    v = fetch_item(l, "INTERNALDATE");
    if (v) e->date = (int64_t)parse_imap_date(inode_str(v));
    v = fetch_item(l, "ENVELOPE");
    if (v && v->t == IN_LIST && v->n >= 10) {
        time_t d = parse_rfc5322_date(inode_str(&v->kids[0]));
        if (d > 0) e->date = (int64_t)d;
        free(e->subject);
        e->subject = decode_header(inode_str(&v->kids[1]));
        free(e->from);
        e->from = format_addr_list(&v->kids[2], 1);
        free(e->to);
        e->to = format_addr_list(&v->kids[5], 3);
        free(e->msgid);
        e->msgid = xstrdup(inode_str(&v->kids[9]));
    }
    for (int i = 0; i + 1 < l->n; i += 2) {
        if (starts_with_i(inode_str(&l->kids[i]), "BODY[HEADER")) {
            const inode *h = &l->kids[i + 1];
            char *ct = h->s ? hdr_get(h->s, h->len, "Content-Type") : NULL;
            e->att = ct && (starts_with_i(ct, "multipart/mixed") || starts_with_i(ct, "application/"));
            free(ct);
        }
    }
}

static int cmp_u32(const void *a, const void *b)
{
    uint32_t x = *(const uint32_t *)a, y = *(const uint32_t *)b;
    return x < y ? -1 : x > y;
}

int sync_folder(account *a, imap *m, const char *folder, folder_cache *fc, int *added)
{
    sync_ctx s;
    (void)a;
    memset(&s, 0, sizeof(s));
    s.fc = fc;
    *added = 0;
    if (do_select(m, folder) != 0) return -1;
    if (fc->uidvalidity != m->uidvalidity) {
        cache_clear(fc);
        fc->uidvalidity = m->uidvalidity;
    }
    fc->exists = m->exists;
    if (m->exists == 0) {
        cache_clear(fc);
        fc->uidvalidity = m->uidvalidity;
        fc->uidnext = m->uidnext;
        return cache_save(fc);
    }
    if (fc->n == 0) {
        uint32_t start = m->exists > 100 ? m->exists - 99 : 1;
        if (imap_cmd(m, on_fetch, &s, "FETCH %u:* " FETCH_ITEMS, start) != 0) return -1;
    } else {
        uint32_t lo = fc->e[0].uid, hi = fc->e[fc->n - 1].uid;
        s.flags_only = 1;
        if (imap_cmd(m, on_fetch, &s, "UID FETCH %u:%u (UID FLAGS)", lo, hi) != 0) { free(s.seen_uids); return -1; }
        qsort(s.seen_uids, (size_t)s.nseen, sizeof(uint32_t), cmp_u32);
        for (int i = fc->n - 1; i >= 0; i--) {
            uint32_t u = fc->e[i].uid;
            if (!bsearch(&u, s.seen_uids, (size_t)s.nseen, sizeof(uint32_t), cmp_u32)) cache_remove(fc, u);
        }
        free(s.seen_uids);
        s.seen_uids = NULL;
        s.flags_only = 0;
        if (!m->uidnext || m->uidnext > hi + 1) {
            s.min_new = hi + 1;
            if (imap_cmd(m, on_fetch, &s, "UID FETCH %u:* " FETCH_ITEMS, hi + 1) != 0) return -1;
        }
    }
    fc->uidnext = m->uidnext;
    *added = s.added;
    return cache_save(fc);
}

int sync_more(account *a, imap *m, const char *folder, folder_cache *fc, int count, int *added)
{
    sync_ctx s;
    buf_t set;
    uint32_t lo;
    int from;
    *added = 0;
    if (fc->n == 0) return sync_folder(a, m, folder, fc, added);
    lo = fc->e[0].uid;
    if (lo <= 1) return 0;
    if (imap_select(m, folder) != 0) return -1;
    memset(&s, 0, sizeof(s));
    s.fc = fc;
    if (imap_cmd(m, on_fetch, &s, "UID SEARCH UID 1:%u", lo - 1) != 0) { free(s.search); return -1; }
    if (s.nsearch == 0) { free(s.search); return 0; }
    qsort(s.search, (size_t)s.nsearch, sizeof(uint32_t), cmp_u32);
    from = s.nsearch > count ? s.nsearch - count : 0;
    buf_init(&set);
    for (int i = from; i < s.nsearch; i++) {
        if (s.search[i] >= lo) continue;
        buf_printf(&set, "%s%u", set.len ? "," : "", s.search[i]);
    }
    free(s.search);
    s.search = NULL;
    if (set.len && imap_cmd(m, on_fetch, &s, "UID FETCH %s " FETCH_ITEMS, set.p) != 0) {
        buf_free(&set);
        return -1;
    }
    buf_free(&set);
    *added = s.added;
    return cache_save(fc);
}

char *fetch_message(account *a, imap *m, const char *folder, uint32_t uid, size_t *len)
{
    char *dir = folder_dir(a, folder), *path = xprintf("%s/%u.eml", dir, uid);
    char *data = read_file(path, len);
    sync_ctx s;
    buf_t body;
    if (data) { free(dir); free(path); return data; }
    if (!m) { free(dir); free(path); return NULL; }
    if (imap_select(m, folder) != 0) { free(dir); free(path); return NULL; }
    memset(&s, 0, sizeof(s));
    buf_init(&body);
    s.body = &body;
    if (imap_cmd(m, on_fetch, &s, "UID FETCH %u (UID BODY.PEEK[])", uid) != 0 || !body.len) {
        if (!m->err[0]) snprintf(m->err, sizeof(m->err), "message %u is gone from the server", uid);
        buf_free(&body);
        free(dir);
        free(path);
        return NULL;
    }
    make_dirs(dir);
    write_file(path, body.p, body.len);
    free(dir);
    free(path);
    *len = body.len;
    return buf_detach(&body);
}

int imap_set_flag(imap *m, const char *folder, uint32_t uid, int add, const char *flag)
{
    if (imap_select(m, folder) != 0) return -1;
    return imap_cmd(m, NULL, NULL, "UID STORE %u %cFLAGS.SILENT (%s)", uid, add ? '+' : '-', flag);
}

static int expunge_uid(imap *m, uint32_t uid)
{
    if (imap_cmd(m, NULL, NULL, "UID STORE %u +FLAGS.SILENT (\\Deleted)", uid) != 0) return -1;
    if (imap_has_cap(m, "UIDPLUS")) return imap_cmd(m, NULL, NULL, "UID EXPUNGE %u", uid);
    return imap_cmd(m, NULL, NULL, "EXPUNGE");
}

int imap_move(imap *m, const char *folder, uint32_t uid, const char *dest)
{
    buf_t q;
    int rc;
    if (imap_select(m, folder) != 0) return -1;
    buf_init(&q);
    imap_quote(&q, dest);
    if (imap_has_cap(m, "MOVE")) {
        rc = imap_cmd(m, NULL, NULL, "UID MOVE %u %s", uid, q.p);
    } else {
        rc = imap_cmd(m, NULL, NULL, "UID COPY %u %s", uid, q.p);
        if (rc == 0) rc = expunge_uid(m, uid);
    }
    buf_free(&q);
    return rc;
}

int imap_delete(imap *m, const char *folder, uint32_t uid, const char *trash)
{
    if (trash && *trash && strcmp(trash, folder)) return imap_move(m, folder, uid, trash);
    if (imap_select(m, folder) != 0) return -1;
    return expunge_uid(m, uid);
}
