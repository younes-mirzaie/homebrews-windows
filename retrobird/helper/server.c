/* rbmaild: the loopback service the Retrobird app talks to (see ../PROTOCOL.md), plus the
   background INBOX sync. One NSPR thread per app connection; IMAP work is serialised per account. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"
#include "nspr.h"

#ifdef _WIN32
#include <windows.h>
#include <process.h>
#define getpid _getpid
#else
#include <unistd.h>
#endif

static char *g_token;
static PRLock *g_lock;            /* account list */
static int g_sync_minutes = 15;
static volatile int g_quit;
static PRFileDesc *g_listener;

/* ---------------- client I/O ---------------- */

typedef struct {
    PRFileDesc *fd;
    char buf[65536];
    size_t start, end;
    buf_t out;
} client;

static int client_readline(client *c, buf_t *line)
{
    buf_clear(line);
    for (;;) {
        char *nl = memchr(c->buf + c->start, '\n', c->end - c->start);
        PRInt32 n;
        if (nl) {
            size_t l = (size_t)(nl - (c->buf + c->start));
            buf_add(line, c->buf + c->start, l);
            c->start += l + 1;
            if (line->len && line->p[line->len - 1] == '\r') line->p[--line->len] = '\0';
            if (!line->p) buf_adds(line, "");
            return 0;
        }
        buf_add(line, c->buf + c->start, c->end - c->start);
        c->start = c->end = 0;
        if (line->len > 32 * 1024 * 1024) return -1;
        n = PR_Recv(c->fd, c->buf, sizeof(c->buf), 0, PR_INTERVAL_NO_TIMEOUT);
        if (n <= 0) return -1;
        c->end = (size_t)n;
    }
}

static int flush(client *c)
{
    const char *p = c->out.p;
    size_t n = c->out.len;
    while (n > 0) {
        PRInt32 w = PR_Send(c->fd, p, (PRInt32)(n > 65536 ? 65536 : n), 0, PR_INTERVAL_NO_TIMEOUT);
        if (w <= 0) return -1;
        p += w;
        n -= (size_t)w;
    }
    buf_clear(&c->out);
    return 0;
}

static void out_field(client *c, const char *v) { buf_addc(&c->out, '\t'); field_escape(&c->out, v ? v : ""); }

static void out_ok(client *c) { buf_adds(&c->out, "OK"); }
static void out_end(client *c) { buf_addc(&c->out, '\n'); }

/* ERR<TAB>code<TAB>message; certificate errors ("CERT|<fingerprint>|text" from net.c) become
   ERR<TAB>CERT<TAB>text<TAB>fingerprint<TAB>imap|smtp so the app can offer to trust it. */
static void reply_err_side(client *c, const char *code, const char *msg, const char *side)
{
    if (msg && !strncmp(msg, "CERT|", 5)) {
        const char *fp = msg + 5, *bar = strchr(fp, '|');
        char f[72];
        snprintf(f, sizeof(f), "%.*s", bar ? (int)(bar - fp) : 0, fp);
        buf_adds(&c->out, "ERR\tCERT");
        out_field(c, bar ? bar + 1 : msg);
        out_field(c, f);
        out_field(c, side);
        out_end(c);
        return;
    }
    buf_adds(&c->out, "ERR");
    out_field(c, code);
    out_field(c, msg);
    out_end(c);
}

static void reply_err(client *c, const char *code, const char *msg) { reply_err_side(c, code, msg, "imap"); }

static const char *classify(const char *err)
{
    if (!strncmp(err, "CERT|", 5)) return "CERT";
    if (stristr(err, "login failed") || stristr(err, "authentication") || stristr(err, "AUTHENTICATIONFAILED"))
        return "AUTH";
    if (stristr(err, "can't find server") || stristr(err, "timed out") || stristr(err, "refused") ||
        stristr(err, "unreachable") || stristr(err, "connection") || stristr(err, "reading from") ||
        stristr(err, "sending to"))
        return "NET";
    return "SERVER";
}

/* ---------------- account helpers ---------------- */

static account *lock_account(const char *id)
{
    account *a;
    PR_Lock(g_lock);
    a = store_find(id);
    if (a) PR_Lock(a->lock);
    PR_Unlock(g_lock);
    return a;
}

static void unlock_account(account *a) { if (a) PR_Unlock(a->lock); }

static void set_last_error(account *a, const char *err)
{
    free(a->last_error);
    a->last_error = err && *err ? xstrdup(err) : NULL;
}

/* Returns a logged-in connection for a (account lock held). */
static imap *get_imap(account *a, char *err, size_t errlen)
{
    if (a->im && (a->im->io_error || time(NULL) - a->im->last_used > 25 * 60)) {
        imap_close(a->im);
        a->im = NULL;
    }
    if (a->im && time(NULL) - a->im->last_used > 120 && imap_cmd(a->im, NULL, NULL, "NOOP") != 0) {
        imap_close(a->im);
        a->im = NULL;
    }
    if (!a->im) {
        if (!a->pass) {
            snprintf(err, errlen, "login failed: no password stored for %s (edit the account)", a->email ? a->email : "");
            return NULL;
        }
        a->im = imap_open(a, err, errlen);
    }
    return a->im;
}

typedef int (*imap_op)(account *a, imap *m, void *ctx);

/* Runs op with a live connection, reconnecting once if the old one had died. */
static int run_imap(account *a, imap_op op, void *ctx, char *err, size_t errlen)
{
    for (int attempt = 0; attempt < 2; attempt++) {
        imap *m = get_imap(a, err, errlen);
        int rc;
        if (!m) return -1;
        m->err[0] = '\0';
        rc = op(a, m, ctx);
        if (rc == 0) return 0;
        snprintf(err, errlen, "%s", m->err[0] ? m->err : "IMAP command failed");
        if (!m->io_error) return -1;
        imap_close(a->im);
        a->im = NULL;
    }
    return -1;
}

static char *acct_dir(account *a) { return xprintf("%s/mail/%s", store_dir(), a->id); }

/* Folder list cache: name, display, role, unseen, total, noselect */
static void save_folders(account *a, folder_info *f, int n)
{
    buf_t b;
    char *dir = acct_dir(a), *path = xprintf("%s/folders", dir);
    buf_init(&b);
    for (int i = 0; i < n; i++) {
        field_escape(&b, f[i].name);
        buf_addc(&b, '\t');
        field_escape(&b, f[i].display);
        buf_printf(&b, "\t%s\t%d\t%d\t%d\n", f[i].role, f[i].unseen, f[i].total, f[i].noselect);
    }
    make_dirs(dir);
    write_file(path, b.p ? b.p : "", b.len);
    buf_free(&b);
    free(dir);
    free(path);
}

/* Name of the folder with this role from the cached list (malloc), or NULL. */
static char *folder_by_role(account *a, const char *role)
{
    char *dir = acct_dir(a), *path = xprintf("%s/folders", dir);
    char *data = read_file(path, NULL), *line, *next, *r = NULL;
    free(dir);
    free(path);
    for (line = data; line && *line && !r; line = next) {
        char *f[6];
        next = strchr(line, '\n');
        if (next) *next++ = '\0';
        if (split_fields(line, f, 6) >= 3 && !strcmp(f[2], role)) r = xstrdup(f[0]);
    }
    free(data);
    return r;
}

/* ---------------- commands ---------------- */

static void cmd_accounts(client *c)
{
    out_ok(c);
    out_end(c);
    PR_Lock(g_lock);
    for (account *a = store_accounts(); a; a = a->next) {
        char num[32];
        buf_adds(&c->out, "A");
        out_field(c, a->id);
        out_field(c, a->name);
        out_field(c, a->email);
        snprintf(num, sizeof(num), "%d", a->unseen);
        out_field(c, num);
        snprintf(num, sizeof(num), "%lld", (long long)a->last_sync);
        out_field(c, num);
        out_field(c, a->last_error);
        out_field(c, a->pass ? "1" : "0");
        out_end(c);
    }
    PR_Unlock(g_lock);
    out_end(c);
}

static void cmd_get(client *c, char **f, int n)
{
    account *a = n > 1 ? lock_account(f[1]) : NULL;
    char num[16];
    if (!a) { reply_err(c, "NOTFOUND", "no such account"); return; }
    out_ok(c);
    out_end(c);
#define KV(k, v) do { buf_adds(&c->out, k); out_field(c, v); out_end(c); } while (0)
    KV("id", a->id); KV("name", a->name); KV("email", a->email);
    KV("imap_host", a->imap_host); snprintf(num, sizeof(num), "%d", a->imap_port); KV("imap_port", num);
    KV("imap_sec", a->imap_sec == SEC_STARTTLS ? "starttls" : a->imap_sec == SEC_PLAIN ? "plain" : "tls");
    KV("smtp_host", a->smtp_host); snprintf(num, sizeof(num), "%d", a->smtp_port); KV("smtp_port", num);
    KV("smtp_sec", a->smtp_sec == SEC_STARTTLS ? "starttls" : a->smtp_sec == SEC_PLAIN ? "plain" : "tls");
    KV("user", a->user); KV("smtp_user", a->smtp_user);
    KV("imap_pin", a->imap_pin); KV("smtp_pin", a->smtp_pin);
    snprintf(num, sizeof(num), "%d", a->save_sent); KV("save_sent", num);
    KV("has_pass", a->pass ? "1" : "0");
#undef KV
    out_end(c);
    unlock_account(a);
}

/* SAVE <id or empty> key=value...   (check=0 skips the login test) */
static void cmd_save(client *c, char **f, int n)
{
    account *tmp = store_new_account(), *a;
    char err[512];
    int check = 1, is_new = n < 2 || !*f[1];
    if (!is_new) {
        a = lock_account(f[1]);
        if (!a) { account_free(tmp); reply_err(c, "NOTFOUND", "no such account"); return; }
        /* start from the stored values (keeps the password when none is sent) */
        tmp->name = a->name ? xstrdup(a->name) : NULL;
        tmp->email = a->email ? xstrdup(a->email) : NULL;
        tmp->imap_host = a->imap_host ? xstrdup(a->imap_host) : NULL;
        tmp->imap_port = a->imap_port;
        tmp->imap_sec = a->imap_sec;
        tmp->smtp_host = a->smtp_host ? xstrdup(a->smtp_host) : NULL;
        tmp->smtp_port = a->smtp_port;
        tmp->smtp_sec = a->smtp_sec;
        tmp->user = a->user ? xstrdup(a->user) : NULL;
        tmp->pass = a->pass ? xstrdup(a->pass) : NULL;
        tmp->smtp_user = a->smtp_user ? xstrdup(a->smtp_user) : NULL;
        tmp->imap_pin = a->imap_pin ? xstrdup(a->imap_pin) : NULL;
        tmp->smtp_pin = a->smtp_pin ? xstrdup(a->smtp_pin) : NULL;
        tmp->save_sent = a->save_sent;
        unlock_account(a);
    }
    for (int i = 2; i < n; i++) {
        char *eq = strchr(f[i], '=');
        if (!eq) continue;
        *eq = '\0';
        if (!strcmp(f[i], "check")) { check = atoi(eq + 1); continue; }
        if (!strcmp(f[i], "pass") && !eq[1] && !is_new) continue;   /* empty: keep the old one */
        if (account_set(tmp, f[i], eq + 1) != 0) {
            char m[96];
            snprintf(m, sizeof(m), "unknown field %s", f[i]);
            account_free(tmp);
            reply_err(c, "ARG", m);
            return;
        }
    }
    if (!tmp->email || !tmp->imap_host || !tmp->smtp_host) {
        account_free(tmp);
        reply_err(c, "ARG", "email, imap_host and smtp_host are required");
        return;
    }
    if (check) {
        imap *m = imap_open(tmp, err, sizeof(err));
        if (!m) {
            char full[600];
            snprintf(full, sizeof(full), "%s%s", strncmp(err, "CERT|", 5) ? "Incoming (IMAP): " : "", err);
            account_free(tmp);
            reply_err(c, classify(err), full);
            return;
        }
        imap_close(m);
        if (smtp_test(tmp, err, sizeof(err)) != 0) {
            char full[600];
            snprintf(full, sizeof(full), "%s%s", strncmp(err, "CERT|", 5) ? "Outgoing (SMTP): " : "", err);
            account_free(tmp);
            reply_err_side(c, classify(err), strncmp(err, "CERT|", 5) ? full : err, "smtp");
            return;
        }
    }
    PR_Lock(g_lock);
    if (is_new) {
        unsigned char rnd[6];
        buf_t id;
        buf_init(&id);
        random_bytes(rnd, sizeof(rnd));
        hex_encode(&id, rnd, sizeof(rnd));
        tmp->id = buf_detach(&id);
        store_add(tmp);
        a = tmp;
    } else {
        a = store_find(f[1]);
        if (a) {
            PR_Lock(a->lock);
            /* swap the settings in; drop the live connection so the new ones are used */
            {
                account keep = *a;
                tmp->id = a->id;
                PR_DestroyLock(tmp->lock);
                tmp->lock = a->lock;
                tmp->next = a->next;
                tmp->unseen = a->unseen;
                tmp->last_sync = a->last_sync;
                tmp->last_error = NULL;
                *a = *tmp;
                a->im = NULL;
                if (keep.im) imap_close(keep.im);
                free(keep.name); free(keep.email); free(keep.imap_host); free(keep.smtp_host);
                free(keep.user); if (keep.pass) memset(keep.pass, 0, strlen(keep.pass)); free(keep.pass);
                free(keep.smtp_user); free(keep.imap_pin); free(keep.smtp_pin); free(keep.last_error);
                free(tmp);
            }
            PR_Unlock(a->lock);
        } else {
            account_free(tmp);
        }
    }
    if (store_save() != 0) rb_log("couldn't write accounts.conf");
    out_ok(c);
    out_field(c, a ? a->id : "");
    out_end(c);
    PR_Unlock(g_lock);
}

static void cmd_delacct(client *c, char **f, int n)
{
    account *a;
    if (n < 2) { reply_err(c, "ARG", "DELACCT id"); return; }
    PR_Lock(g_lock);
    a = store_find(f[1]);
    if (a) {
        PR_Lock(a->lock);   /* wait for work on it to finish */
        PR_Unlock(a->lock);
        store_remove(f[1]);
        store_save();
    }
    PR_Unlock(g_lock);
    if (!a) reply_err(c, "NOTFOUND", "no such account");
    else { out_ok(c); out_end(c); }
}

typedef struct { folder_info *f; int n; } folders_ctx;

static int op_folders(account *a, imap *m, void *ctx)
{
    folders_ctx *fc = ctx;
    (void)a;
    return imap_list_folders(m, &fc->f, &fc->n);
}

static void cmd_folders(client *c, char **f, int n)
{
    account *a = n > 1 ? lock_account(f[1]) : NULL;
    folders_ctx fc = {0};
    char err[512];
    if (!a) { reply_err(c, "NOTFOUND", "no such account"); return; }
    if (run_imap(a, op_folders, &fc, err, sizeof(err)) == 0) {
        save_folders(a, fc.f, fc.n);
        for (int i = 0; i < fc.n; i++)
            if (!strcmp(fc.f[i].role, "inbox") && fc.f[i].unseen >= 0) a->unseen = fc.f[i].unseen;
        out_ok(c);
        out_field(c, "online");
        out_end(c);
        folder_info_free(fc.f, fc.n);
    } else {
        out_ok(c);
        out_field(c, "offline");
        out_field(c, err);
        out_end(c);
    }
    {
        /* rows come from the folder cache either way */
        char *dir = acct_dir(a), *path = xprintf("%s/folders", dir);
        char *data = read_file(path, NULL), *line, *next;
        for (line = data; line && *line; line = next) {
            next = strchr(line, '\n');
            if (next) *next++ = '\0';
            buf_adds(&c->out, "F\t");
            buf_adds(&c->out, line);   /* already escaped */
            out_end(c);
        }
        free(data);
        free(dir);
        free(path);
    }
    out_end(c);
    unlock_account(a);
}

typedef struct { const char *folder; int count; int added; int more; int unseen; } sync_args;

static void on_search_count(void *ctx, inode *it, int n)
{
    if (n >= 1 && strieq(inode_str(&it[0]), "SEARCH")) *(int *)ctx += n - 1;
}

static int op_sync(account *a, imap *m, void *ctx)
{
    sync_args *s = ctx;
    folder_cache fc;
    char *dir = folder_dir(a, s->folder);
    int rc;
    cache_load(&fc, dir);
    free(dir);
    rc = s->more ? sync_more(a, m, s->folder, &fc, s->count, &s->added)
                 : sync_folder(a, m, s->folder, &fc, &s->added);
    cache_free(&fc);
    /* the real unread count (the cache only holds the newest messages) */
    s->unseen = 0;
    if (rc == 0 && imap_cmd(m, on_search_count, &s->unseen, "SEARCH UNSEEN") != 0) s->unseen = -1;
    return rc;
}

static void cmd_sync(client *c, char **f, int n, int more)
{
    account *a = n > 2 ? lock_account(f[1]) : NULL;
    sync_args s = {0};
    char err[512], num[16];
    if (!a) { reply_err(c, n > 2 ? "NOTFOUND" : "ARG", n > 2 ? "no such account" : "SYNC id folder"); return; }
    s.folder = f[2];
    s.more = more;
    s.count = more && n > 3 ? atoi(f[3]) : 50;
    if (s.count <= 0 || s.count > 500) s.count = 50;
    if (run_imap(a, op_sync, &s, err, sizeof(err)) != 0) {
        if (strieq(s.folder, "INBOX")) set_last_error(a, err);
        unlock_account(a);
        reply_err(c, classify(err), err);
        return;
    }
    if (strieq(s.folder, "INBOX")) {
        a->last_sync = time(NULL);
        if (s.unseen >= 0) a->unseen = s.unseen;
        set_last_error(a, NULL);
    }
    unlock_account(a);
    out_ok(c);
    snprintf(num, sizeof(num), "%d", s.added);
    out_field(c, num);
    snprintf(num, sizeof(num), "%d", s.unseen);
    out_field(c, num);
    out_end(c);
}

/* LIST id folder offset count: newest first, from the cache (works offline) */
static void cmd_list(client *c, char **f, int n)
{
    account *a = n > 2 ? lock_account(f[1]) : NULL;
    folder_cache fc;
    char *dir;
    int off, cnt;
    char num[64];
    if (!a) { reply_err(c, "NOTFOUND", "no such account"); return; }
    off = n > 3 ? atoi(f[3]) : 0;
    cnt = n > 4 ? atoi(f[4]) : 50;
    dir = folder_dir(a, f[2]);
    cache_load(&fc, dir);
    free(dir);
    out_ok(c);
    snprintf(num, sizeof(num), "%d", fc.n);
    out_field(c, num);
    snprintf(num, sizeof(num), "%u", fc.exists);
    out_field(c, num);
    out_end(c);
    for (int i = fc.n - 1 - off, k = 0; i >= 0 && k < cnt; i--, k++) {
        msg_entry *e = &fc.e[i];
        buf_printf(&c->out, "M\t%u\t%s\t%lld\t%u\t%d", e->uid, e->flags, (long long)e->date, e->size, e->att);
        out_field(c, e->from);
        out_field(c, e->to);
        out_field(c, e->subject);
        out_end(c);
    }
    out_end(c);
    cache_free(&fc);
    unlock_account(a);
}

typedef struct { const char *folder; uint32_t uid; char *data; size_t len; } fetch_args;

static int op_fetch(account *a, imap *m, void *ctx)
{
    fetch_args *fa = ctx;
    fa->data = fetch_message(a, m, fa->folder, fa->uid, &fa->len);
    return fa->data ? 0 : -1;
}

/* Loads a message: from the local cache, else from the server (account lock held). */
static char *load_message(account *a, const char *folder, uint32_t uid, size_t *len, char *err, size_t errlen)
{
    fetch_args fa = {folder, uid, NULL, 0};
    fa.data = fetch_message(a, NULL, folder, uid, &fa.len);
    if (!fa.data && run_imap(a, op_fetch, &fa, err, errlen) != 0) return NULL;
    *len = fa.len;
    return fa.data;
}

static int resolve_cid(void *ctx, const char *cid, buf_t *uri)
{
    mime_msg *m = ctx;
    for (int i = 0; i < m->nleaves; i++) {
        mime_part *p = mime_leaf(m, i);
        if (p && p->cid && !strcmp(p->cid, cid)) {
            buf_t raw;
            buf_init(&raw);
            mime_decode(p, &raw);
            buf_printf(uri, "data:%s;base64,", p->ctype);
            b64_encode(uri, raw.p ? raw.p : "", raw.len, 0);
            buf_free(&raw);
            return 0;
        }
    }
    return -1;
}

static void update_cached_flag(account *a, const char *folder, uint32_t uid, char flag, int add)
{
    folder_cache fc;
    char *dir = folder_dir(a, folder);
    msg_entry *e;
    cache_load(&fc, dir);
    free(dir);
    e = cache_find(&fc, uid);
    if (e) {
        char *p = strchr(e->flags, flag);
        if (add && !p && strlen(e->flags) < sizeof(e->flags) - 1) {
            size_t l = strlen(e->flags);
            e->flags[l] = flag;
            e->flags[l + 1] = '\0';
        } else if (!add && p) {
            memmove(p, p + 1, strlen(p));
        }
        cache_save(&fc);
    }
    cache_free(&fc);
}

typedef struct { const char *folder; uint32_t uid; int add; const char *flag; const char *dest; } flag_args;

static int op_flag(account *a, imap *m, void *ctx)
{
    flag_args *fl = ctx;
    (void)a;
    return imap_set_flag(m, fl->folder, fl->uid, fl->add, fl->flag);
}

static char *part_name(mime_part *p)
{
    const char *slash;
    if (p->filename && *p->filename) return xstrdup(p->filename);
    slash = strchr(p->ctype, '/');
    if (!strcmp(p->ctype, "message/rfc822")) return xprintf("message-%d.eml", p->index + 1);
    if (!strcmp(p->ctype, "text/plain")) return xprintf("part-%d.txt", p->index + 1);
    if (!strcmp(p->ctype, "text/html")) return xprintf("part-%d.html", p->index + 1);
    if (!strcmp(p->ctype, "text/calendar")) return xprintf("invite-%d.ics", p->index + 1);
    return xprintf("part-%d.%s", p->index + 1, slash && slash[1] ? slash + 1 : "bin");
}

/* MSG id folder uid [images]: the message ready to show (and marks it read) */
static void cmd_msg(client *c, char **f, int n)
{
    account *a = n > 3 ? lock_account(f[1]) : NULL;
    uint32_t uid;
    char err[512], *data, num[64];
    size_t len;
    mime_msg *m;
    mime_view v;
    buf_t html, text, raw;
    int blocked = 0, images = n > 4 && atoi(f[4]);
    if (!a) { reply_err(c, "NOTFOUND", "no such account"); return; }
    uid = (uint32_t)strtoul(f[3], NULL, 10);
    data = load_message(a, f[2], uid, &len, err, sizeof(err));
    if (!data) { unlock_account(a); reply_err(c, classify(err), err); return; }
    {
        /* mark read (best effort: works offline later through the next sync) */
        folder_cache fc;
        char *dir = folder_dir(a, f[2]);
        msg_entry *e;
        int unread;
        cache_load(&fc, dir);
        free(dir);
        e = cache_find(&fc, uid);
        unread = e && !strchr(e->flags, 'S');
        cache_free(&fc);
        if (unread) {
            flag_args fl = {f[2], uid, 1, "\\Seen", NULL};
            if (run_imap(a, op_flag, &fl, err, sizeof(err)) == 0) update_cached_flag(a, f[2], uid, 'S', 1);
        }
    }
    unlock_account(a);

    m = mime_parse(data, len);
    free(data);
    mime_view_get(m, &v);
    buf_init(&html);
    buf_init(&text);
    buf_init(&raw);
    if (v.html) {
        mime_text(v.html, &raw);
        html_sanitize(&html, buf_str(&raw), raw.len, images, resolve_cid, m, &blocked);
        if (v.text) mime_text(v.text, &text);
        else html_to_text(&text, buf_str(&raw), raw.len);
    } else if (v.text) {
        mime_text(v.text, &text);
        buf_adds(&html, "<div class=\"rb-plain\">");
        text_to_html(&html, buf_str(&text), text.len);
        buf_adds(&html, "</div>");
    }
    out_ok(c);
    out_end(c);
    {
        static const char *H[] = {"From", "To", "Cc", "Reply-To", "Subject", NULL};
        static const char *K[] = {"from", "to", "cc", "replyto", "subject", NULL};
        for (int i = 0; H[i]; i++) {
            char *r = hdr_get(m->root->hdr, m->root->hdr_len, H[i]);
            char *d = decode_header(r);
            buf_adds(&c->out, K[i]);
            out_field(c, d);
            out_end(c);
            free(r);
            free(d);
        }
        {
            char *r = hdr_get(m->root->hdr, m->root->hdr_len, "Date");
            snprintf(num, sizeof(num), "%lld", (long long)parse_rfc5322_date(r));
            buf_adds(&c->out, "date");
            out_field(c, num);
            out_end(c);
            free(r);
        }
        {
            char *mid = hdr_get(m->root->hdr, m->root->hdr_len, "Message-ID");
            char *refs = hdr_get(m->root->hdr, m->root->hdr_len, "References");
            buf_adds(&c->out, "msgid");
            out_field(c, mid);
            out_end(c);
            buf_adds(&c->out, "references");
            out_field(c, refs);
            out_end(c);
            free(mid);
            free(refs);
        }
    }
    buf_adds(&c->out, "blocked");
    out_field(c, blocked ? "1" : "0");
    out_end(c);
    buf_adds(&c->out, "text");
    out_field(c, buf_str(&text));
    out_end(c);
    buf_adds(&c->out, "html");
    out_field(c, buf_str(&html));
    out_end(c);
    for (int i = 0; i < v.natts; i++) {
        mime_part *p = v.atts[i];
        char *nm = part_name(p);
        size_t size = !strcmp(p->encoding, "base64") ? p->body_len * 3 / 4 : p->body_len;
        int inl = p->cid && (!p->disposition || strcmp(p->disposition, "attachment")) && !strncmp(p->ctype, "image/", 6);
        buf_printf(&c->out, "att\t%d", p->index);
        out_field(c, nm);
        out_field(c, p->ctype);
        snprintf(num, sizeof(num), "%lu", (unsigned long)size);
        out_field(c, num);
        out_field(c, inl ? "1" : "0");
        out_end(c);
        free(nm);
    }
    out_end(c);
    buf_free(&html);
    buf_free(&text);
    buf_free(&raw);
    mime_view_free(&v);
    mime_free(m);
}

static void safe_name(char *s)
{
    size_t n = 0;
    for (char *p = s; *p; p++) {
        unsigned char ch = (unsigned char)*p;
        if (ch < 32 || strchr("\\/:*?\"<>|", ch)) *p = '_';
        n++;
    }
    if (n > 120) s[120] = '\0';
    while (*s == '.') *s++ = '_';
}

/* SAVEATT id folder uid index destdir */
static void cmd_saveatt(client *c, char **f, int n)
{
    account *a = n > 5 ? lock_account(f[1]) : NULL;
    char err[512], *data, *path = NULL, *nm;
    size_t len;
    mime_msg *m;
    mime_part *p;
    buf_t raw;
    if (!a) { reply_err(c, n > 5 ? "NOTFOUND" : "ARG", n > 5 ? "no such account" : "SAVEATT id folder uid index dir"); return; }
    data = load_message(a, f[2], (uint32_t)strtoul(f[3], NULL, 10), &len, err, sizeof(err));
    unlock_account(a);
    if (!data) { reply_err(c, classify(err), err); return; }
    m = mime_parse(data, len);
    free(data);
    p = mime_leaf(m, atoi(f[4]));
    if (!p) { mime_free(m); reply_err(c, "NOTFOUND", "no such attachment"); return; }
    nm = part_name(p);
    safe_name(nm);
    if (!*nm) { free(nm); nm = xstrdup("attachment"); }
    make_dirs(f[5]);
    {
        /* don't overwrite: "name (2).ext" */
        char *dot = strrchr(nm, '.');
        for (int i = 1; i < 1000; i++) {
            FILE *probe;
            free(path);
            if (i == 1) path = xprintf("%s/%s", f[5], nm);
            else if (dot) path = xprintf("%s/%.*s (%d)%s", f[5], (int)(dot - nm), nm, i, dot);
            else path = xprintf("%s/%s (%d)", f[5], nm, i);
            probe = rb_fopen(path, "rb");
            if (!probe) break;
            fclose(probe);
        }
    }
#ifdef _WIN32
    for (char *s = path; *s; s++)
        if (*s == '/') *s = '\\';
#endif
    buf_init(&raw);
    mime_decode(p, &raw);
    if (write_file(path, raw.p ? raw.p : "", raw.len) != 0) {
        reply_err(c, "IO", "couldn't write the file");
    } else {
        out_ok(c);
        out_field(c, path);
        out_end(c);
    }
    buf_free(&raw);
    free(path);
    free(nm);
    mime_free(m);
}

/* FLAG id folder uid +S|-S|+F|-F|+A */
static void cmd_flag(client *c, char **f, int n)
{
    account *a = n > 4 ? lock_account(f[1]) : NULL;
    flag_args fl;
    char err[512];
    if (!a) { reply_err(c, n > 4 ? "NOTFOUND" : "ARG", n > 4 ? "no such account" : "FLAG id folder uid +S"); return; }
    fl.folder = f[2];
    fl.uid = (uint32_t)strtoul(f[3], NULL, 10);
    fl.add = f[4][0] == '+';
    fl.flag = f[4][1] == 'S' ? "\\Seen" : f[4][1] == 'F' ? "\\Flagged" : f[4][1] == 'A' ? "\\Answered" : NULL;
    if (!fl.flag) { unlock_account(a); reply_err(c, "ARG", "flag must be S, F or A"); return; }
    if (run_imap(a, op_flag, &fl, err, sizeof(err)) != 0) {
        unlock_account(a);
        reply_err(c, classify(err), err);
        return;
    }
    update_cached_flag(a, f[2], fl.uid, f[4][1], fl.add);
    unlock_account(a);
    out_ok(c);
    out_end(c);
}

static int op_move(account *a, imap *m, void *ctx)
{
    flag_args *fl = ctx;
    (void)a;
    if (fl->add) return imap_delete(m, fl->folder, fl->uid, fl->dest);
    return imap_move(m, fl->folder, fl->uid, fl->dest);
}

static void remove_cached(account *a, const char *folder, uint32_t uid)
{
    folder_cache fc;
    char *dir = folder_dir(a, folder);
    cache_load(&fc, dir);
    free(dir);
    cache_remove(&fc, uid);
    cache_save(&fc);
    cache_free(&fc);
}

/* DELETE id folder uid   /   MOVE id folder uid dest */
static void cmd_move(client *c, char **f, int n, int del)
{
    account *a = n > (del ? 3 : 4) ? lock_account(f[1]) : NULL;
    flag_args fl;
    char err[512], *trash = NULL;
    if (!a) { reply_err(c, "ARG", del ? "DELETE id folder uid" : "MOVE id folder uid dest"); return; }
    fl.folder = f[2];
    fl.uid = (uint32_t)strtoul(f[3], NULL, 10);
    fl.add = del;
    if (del) {
        trash = folder_by_role(a, "trash");
        fl.dest = trash;
    } else {
        fl.dest = f[4];
    }
    if (run_imap(a, op_move, &fl, err, sizeof(err)) != 0) {
        unlock_account(a);
        free(trash);
        reply_err(c, classify(err), err);
        return;
    }
    remove_cached(a, f[2], fl.uid);
    unlock_account(a);
    out_ok(c);
    out_field(c, del && trash && strcmp(trash, f[2]) ? "trash" : del ? "expunged" : "moved");
    out_end(c);
    free(trash);
}

typedef struct { char **v; int n, cap; } addr_list;

static void collect_addr(void *ctx, const char *name, const char *addr)
{
    addr_list *l = ctx;
    (void)name;
    if (!*addr || !strchr(addr, '@')) return;
    if (l->n == l->cap) {
        l->cap = l->cap ? l->cap * 2 : 8;
        l->v = xrealloc(l->v, sizeof(char *) * (size_t)l->cap);
    }
    l->v[l->n++] = xstrdup(addr);
}

typedef struct { const char *folder; const char *data; size_t len; } append_args;

static int op_append(account *a, imap *m, void *ctx)
{
    append_args *ap = ctx;
    (void)a;
    return imap_append(m, ap->folder, "\\Seen", ap->data, ap->len);
}

static int op_find_sent(account *a, imap *m, void *ctx)
{
    folders_ctx fc = {0};
    (void)ctx;
    if (imap_list_folders(m, &fc.f, &fc.n) != 0) return -1;
    save_folders(a, fc.f, fc.n);
    folder_info_free(fc.f, fc.n);
    return 0;
}

/* SEND id, then key<TAB>value lines and an empty line */
static void cmd_send(client *c, char **f, int n)
{
    char *to = NULL, *cc = NULL, *bcc = NULL, *subject = NULL, *body = NULL, *irt = NULL, *refs = NULL;
    char *rfolder = NULL, *msg, *msgid = NULL, err[512];
    uint32_t ruid = 0;
    buf_t line;
    addr_list rc = {0};
    account *a;
    size_t len;
    mime_out o;
    const char *warn = "";
    buf_init(&line);
    for (;;) {
        char *kv[2];
        if (client_readline(c, &line) != 0) { buf_free(&line); return; }
        if (!line.len) break;
        if (split_fields(line.p, kv, 2) < 2) continue;
#define TAKE(k, var) if (!strcmp(kv[0], k)) { free(var); var = xstrdup(kv[1]); continue; }
        TAKE("to", to) TAKE("cc", cc) TAKE("bcc", bcc) TAKE("subject", subject) TAKE("body", body)
        TAKE("in_reply_to", irt) TAKE("references", refs) TAKE("reply_folder", rfolder)
#undef TAKE
        if (!strcmp(kv[0], "reply_uid")) ruid = (uint32_t)strtoul(kv[1], NULL, 10);
    }
    buf_free(&line);
    a = n > 1 ? lock_account(f[1]) : NULL;
    if (!a) { reply_err(c, "NOTFOUND", "no such account"); goto out; }
    addr_parse(to, collect_addr, &rc);
    addr_parse(cc, collect_addr, &rc);
    addr_parse(bcc, collect_addr, &rc);
    if (!rc.n) { unlock_account(a); reply_err(c, "ARG", "add at least one recipient"); goto out; }
    memset(&o, 0, sizeof(o));
    o.from_name = a->name;
    o.from_addr = a->email;
    o.to = to;
    o.cc = cc;
    o.subject = subject;
    o.body = body;
    o.in_reply_to = irt;
    o.references = refs;
    o.msgid_domain = a->email && strchr(a->email, '@') ? strchr(a->email, '@') + 1 : NULL;
    msg = mime_build(&o, &len, &msgid);
    if (smtp_send(a, a->email, rc.v, rc.n, msg, len, err, sizeof(err)) != 0) {
        unlock_account(a);
        free(msg);
        reply_err_side(c, classify(err), err, "smtp");
        goto out;
    }
    rb_log("sent %s to %d recipient(s)", msgid ? msgid : "?", rc.n);
    if (a->save_sent) {
        char *sent = folder_by_role(a, "sent");
        if (!sent && run_imap(a, op_find_sent, NULL, err, sizeof(err)) == 0) sent = folder_by_role(a, "sent");
        if (sent) {
            append_args ap = {sent, msg, len};
            if (run_imap(a, op_append, &ap, err, sizeof(err)) != 0) {
                rb_log("saving to %s failed: %s", sent, err);
                warn = "sent, but couldn't save a copy in Sent";
            }
            free(sent);
        } else {
            warn = "sent (no Sent folder found for a copy)";
        }
    }
    if (rfolder && ruid) {
        flag_args fl = {rfolder, ruid, 1, "\\Answered", NULL};
        if (run_imap(a, op_flag, &fl, err, sizeof(err)) == 0) update_cached_flag(a, rfolder, ruid, 'A', 1);
    }
    unlock_account(a);
    free(msg);
    out_ok(c);
    out_field(c, warn);
    out_end(c);
out:
    for (int i = 0; i < rc.n; i++) free(rc.v[i]);
    free(rc.v);
    free(to); free(cc); free(bcc); free(subject); free(body); free(irt); free(refs); free(rfolder); free(msgid);
}

static void cmd_settings(client *c, char **f, int n)
{
    for (int i = 1; i + 1 < n; i += 2) {
        if (!strcmp(f[i], "sync_minutes")) {
            g_sync_minutes = atoi(f[i + 1]);
            if (g_sync_minutes != 0 && g_sync_minutes < 5) g_sync_minutes = 5;
        }
    }
    out_ok(c);
    {
        char num[16];
        snprintf(num, sizeof(num), "%d", g_sync_minutes);
        out_field(c, num);
    }
    out_end(c);
}

/* ---------------- background sync ---------------- */

static void sync_thread(void *arg)
{
    time_t last = 0;
    (void)arg;
    while (!g_quit) {
        PR_Sleep(PR_SecondsToInterval(5));
        if (!g_sync_minutes || time(NULL) - last < g_sync_minutes * 60) continue;
        last = time(NULL);
        for (int i = 0;; i++) {
            account *a;
            char id[64], err[512];
            sync_args s = {0};
            int k = 0;
            PR_Lock(g_lock);
            for (a = store_accounts(); a && k < i; a = a->next) k++;
            if (a) snprintf(id, sizeof(id), "%s", a->id);
            PR_Unlock(g_lock);
            if (!a) break;
            a = lock_account(id);
            if (!a) continue;
            s.folder = "INBOX";
            if (run_imap(a, op_sync, &s, err, sizeof(err)) == 0) {
                a->last_sync = time(NULL);
                if (s.unseen >= 0) a->unseen = s.unseen;
                set_last_error(a, NULL);
                if (s.added) rb_log("sync %s: %d new", a->id, s.added);
            } else {
                set_last_error(a, err);
                rb_log("sync %s failed: %s", a->id, err);
            }
            unlock_account(a);
        }
    }
}

/* ---------------- connection loop ---------------- */

static void serve(void *arg)
{
    client *c = arg;
    buf_t line;
    int authed = 0;
    buf_init(&line);
    buf_init(&c->out);
    while (!g_quit && client_readline(c, &line) == 0) {
        char *f[64];
        int n = split_fields(line.p, f, 64);
        if (!authed) {
            if (n >= 2 && !strcmp(f[0], "AUTH") && g_token && !strcmp(f[1], g_token)) {
                authed = 1;
                buf_printf(&c->out, "OK\trbmaild\t%s\t%d\n", RB_VERSION, (int)getpid());
            } else {
                buf_adds(&c->out, "ERR\tAUTH\tbad key\n");
                flush(c);
                break;
            }
        } else if (!strcmp(f[0], "PING")) {
            buf_printf(&c->out, "OK\trbmaild\t%s\t%d\n", RB_VERSION, (int)getpid());
        } else if (!strcmp(f[0], "ACCOUNTS")) cmd_accounts(c);
        else if (!strcmp(f[0], "GET")) cmd_get(c, f, n);
        else if (!strcmp(f[0], "SAVE")) cmd_save(c, f, n);
        else if (!strcmp(f[0], "DELACCT")) cmd_delacct(c, f, n);
        else if (!strcmp(f[0], "FOLDERS")) cmd_folders(c, f, n);
        else if (!strcmp(f[0], "SYNC")) cmd_sync(c, f, n, 0);
        else if (!strcmp(f[0], "MORE")) cmd_sync(c, f, n, 1);
        else if (!strcmp(f[0], "LIST")) cmd_list(c, f, n);
        else if (!strcmp(f[0], "MSG")) cmd_msg(c, f, n);
        else if (!strcmp(f[0], "SAVEATT")) cmd_saveatt(c, f, n);
        else if (!strcmp(f[0], "FLAG")) cmd_flag(c, f, n);
        else if (!strcmp(f[0], "DELETE")) cmd_move(c, f, n, 1);
        else if (!strcmp(f[0], "MOVE")) cmd_move(c, f, n, 0);
        else if (!strcmp(f[0], "SEND")) cmd_send(c, f, n);
        else if (!strcmp(f[0], "SETTINGS")) cmd_settings(c, f, n);
        else if (!strcmp(f[0], "QUIT")) {
            buf_adds(&c->out, "OK\n");
            flush(c);
            g_quit = 1;   /* the accept loop notices within a second */
            break;
        } else {
            reply_err(c, "ARG", "unknown command");
        }
        if (flush(c) != 0) break;
    }
    buf_free(&line);
    buf_free(&c->out);
    PR_Close(c->fd);
    free(c);
}

static void exe_dir(char *buf, size_t len)
{
#ifdef _WIN32
    char *slash;
    GetModuleFileNameA(NULL, buf, (DWORD)len);
    slash = strrchr(buf, '\\');
    if (slash) slash[0] = '\0';
#else
    snprintf(buf, len, ".");
#endif
}

/* RetrobirdMain (built with -DRB_DLL as rbmail.dll), which the app calls on a thread of its own so
   the engine runs inside the app's process; or main, for the Linux test build. */
static int rb_main(int argc, char **argv)
{
    int port = RB_DEFAULT_PORT;
    char dir[512], data[600], roots[600];
    PRNetAddr addr;
    PRSocketOptionData opt;
    exe_dir(dir, sizeof(dir));
#ifdef _WIN32
    snprintf(data, sizeof(data), "%s\\data", dir);
    snprintf(roots, sizeof(roots), "%s\\nssckbi.dll", dir);
#else
    snprintf(data, sizeof(data), "./rbdata");
    snprintf(roots, sizeof(roots), "libnssckbi.so");
#endif
    for (int i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--token") && i + 1 < argc) g_token = argv[++i];
        else if (!strcmp(argv[i], "--port") && i + 1 < argc) port = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--data") && i + 1 < argc) snprintf(data, sizeof(data), "%s", argv[++i]);
        else if (!strcmp(argv[i], "--roots") && i + 1 < argc) snprintf(roots, sizeof(roots), "%s", argv[++i]);
        else if (!strcmp(argv[i], "--sync-minutes") && i + 1 < argc) g_sync_minutes = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--debug")) rb_debug = 1;
        else {
            fprintf(stderr, "usage: rbmaild --token KEY [--port %d] [--data DIR] [--roots nssckbi] "
                            "[--sync-minutes 15] [--debug]\n", RB_DEFAULT_PORT);
            return 2;
        }
    }
    if (!g_token || strlen(g_token) < 8) {
        fprintf(stderr, "rbmaild: --token with at least 8 characters is required\n");
        return 2;
    }
    if (net_init(roots) != 0) return 1;
    g_lock = PR_NewLock();
    store_init(data, g_token);
    rb_log("rbmaild %s starting on 127.0.0.1:%d, data %s", RB_VERSION, port, data);

    g_listener = PR_OpenTCPSocket(PR_AF_INET);
    opt.option = PR_SockOpt_Reuseaddr;
    opt.value.reuse_addr = PR_TRUE;
    PR_SetSocketOption(g_listener, &opt);
    PR_InitializeNetAddr(PR_IpAddrLoopback, (PRUint16)port, &addr);
    {
        /* a helper that was just told to QUIT may still hold the port for a moment */
        int bound = 0;
        for (int i = 0; i < 25 && !bound; i++) {
            bound = PR_Bind(g_listener, &addr) == PR_SUCCESS;
            if (!bound) PR_Sleep(PR_MillisecondsToInterval(200));
        }
        if (!bound || PR_Listen(g_listener, 8) != PR_SUCCESS) {
            rb_log("can't listen on 127.0.0.1:%d (already running?)", port);
            return 1;
        }
    }
    PR_CreateThread(PR_USER_THREAD, sync_thread, NULL, PR_PRIORITY_LOW, PR_GLOBAL_THREAD,
                    PR_UNJOINABLE_THREAD, 0);
    while (!g_quit) {
        PRNetAddr peer;
        PRFileDesc *fd = PR_Accept(g_listener, &peer, PR_SecondsToInterval(1));
        client *c;
        if (!fd) continue;   /* timeout: check g_quit */
        c = xcalloc(1, sizeof(*c));
        c->fd = fd;
        if (!PR_CreateThread(PR_USER_THREAD, serve, c, PR_PRIORITY_NORMAL, PR_GLOBAL_THREAD,
                             PR_UNJOINABLE_THREAD, 256 * 1024)) {
            PR_Close(fd);
            free(c);
        }
    }
    rb_log("rbmaild stopping");
    PR_Close(g_listener);
    return 0;
}

#ifdef RB_DLL
#ifdef _WIN32
__declspec(dllexport)
#endif
int RetrobirdMain(int argc, char **argv) { return rb_main(argc, argv); }
#else
int main(int argc, char **argv) { return rb_main(argc, argv); }
#endif
