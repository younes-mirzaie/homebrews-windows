/* rbmaild: Retrobird's native mail helper. Shared declarations.
   Portable C on NSPR/NSS: builds for the phone (clang-cl, ARM, static NSS from retrofox) and for
   Linux (system NSS) so the whole mail stack can be tested without a phone. */
#ifndef RB_H
#define RB_H

#include <stddef.h>
#include <stdarg.h>
#include <stdint.h>
#include <time.h>

#define RB_VERSION "1"
#define RB_DEFAULT_PORT 8096

#if defined(__GNUC__) || defined(__clang__)
#define RB_PRINTF(a, b) __attribute__((format(printf, a, b)))
#else
#define RB_PRINTF(a, b)
#endif

/* ---- buf.c: growable byte buffers and small string helpers ---- */
typedef struct {
    char *p;      /* always NUL-terminated when non-NULL */
    size_t len, cap;
} buf_t;

void buf_init(buf_t *b);
void buf_free(buf_t *b);
void buf_clear(buf_t *b);
void buf_add(buf_t *b, const void *data, size_t n);
void buf_adds(buf_t *b, const char *s);
void buf_addc(buf_t *b, int c);
void buf_printf(buf_t *b, const char *fmt, ...) RB_PRINTF(2, 3);
void buf_vprintf(buf_t *b, const char *fmt, va_list ap);
char *buf_detach(buf_t *b);           /* returns the string (never NULL), leaves b empty */
const char *buf_str(buf_t *b);        /* "" when empty */

void *xmalloc(size_t n);
void *xcalloc(size_t n, size_t m);
void *xrealloc(void *p, size_t n);
char *xstrdup(const char *s);
char *xstrndup(const char *s, size_t n);
char *xprintf(const char *fmt, ...) RB_PRINTF(1, 2);
int ascii_lower(int c);
int strieq(const char *a, const char *b);
int strnieq(const char *a, const char *b, size_t n);
const char *stristr(const char *hay, const char *needle);
char *str_trim(char *s);              /* in place, returns s moved past leading space */
void str_lower(char *s);
int starts_with_i(const char *s, const char *prefix);

/* Fields on the app protocol are TAB-separated; values escape \\ \t \n \r. */
void field_escape(buf_t *out, const char *s);
void field_escape_n(buf_t *out, const char *s, size_t n);
void field_unescape_inplace(char *s);
int split_fields(char *line, char **out, int max);   /* splits on TAB and unescapes each */

/* ---- codec.c ---- */
void b64_encode(buf_t *out, const void *data, size_t n, int wrap76);
void b64_decode(buf_t *out, const char *in, size_t n);
void qp_decode(buf_t *out, const char *in, size_t n, int header);
void qp_encode(buf_t *out, const char *in, size_t n);
void hex_encode(buf_t *out, const void *data, size_t n);
int hex_decode(buf_t *out, const char *hex);

/* Converts text in any common charset to UTF-8. Unknown charsets are treated as UTF-8 if valid,
   otherwise windows-1252. Invalid UTF-8 becomes U+FFFD. Returns 0 when the charset was known. */
int charset_to_utf8(buf_t *out, const char *charset, const char *in, size_t n);
void utf8_sanitize(buf_t *out, const char *in, size_t n);
int utf8_valid(const char *in, size_t n);
int is_ascii(const char *s);
void utf8_put(buf_t *out, uint32_t cp);

char *decode_header(const char *raw);   /* unfold + RFC 2047 encoded words, result UTF-8 */
void encode_header_text(buf_t *out, const char *utf8, size_t used);  /* RFC 2047 if needed */
char *imap_mutf7_decode(const char *s);  /* IMAP folder names (modified UTF-7) to UTF-8 */

void html_to_text(buf_t *out, const char *html, size_t n);
/* Makes HTML mail safe to show: drops scripts, event handlers, forms and frames, turns cid:
   references into data: URIs via the callback and blocks remote images unless allowed.
   *blocked is set when remote content was removed. */
typedef int (*cid_resolver)(void *ctx, const char *cid, buf_t *data_uri);
void html_sanitize(buf_t *out, const char *html, size_t n, int allow_remote,
                   cid_resolver resolve, void *ctx, int *blocked);
void text_to_html(buf_t *out, const char *text, size_t n);

/* ---- mime.c ---- */
typedef struct mime_part {
    char *ctype;          /* lower case, e.g. "text/plain"; default "text/plain" */
    char *charset;
    char *filename;       /* decoded UTF-8, or NULL */
    char *disposition;    /* lower case "attachment"/"inline" or NULL */
    char *cid;            /* Content-ID without <> */
    char *encoding;       /* lower case transfer encoding */
    const char *hdr;      /* raw header block (slice of the message) */
    size_t hdr_len;
    const char *body;     /* raw encoded body (slice) */
    size_t body_len;
    int index;            /* depth-first leaf number, used for attachments */
    struct mime_part *child, *next;
} mime_part;

typedef struct {
    char *data;           /* owned copy of the message */
    size_t len;
    mime_part *root;
    int nleaves;
} mime_msg;

mime_msg *mime_parse(const char *data, size_t len);
void mime_free(mime_msg *m);
char *hdr_get(const char *hdr, size_t len, const char *name);      /* unfolded raw value or NULL */
char *hdr_param(const char *value, const char *param);              /* RFC 2231 aware */
void mime_decode(const mime_part *p, buf_t *out);                   /* transfer-decoded bytes */
void mime_text(const mime_part *p, buf_t *out);                     /* decoded + to UTF-8 */
mime_part *mime_leaf(mime_msg *m, int index);

typedef struct {
    mime_part *text, *html;
    mime_part **atts;
    int natts;
} mime_view;
void mime_view_get(mime_msg *m, mime_view *v);
void mime_view_free(mime_view *v);

/* address lists: calls fn for each "name", "addr" (UTF-8, decoded) */
typedef void (*addr_fn)(void *ctx, const char *name, const char *addr);
void addr_parse(const char *decoded, addr_fn fn, void *ctx);

typedef struct {
    const char *from_name, *from_addr;
    const char *to, *cc;          /* comma-separated, as typed */
    const char *subject, *body;   /* UTF-8 */
    const char *in_reply_to, *references;
    const char *msgid_domain;
} mime_out;
char *mime_build(const mime_out *o, size_t *len, char **msgid);
char *rfc5322_date(time_t t);
time_t parse_rfc5322_date(const char *s);
time_t parse_imap_date(const char *s);  /* "17-Jul-1996 02:44:25 -0700" */

/* ---- net.c ---- */
typedef struct conn conn;
enum { SEC_TLS = 0, SEC_STARTTLS = 1, SEC_PLAIN = 2 };
int net_init(const char *roots_module);   /* NSS + root store; 0 on success */
void net_shutdown(void);
/* pin: hex SHA-256 of a server certificate to accept even if it doesn't verify (self-signed). */
conn *conn_open(const char *host, int port, int tls, const char *pin, char *err, size_t errlen);
int conn_starttls(conn *c, char *err, size_t errlen);
int conn_write(conn *c, const void *data, size_t n);
int conn_puts(conn *c, const char *s);
int conn_readline(conn *c, buf_t *line);     /* without CRLF; -1 on EOF/error */
int conn_read(conn *c, buf_t *out, size_t n);
void conn_close(conn *c);
const char *conn_error(conn *c);
const char *conn_tls_info(conn *c);           /* "TLS 1.3 TLS_AES_128_GCM_SHA256" or "" */
void conn_set_timeout(conn *c, int seconds);
void sha256_hex(const void *data, size_t n, char out[65]);
int random_bytes(void *out, size_t n);
/* AES-256-GCM with a key derived from the app's token: protects passwords at rest. */
char *secret_seal(const char *token, const char *plain);
char *secret_open(const char *token, const char *sealed);

/* ---- accounts / store (store.c) ---- */
typedef struct account {
    char *id, *name, *email;
    char *imap_host; int imap_port, imap_sec;
    char *smtp_host; int smtp_port, smtp_sec;
    char *user, *pass;           /* pass decrypted in memory only */
    char *smtp_user;             /* NULL: same as user */
    char *imap_pin, *smtp_pin;
    int save_sent;
    struct imap *im;             /* open connection, guarded by lock */
    void *lock;                  /* PRLock */
    time_t last_sync;
    int unseen, recent_new;
    char *last_error;
    struct account *next;
} account;

typedef struct {
    uint32_t uid;
    char flags[8];               /* S seen, F flagged, A answered, D deleted, T draft */
    int64_t date;
    uint32_t size;
    int att;
    char *from, *to, *subject, *msgid;
} msg_entry;

typedef struct {
    char *dir;
    uint32_t uidvalidity, uidnext;
    uint32_t exists;
    msg_entry *e;                /* sorted by uid ascending */
    int n, cap;
} folder_cache;

void store_init(const char *datadir, const char *token);
const char *store_dir(void);
account *store_accounts(void);
account *store_find(const char *id);
int store_save(void);
account *store_new_account(void);
void store_add(account *a);
void store_remove(const char *id);
int account_set(account *a, const char *key, const char *val);
void account_free(account *a);
char *folder_dir(account *a, const char *folder);
void cache_load(folder_cache *fc, const char *dir);
int cache_save(folder_cache *fc);
void cache_free(folder_cache *fc);
void cache_clear(folder_cache *fc);
msg_entry *cache_find(folder_cache *fc, uint32_t uid);
msg_entry *cache_insert(folder_cache *fc, uint32_t uid);
void cache_remove(folder_cache *fc, uint32_t uid);
void entry_free(msg_entry *e);
#include <stdio.h>
FILE *rb_fopen(const char *path, const char *mode);   /* UTF-8 path */
char *read_file(const char *path, size_t *len);
int write_file(const char *path, const void *data, size_t len);
int make_dirs(const char *path);
int remove_tree(const char *path);
void rb_log(const char *fmt, ...) RB_PRINTF(1, 2);
extern int rb_debug;

/* ---- imap.c ---- */
typedef enum { IN_NIL, IN_ATOM, IN_STR, IN_LIST } inode_type;
typedef struct inode {
    inode_type t;
    char *s;                     /* atom or string bytes (NUL-terminated) */
    size_t len;
    struct inode *kids;          /* list items */
    int n;
} inode;

typedef struct imap {
    conn *c;
    int tag;
    char caps[1024];
    char *selected;
    uint32_t uidvalidity, uidnext, exists;
    int readonly;
    char err[512];
    int io_error;
    time_t last_used;
} imap;

typedef void (*imap_untagged_fn)(void *ctx, inode *resp, int n);
imap *imap_open(account *a, char *err, size_t errlen);
void imap_close(imap *m);
int imap_has_cap(imap *m, const char *cap);
int imap_cmd(imap *m, imap_untagged_fn fn, void *ctx, const char *fmt, ...) RB_PRINTF(4, 5);
int imap_select(imap *m, const char *folder);
int imap_append(imap *m, const char *folder, const char *flags, const char *data, size_t len);
void imap_quote(buf_t *out, const char *s);
const char *inode_str(const inode *x);   /* atom/string value, "" for NIL */
inode *inode_get(inode *list, int n, int i);
inode *fetch_item(inode *list, const char *name);   /* value after a FETCH item name */
void inode_free_all(inode *list, int n);

typedef struct { char *name, *display, *role; char delim; int noselect; int unseen, total; } folder_info;
int imap_list_folders(imap *m, folder_info **out, int *n);
void folder_info_free(folder_info *f, int n);

int sync_folder(account *a, imap *m, const char *folder, folder_cache *fc, int *added);
int sync_more(account *a, imap *m, const char *folder, folder_cache *fc, int count, int *added);
char *fetch_message(account *a, imap *m, const char *folder, uint32_t uid, size_t *len);
int imap_set_flag(imap *m, const char *folder, uint32_t uid, int add, const char *flag);
int imap_move(imap *m, const char *folder, uint32_t uid, const char *dest);
int imap_delete(imap *m, const char *folder, uint32_t uid, const char *trash);

/* ---- smtp.c ---- */
int smtp_send(account *a, const char *from, char **rcpts, int nrcpt, const char *data, size_t len,
              char *err, size_t errlen);
int smtp_test(account *a, char *err, size_t errlen);

#endif
