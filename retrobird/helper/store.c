/* Accounts (accounts.conf, passwords sealed with the app's token), the per-folder header cache,
   file helpers and the log. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"
#include "nspr.h"

static char *g_dir;
static char *g_token;
static account *g_accounts;
static PRLock *g_loglock;
static FILE *g_log;
int rb_debug;

void rb_log(const char *fmt, ...)
{
    va_list ap;
    time_t t = time(NULL);
    struct tm tmv;
    char ts[32];
#ifdef _WIN32
    localtime_s(&tmv, &t);
#else
    localtime_r(&t, &tmv);
#endif
    strftime(ts, sizeof(ts), "%Y-%m-%d %H:%M:%S", &tmv);
    if (g_loglock) PR_Lock(g_loglock);
    if (g_log) {
        fprintf(g_log, "%s ", ts);
        va_start(ap, fmt);
        vfprintf(g_log, fmt, ap);
        va_end(ap);
        fputc('\n', g_log);
        fflush(g_log);
    }
    if (rb_debug || !g_log) {
        fprintf(stderr, "%s ", ts);
        va_start(ap, fmt);
        vfprintf(stderr, fmt, ap);
        va_end(ap);
        fputc('\n', stderr);
    }
    if (g_loglock) PR_Unlock(g_loglock);
}

/* ---------------- files ---------------- */

/* Paths are UTF-8 (attachment names can be anything); Windows needs the wide-char calls. */
#ifdef _WIN32
#include <windows.h>
static WCHAR *wide(const char *s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    WCHAR *w = xmalloc(sizeof(WCHAR) * (size_t)(n > 0 ? n : 1));
    if (n <= 0 || !MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n)) w[0] = 0;
    for (WCHAR *p = w; *p; p++)
        if (*p == '/') *p = '\\';
    return w;
}

FILE *rb_fopen(const char *path, const char *mode)
{
    WCHAR *w = wide(path), m[8];
    FILE *f;
    int i;
    for (i = 0; mode[i] && i < 7; i++) m[i] = (WCHAR)mode[i];
    m[i] = 0;
    f = _wfopen(w, m);
    free(w);
    return f;
}

static int rb_delete(const char *path)
{
    WCHAR *w = wide(path);
    BOOL ok = DeleteFileW(w);
    free(w);
    return ok ? 0 : -1;
}

static int rb_rename(const char *from, const char *to)
{
    WCHAR *a = wide(from), *b = wide(to);
    BOOL ok;
    DeleteFileW(b);
    ok = MoveFileW(a, b);
    free(a);
    free(b);
    return ok ? 0 : -1;
}
#else
FILE *rb_fopen(const char *path, const char *mode) { return fopen(path, mode); }
static int rb_delete(const char *path) { return PR_Delete(path) == PR_SUCCESS ? 0 : -1; }
static int rb_rename(const char *from, const char *to)
{
    PR_Delete(to);
    return PR_Rename(from, to) == PR_SUCCESS ? 0 : -1;
}
#endif

char *read_file(const char *path, size_t *len)
{
    FILE *f = rb_fopen(path, "rb");
    buf_t b;
    char tmp[65536];
    size_t n;
    if (!f) return NULL;
    buf_init(&b);
    while ((n = fread(tmp, 1, sizeof(tmp), f)) > 0) buf_add(&b, tmp, n);
    fclose(f);
    if (len) *len = b.len;
    return buf_detach(&b);
}

int write_file(const char *path, const void *data, size_t len)
{
    char *tmp = xprintf("%s.tmp", path);
    FILE *f = rb_fopen(tmp, "wb");
    int ok;
    if (!f) { free(tmp); return -1; }
    ok = fwrite(data, 1, len, f) == len;
    ok &= fclose(f) == 0;
    if (ok) ok = rb_rename(tmp, path) == 0;
    if (!ok) rb_delete(tmp);
    free(tmp);
    return ok ? 0 : -1;
}

int make_dirs(const char *path)
{
    char *p = xstrdup(path);
    for (char *s = p + 1; *s; s++) {
        if (*s == '/' || *s == '\\') {
            char c = *s;
            if (s[-1] == ':') continue;   /* "C:\" */
            *s = '\0';
            PR_MkDir(p, 0700);
            *s = c;
        }
    }
    PR_MkDir(p, 0700);
    {
        PRFileInfo fi;
        int ok = PR_GetFileInfo(p, &fi) == PR_SUCCESS && fi.type == PR_FILE_DIRECTORY;
        free(p);
        return ok ? 0 : -1;
    }
}

int remove_tree(const char *path)
{
    PRDir *d = PR_OpenDir(path);
    PRDirEntry *e;
    if (!d) return PR_Delete(path) == PR_SUCCESS ? 0 : -1;
    while ((e = PR_ReadDir(d, PR_SKIP_BOTH)) != NULL) {
        char *sub = xprintf("%s/%s", path, e->name);
        PRFileInfo fi;
        if (PR_GetFileInfo(sub, &fi) == PR_SUCCESS && fi.type == PR_FILE_DIRECTORY) remove_tree(sub);
        else PR_Delete(sub);
        free(sub);
    }
    PR_CloseDir(d);
    return PR_RmDir(path) == PR_SUCCESS ? 0 : -1;
}

/* ---------------- accounts ---------------- */

static const char *sec_name(int s) { return s == SEC_STARTTLS ? "starttls" : s == SEC_PLAIN ? "plain" : "tls"; }

static int sec_parse(const char *v)
{
    if (strieq(v, "starttls")) return SEC_STARTTLS;
    if (strieq(v, "plain") || strieq(v, "none")) return SEC_PLAIN;
    return SEC_TLS;
}

static void set_str(char **dst, const char *v)
{
    free(*dst);
    *dst = v && *v ? xstrdup(v) : NULL;
}

int account_set(account *a, const char *key, const char *val)
{
    if (!strcmp(key, "name")) set_str(&a->name, val);
    else if (!strcmp(key, "email")) set_str(&a->email, val);
    else if (!strcmp(key, "imap_host")) set_str(&a->imap_host, val);
    else if (!strcmp(key, "imap_port")) a->imap_port = atoi(val);
    else if (!strcmp(key, "imap_sec")) a->imap_sec = sec_parse(val);
    else if (!strcmp(key, "smtp_host")) set_str(&a->smtp_host, val);
    else if (!strcmp(key, "smtp_port")) a->smtp_port = atoi(val);
    else if (!strcmp(key, "smtp_sec")) a->smtp_sec = sec_parse(val);
    else if (!strcmp(key, "user")) set_str(&a->user, val);
    else if (!strcmp(key, "pass")) {
        if (a->pass) memset(a->pass, 0, strlen(a->pass));
        set_str(&a->pass, val);
    }
    else if (!strcmp(key, "smtp_user")) set_str(&a->smtp_user, val);
    else if (!strcmp(key, "imap_pin")) set_str(&a->imap_pin, val);
    else if (!strcmp(key, "smtp_pin")) set_str(&a->smtp_pin, val);
    else if (!strcmp(key, "save_sent")) a->save_sent = atoi(val);
    else if (!strcmp(key, "auth")) {
        int m = val && !strcmp(val, "microsoft") ? AUTH_MICROSOFT : AUTH_PASSWORD;
        if (m != a->auth) { free(a->access); a->access = NULL; a->access_exp = 0; }
        a->auth = m;
    }
    else if (!strcmp(key, "oauth_client")) set_str(&a->oauth_client, val);
    else return -1;
    return 0;
}

account *store_new_account(void)
{
    account *a = xcalloc(1, sizeof(*a));
    a->imap_port = 993;
    a->smtp_port = 465;
    a->save_sent = 1;
    a->lock = PR_NewLock();
    return a;
}

void account_free(account *a)
{
    if (!a) return;
    if (a->im) imap_close(a->im);
    if (a->pass) memset(a->pass, 0, strlen(a->pass));
    free(a->id); free(a->name); free(a->email); free(a->imap_host); free(a->smtp_host);
    free(a->user); free(a->pass); free(a->smtp_user); free(a->imap_pin); free(a->smtp_pin);
    free(a->last_error); free(a->oauth_client);
    if (a->access) memset(a->access, 0, strlen(a->access));
    free(a->access);
    if (a->lock) PR_DestroyLock(a->lock);
    free(a);
}

static void put(buf_t *b, const char *k, const char *v)
{
    if (!v || !*v) return;
    buf_printf(b, "%s=", k);
    field_escape(b, v);
    buf_addc(b, '\n');
}

/* Callers hold the account-list lock and no account lock. */
int store_save(void)
{
    g_store_dirty = 0;
    buf_t b;
    char *path = xprintf("%s/accounts.conf", g_dir);
    int rc;
    buf_init(&b);
    buf_adds(&b, "# Retrobird accounts. Passwords are sealed with the app's key.\n");
    for (account *a = g_accounts; a; a = a->next) {
        char num[16];
        buf_adds(&b, "\n[account]\n");
        put(&b, "id", a->id);
        put(&b, "name", a->name);
        put(&b, "email", a->email);
        put(&b, "imap_host", a->imap_host);
        snprintf(num, sizeof(num), "%d", a->imap_port);
        put(&b, "imap_port", num);
        put(&b, "imap_sec", sec_name(a->imap_sec));
        put(&b, "smtp_host", a->smtp_host);
        snprintf(num, sizeof(num), "%d", a->smtp_port);
        put(&b, "smtp_port", num);
        put(&b, "smtp_sec", sec_name(a->smtp_sec));
        put(&b, "user", a->user);
        put(&b, "smtp_user", a->smtp_user);
        put(&b, "imap_pin", a->imap_pin);
        put(&b, "smtp_pin", a->smtp_pin);
        snprintf(num, sizeof(num), "%d", a->save_sent);
        put(&b, "save_sent", num);
        if (a->auth == AUTH_MICROSOFT) put(&b, "auth", "microsoft");
        put(&b, "oauth_client", a->oauth_client);
        /* the refresh token in pass can change under the account lock (oauth.c) */
        if (a->lock) PR_Lock(a->lock);
        if (a->pass) {
            char *sealed = secret_seal(g_token, a->pass);
            put(&b, "pass_sealed", sealed);
            free(sealed);
        }
        if (a->lock) PR_Unlock(a->lock);
    }
    rc = write_file(path, b.p, b.len);
    free(path);
    buf_free(&b);
    return rc;
}

static void load_accounts(void)
{
    char *path = xprintf("%s/accounts.conf", g_dir);
    char *data = read_file(path, NULL), *line, *next;
    account *cur = NULL, **tail = &g_accounts;
    free(path);
    if (!data) return;
    for (line = data; line && *line; line = next) {
        char *eq;
        next = strchr(line, '\n');
        if (next) *next++ = '\0';
        if (line[0] == '#' || !line[0]) continue;
        if (!strcmp(str_trim(line), "[account]")) {
            cur = store_new_account();
            *tail = cur;
            tail = &cur->next;
            continue;
        }
        eq = strchr(line, '=');
        if (!cur || !eq) continue;
        *eq = '\0';
        field_unescape_inplace(eq + 1);
        if (!strcmp(line, "id")) set_str(&cur->id, eq + 1);
        else if (!strcmp(line, "pass_sealed")) {
            cur->pass = secret_open(g_token, eq + 1);
            if (!cur->pass) rb_log("account %s: stored password can't be opened with this key", cur->id ? cur->id : "?");
        } else account_set(cur, line, eq + 1);
    }
    free(data);
}

void store_init(const char *datadir, const char *token)
{
    char *logpath;
    g_dir = xstrdup(datadir);
    g_token = xstrdup(token ? token : "");
    make_dirs(g_dir);
    g_loglock = PR_NewLock();
    logpath = xprintf("%s/rbmaild.log", g_dir);
    {
        PRFileInfo fi;
        if (PR_GetFileInfo(logpath, &fi) == PR_SUCCESS && fi.size > 1024 * 1024) {
            char *old = xprintf("%s.old", logpath);
            PR_Delete(old);
            PR_Rename(logpath, old);
            free(old);
        }
    }
    g_log = fopen(logpath, "a");
    free(logpath);
    load_accounts();
}

const char *store_dir(void) { return g_dir; }
account *store_accounts(void) { return g_accounts; }

account *store_find(const char *id)
{
    for (account *a = g_accounts; a; a = a->next)
        if (a->id && !strcmp(a->id, id)) return a;
    return NULL;
}

void store_add(account *a)
{
    account **t = &g_accounts;
    while (*t) t = &(*t)->next;
    a->next = NULL;
    *t = a;
}

void store_remove(const char *id)
{
    for (account **t = &g_accounts; *t; t = &(*t)->next) {
        if ((*t)->id && !strcmp((*t)->id, id)) {
            account *a = *t;
            char *dir = xprintf("%s/mail/%s", g_dir, id);
            *t = a->next;
            remove_tree(dir);
            free(dir);
            account_free(a);
            return;
        }
    }
}

/* ---------------- folder cache ---------------- */

char *folder_dir(account *a, const char *folder)
{
    buf_t b;
    buf_init(&b);
    buf_printf(&b, "%s/mail/%s/", g_dir, a->id);
    hex_encode(&b, folder, strlen(folder) > 100 ? 100 : strlen(folder));
    return buf_detach(&b);
}

void entry_free(msg_entry *e)
{
    free(e->from);
    free(e->to);
    free(e->subject);
    free(e->msgid);
    free(e->preview);
}

void cache_clear(folder_cache *fc)
{
    for (int i = 0; i < fc->n; i++) entry_free(&fc->e[i]);
    fc->n = 0;
    fc->uidvalidity = fc->uidnext = fc->exists = 0;
    if (fc->dir) {
        /* drop cached message bodies too: uids no longer mean the same messages */
        PRDir *d = PR_OpenDir(fc->dir);
        PRDirEntry *de;
        if (d) {
            while ((de = PR_ReadDir(d, PR_SKIP_BOTH)) != NULL) {
                size_t l = strlen(de->name);
                if (l > 4 && !strcmp(de->name + l - 4, ".eml")) {
                    char *p = xprintf("%s/%s", fc->dir, de->name);
                    PR_Delete(p);
                    free(p);
                }
            }
            PR_CloseDir(d);
        }
    }
}

void cache_free(folder_cache *fc)
{
    for (int i = 0; i < fc->n; i++) entry_free(&fc->e[i]);
    free(fc->e);
    free(fc->dir);
    memset(fc, 0, sizeof(*fc));
}

void cache_load(folder_cache *fc, const char *dir)
{
    char *path = xprintf("%s/index", dir);
    char *data = read_file(path, NULL), *line, *next;
    memset(fc, 0, sizeof(*fc));
    fc->dir = xstrdup(dir);
    free(path);
    if (!data) return;
    for (line = data; line && *line; line = next) {
        char *f[11];
        int n;
        next = strchr(line, '\n');
        if (next) *next++ = '\0';
        n = split_fields(line, f, 11);
        if (!strcmp(f[0], "RBIDX1")) {
            if (n >= 4) {
                fc->uidvalidity = (uint32_t)strtoul(f[1], NULL, 10);
                fc->uidnext = (uint32_t)strtoul(f[2], NULL, 10);
                fc->exists = (uint32_t)strtoul(f[3], NULL, 10);
            }
            continue;
        }
        if (n < 9) continue;
        {
            msg_entry *e = cache_insert(fc, (uint32_t)strtoul(f[0], NULL, 10));
            snprintf(e->flags, sizeof(e->flags), "%s", f[1]);
            e->date = strtoll(f[2], NULL, 10);
            e->size = (uint32_t)strtoul(f[3], NULL, 10);
            e->att = atoi(f[4]);
            e->from = xstrdup(f[5]);
            e->to = xstrdup(f[6]);
            e->subject = xstrdup(f[7]);
            e->msgid = xstrdup(f[8]);
            /* a tenth field (from 0.7.2) is the preview; older indexes have none yet */
            e->preview = n >= 10 ? xstrdup(f[9]) : NULL;
        }
    }
    free(data);
}

int cache_save(folder_cache *fc)
{
    buf_t b;
    char *path;
    int rc;
    if (!fc->dir) return -1;
    make_dirs(fc->dir);
    buf_init(&b);
    buf_printf(&b, "RBIDX1\t%u\t%u\t%u\n", fc->uidvalidity, fc->uidnext, fc->exists);
    for (int i = 0; i < fc->n; i++) {
        msg_entry *e = &fc->e[i];
        buf_printf(&b, "%u\t%s\t%lld\t%u\t%d\t", e->uid, e->flags, (long long)e->date, e->size, e->att);
        field_escape(&b, e->from);
        buf_addc(&b, '\t');
        field_escape(&b, e->to);
        buf_addc(&b, '\t');
        field_escape(&b, e->subject);
        buf_addc(&b, '\t');
        field_escape(&b, e->msgid);
        if (e->preview) {
            buf_addc(&b, '\t');
            field_escape(&b, e->preview);
        }
        buf_addc(&b, '\n');
    }
    path = xprintf("%s/index", fc->dir);
    rc = write_file(path, b.p, b.len);
    free(path);
    buf_free(&b);
    return rc;
}

static int find_pos(folder_cache *fc, uint32_t uid, int *found)
{
    int lo = 0, hi = fc->n;
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        if (fc->e[mid].uid < uid) lo = mid + 1;
        else hi = mid;
    }
    *found = lo < fc->n && fc->e[lo].uid == uid;
    return lo;
}

msg_entry *cache_find(folder_cache *fc, uint32_t uid)
{
    int found, pos = find_pos(fc, uid, &found);
    return found ? &fc->e[pos] : NULL;
}

msg_entry *cache_insert(folder_cache *fc, uint32_t uid)
{
    int found, pos = find_pos(fc, uid, &found);
    if (found) return &fc->e[pos];
    if (fc->n == fc->cap) {
        fc->cap = fc->cap ? fc->cap * 2 : 128;
        fc->e = xrealloc(fc->e, sizeof(msg_entry) * (size_t)fc->cap);
    }
    memmove(&fc->e[pos + 1], &fc->e[pos], sizeof(msg_entry) * (size_t)(fc->n - pos));
    memset(&fc->e[pos], 0, sizeof(msg_entry));
    fc->e[pos].uid = uid;
    fc->n++;
    return &fc->e[pos];
}

void cache_remove(folder_cache *fc, uint32_t uid)
{
    int found, pos = find_pos(fc, uid, &found);
    if (!found) return;
    entry_free(&fc->e[pos]);
    memmove(&fc->e[pos], &fc->e[pos + 1], sizeof(msg_entry) * (size_t)(fc->n - pos - 1));
    fc->n--;
    if (fc->dir) {
        char *p = xprintf("%s/%u.eml", fc->dir, uid);
        PR_Delete(p);
        free(p);
    }
}
