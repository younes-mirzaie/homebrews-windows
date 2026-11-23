/* MIME: parsing received mail into parts, addresses, dates, and building outgoing messages. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include "rb.h"

/* ---------------- headers ---------------- */

/* Offset of the blank line that ends the header block (start of body), and its length. */
static size_t header_end(const char *s, size_t n, size_t *sep)
{
    size_t i;
    if (n >= 2 && s[0] == '\r' && s[1] == '\n') { *sep = 2; return 0; }
    if (n >= 1 && s[0] == '\n') { *sep = 1; return 0; }
    for (i = 0; i + 1 < n; i++) {
        if (s[i] != '\n') continue;
        if (s[i + 1] == '\n') { *sep = 1; return i + 1; }
        if (s[i + 1] == '\r' && i + 2 < n && s[i + 2] == '\n') { *sep = 2; return i + 1; }
    }
    *sep = 0;
    return n;
}

char *hdr_get(const char *hdr, size_t len, const char *name)
{
    size_t nl = strlen(name), i = 0;
    while (i < len) {
        size_t le = i;
        while (le < len && hdr[le] != '\n') le++;
        if (le - i > nl && strnieq(hdr + i, name, nl) && (hdr[i + nl] == ':' ||
            ((hdr[i + nl] == ' ' || hdr[i + nl] == '\t') && memchr(hdr + i + nl, ':', le - i - nl)))) {
            buf_t v;
            const char *c = memchr(hdr + i + nl, ':', le - i - nl);
            size_t j;
            buf_init(&v);
            if (!c) { i = le + 1; continue; }
            buf_add(&v, c + 1, (size_t)(hdr + le - (c + 1)));
            /* continuation lines */
            j = le + 1;
            while (j < len && (hdr[j] == ' ' || hdr[j] == '\t')) {
                size_t e2 = j;
                while (e2 < len && hdr[e2] != '\n') e2++;
                buf_add(&v, hdr + j, e2 - j);
                j = e2 + 1;
            }
            {
                /* drop CRs left from CRLF line ends */
                char *r = buf_detach(&v), *w = r, *p = r;
                for (; *p; p++)
                    if (*p != '\r') *w++ = *p;
                *w = '\0';
                {
                    char *t = str_trim(r);
                    if (t != r) memmove(r, t, strlen(t) + 1);
                }
                return r;
            }
        }
        i = le + 1;
    }
    return NULL;
}

static int pct_val(int c)
{
    if (c >= '0' && c <= '9') return c - '0';
    c = ascii_lower(c);
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

/* Reads one parameter value (token or quoted string) at *pp into out. */
static void param_value(const char **pp, buf_t *out)
{
    const char *p = *pp;
    while (*p == ' ' || *p == '\t') p++;
    if (*p == '"') {
        p++;
        while (*p && *p != '"') {
            if (*p == '\\' && p[1]) p++;
            buf_addc(out, *p++);
        }
        if (*p == '"') p++;
    } else {
        while (*p && *p != ';' && *p != ' ' && *p != '\t') buf_addc(out, *p++);
    }
    *pp = p;
}

char *hdr_param(const char *value, const char *param)
{
    /* collects plain, extended (name*=) and continued (name*0, name*1*) forms */
    char *pieces[32] = {0};
    int ext[32] = {0}, maxpiece = -1;
    char *plain = NULL, *extended = NULL;
    size_t pl = strlen(param);
    const char *p = value ? strchr(value, ';') : NULL;
    char *result = NULL;
    while (p && *p) {
        const char *nm;
        size_t nlen;
        buf_t v;
        p++;
        while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
        nm = p;
        while (*p && *p != '=' && *p != ';') p++;
        nlen = (size_t)(p - nm);
        while (nlen && (nm[nlen - 1] == ' ' || nm[nlen - 1] == '\t')) nlen--;
        if (*p != '=') continue;
        p++;
        buf_init(&v);
        param_value(&p, &v);
        while (*p && *p != ';') p++;
        if (nlen >= pl && strnieq(nm, param, pl)) {
            const char *rest = nm + pl;
            size_t rl = nlen - pl;
            if (rl == 0) { free(plain); plain = buf_detach(&v); continue; }
            if (rl == 1 && rest[0] == '*') { free(extended); extended = buf_detach(&v); continue; }
            if (rest[0] == '*' && rl >= 2 && isdigit((unsigned char)rest[1])) {
                int idx = atoi(rest + 1);
                if (idx >= 0 && idx < 32) {
                    free(pieces[idx]);
                    pieces[idx] = buf_detach(&v);
                    ext[idx] = rest[rl - 1] == '*';
                    if (idx > maxpiece) maxpiece = idx;
                    continue;
                }
            }
        }
        buf_free(&v);
    }
    if (maxpiece >= 0 && !extended) {
        buf_t all;
        buf_init(&all);
        for (int i = 0; i <= maxpiece; i++) {
            if (!pieces[i]) break;
            if (ext[i]) {
                const char *s = pieces[i];
                if (i == 0) buf_adds(&all, "");
                for (; *s; s++) {
                    if (*s == '%' && pct_val((unsigned char)s[1]) >= 0 && pct_val((unsigned char)s[2]) >= 0) {
                        buf_addc(&all, pct_val((unsigned char)s[1]) * 16 + pct_val((unsigned char)s[2]));
                        s += 2;
                    } else {
                        buf_addc(&all, *s);
                    }
                }
            } else {
                buf_adds(&all, pieces[i]);
            }
        }
        if (ext[0]) {
            /* charset'lang'value: the charset prefix was copied raw, split it now */
            char *a = buf_detach(&all), *q1 = strchr(a, '\''), *q2 = q1 ? strchr(q1 + 1, '\'') : NULL;
            buf_t u;
            buf_init(&u);
            if (q1 && q2) {
                *q1 = '\0';
                charset_to_utf8(&u, a, q2 + 1, strlen(q2 + 1));
            } else {
                charset_to_utf8(&u, "utf-8", a, strlen(a));
            }
            free(a);
            result = buf_detach(&u);
        } else {
            char *a = buf_detach(&all);
            result = decode_header(a);
            free(a);
        }
    } else if (extended) {
        char *q1 = strchr(extended, '\''), *q2 = q1 ? strchr(q1 + 1, '\'') : NULL;
        buf_t raw, u;
        const char *s = q2 ? q2 + 1 : extended;
        buf_init(&raw);
        buf_init(&u);
        for (; *s; s++) {
            if (*s == '%' && pct_val((unsigned char)s[1]) >= 0 && pct_val((unsigned char)s[2]) >= 0) {
                buf_addc(&raw, pct_val((unsigned char)s[1]) * 16 + pct_val((unsigned char)s[2]));
                s += 2;
            } else {
                buf_addc(&raw, *s);
            }
        }
        if (q1 && q2) *q1 = '\0';
        charset_to_utf8(&u, q1 && q2 ? extended : "utf-8", buf_str(&raw), raw.len);
        buf_free(&raw);
        result = buf_detach(&u);
    } else if (plain) {
        result = decode_header(plain);   /* many mailers put encoded words in quoted names */
    }
    free(plain);
    free(extended);
    for (int i = 0; i < 32; i++) free(pieces[i]);
    return result;
}

/* ---------------- parsing ---------------- */

static char *lower_token(const char *v)
{
    size_t n = 0;
    char *r;
    if (!v) return NULL;
    while (*v == ' ' || *v == '\t') v++;
    while (v[n] && v[n] != ';' && v[n] != ' ' && v[n] != '\t' && v[n] != '(') n++;
    r = xstrndup(v, n);
    str_lower(r);
    return r;
}

static mime_part *parse_part(mime_msg *m, const char *s, size_t n, int depth, const char *default_type)
{
    mime_part *p = xcalloc(1, sizeof(*p));
    size_t sep, he = header_end(s, n, &sep);
    char *ct, *cd, *cte, *cid;
    p->hdr = s;
    p->hdr_len = he;
    p->body = s + he + sep;
    p->body_len = n > he + sep ? n - he - sep : 0;
    ct = hdr_get(s, he, "Content-Type");
    cd = hdr_get(s, he, "Content-Disposition");
    cte = hdr_get(s, he, "Content-Transfer-Encoding");
    cid = hdr_get(s, he, "Content-ID");
    p->ctype = ct ? lower_token(ct) : xstrdup(default_type);
    if (!strchr(p->ctype, '/')) { free(p->ctype); p->ctype = xstrdup("text/plain"); }
    p->charset = ct ? hdr_param(ct, "charset") : NULL;
    p->encoding = cte ? lower_token(cte) : xstrdup("7bit");
    p->disposition = cd ? lower_token(cd) : NULL;
    p->filename = cd ? hdr_param(cd, "filename") : NULL;
    if (!p->filename && ct) p->filename = hdr_param(ct, "name");
    if (cid) {
        char *c = cid;
        size_t l;
        while (*c == '<' || *c == ' ') c++;
        l = strlen(c);
        while (l && (c[l - 1] == '>' || c[l - 1] == ' ')) l--;
        p->cid = xstrndup(c, l);
    }
    if (depth < 20 && !strncmp(p->ctype, "multipart/", 10)) {
        char *boundary = hdr_param(ct, "boundary");
        if (boundary && *boundary) {
            size_t bl = strlen(boundary);
            const char *b = p->body, *e = p->body + p->body_len, *part = NULL;
            mime_part **tail = &p->child;
            const char *dflt = !strcmp(p->ctype, "multipart/digest") ? "message/rfc822" : "text/plain";
            const char *line = b;
            while (line < e) {
                const char *le = memchr(line, '\n', (size_t)(e - line));
                const char *next = le ? le + 1 : e;
                if (next - line >= (long)(bl + 2) && line[0] == '-' && line[1] == '-' &&
                    !memcmp(line + 2, boundary, bl)) {
                    const char *after = line + 2 + bl;
                    int last = after + 1 < e && after[0] == '-' && after[1] == '-';
                    if (part) {
                        /* the CRLF before the boundary belongs to the boundary */
                        const char *pe = line;
                        if (pe > part && pe[-1] == '\n') pe--;
                        if (pe > part && pe[-1] == '\r') pe--;
                        *tail = parse_part(m, part, (size_t)(pe - part), depth + 1, dflt);
                        tail = &(*tail)->next;
                    }
                    if (last) { part = NULL; break; }
                    part = next;
                }
                line = next;
            }
            if (part && part < e) {   /* missing close boundary */
                *tail = parse_part(m, part, (size_t)(e - part), depth + 1, dflt);
            }
        }
        free(boundary);
    }
    if (!p->child) p->index = m->nleaves++;
    else p->index = -1;
    free(ct);
    free(cd);
    free(cte);
    free(cid);
    return p;
}

mime_msg *mime_parse(const char *data, size_t len)
{
    mime_msg *m = xcalloc(1, sizeof(*m));
    m->data = xstrndup(data, len);
    m->len = len;
    m->root = parse_part(m, m->data, len, 0, "text/plain");
    return m;
}

static void part_free(mime_part *p)
{
    while (p) {
        mime_part *next = p->next;
        part_free(p->child);
        free(p->ctype);
        free(p->charset);
        free(p->filename);
        free(p->disposition);
        free(p->cid);
        free(p->encoding);
        free(p);
        p = next;
    }
}

void mime_free(mime_msg *m)
{
    if (!m) return;
    part_free(m->root);
    free(m->data);
    free(m);
}

void mime_decode(const mime_part *p, buf_t *out)
{
    if (!strcmp(p->encoding, "base64")) b64_decode(out, p->body, p->body_len);
    else if (!strcmp(p->encoding, "quoted-printable")) qp_decode(out, p->body, p->body_len, 0);
    else buf_add(out, p->body, p->body_len);
}

void mime_text(const mime_part *p, buf_t *out)
{
    buf_t raw;
    buf_init(&raw);
    mime_decode(p, &raw);
    charset_to_utf8(out, p->charset, buf_str(&raw), raw.len);
    buf_free(&raw);
}

static mime_part *find_leaf(mime_part *p, int index)
{
    for (; p; p = p->next) {
        mime_part *r;
        if (!p->child && p->index == index) return p;
        if (p->child && (r = find_leaf(p->child, index))) return r;
    }
    return NULL;
}

mime_part *mime_leaf(mime_msg *m, int index) { return find_leaf(m->root, index); }

static void collect(mime_part *p, mime_view *v)
{
    for (; p; p = p->next) {
        if (p->child) { collect(p->child, v); continue; }
        int is_att = (p->disposition && !strcmp(p->disposition, "attachment")) ||
                     (p->filename && *p->filename);
        if (!is_att && !strcmp(p->ctype, "text/plain") && !v->text) { v->text = p; continue; }
        if (!is_att && !strcmp(p->ctype, "text/html") && !v->html) { v->html = p; continue; }
        if (!p->body_len && !p->filename) continue;
        v->atts = xrealloc(v->atts, sizeof(*v->atts) * (size_t)(v->natts + 1));
        v->atts[v->natts++] = p;
    }
}

void mime_view_get(mime_msg *m, mime_view *v)
{
    memset(v, 0, sizeof(*v));
    collect(m->root, v);
}

void mime_view_free(mime_view *v) { free(v->atts); memset(v, 0, sizeof(*v)); }

/* ---------------- addresses ---------------- */

static void emit_addr(const char *s, size_t n, addr_fn fn, void *ctx)
{
    buf_t name, addr, item;
    const char *lt, *gt;
    char *it;
    buf_init(&name);
    buf_init(&addr);
    buf_init(&item);
    buf_add(&item, s, n);
    it = str_trim(item.p ? item.p : (char *)"");
    /* group syntax "Team: a@b, c@d;" */
    {
        char *colon = strchr(it, ':'), *at = strchr(it, '@'), *ang = strchr(it, '<'), *q = strchr(it, '"');
        if (colon && (!at || colon < at) && (!ang || colon < ang) && (!q || colon < q)) it = str_trim(colon + 1);
        while (*it && it[strlen(it) - 1] == ';') it[strlen(it) - 1] = '\0';
    }
    lt = strrchr(it, '<');
    gt = lt ? strchr(lt, '>') : NULL;
    if (lt && gt) {
        buf_add(&addr, lt + 1, (size_t)(gt - lt - 1));
        for (const char *p = it; p < lt; p++) {
            if (*p == '"') continue;
            if (*p == '\\' && p + 1 < lt) { p++; }
            buf_addc(&name, *p);
        }
    } else {
        /* bare address, possibly with a (comment) name */
        const char *open = strchr(it, '('), *close = open ? strchr(open, ')') : NULL;
        if (open && close) {
            buf_add(&name, open + 1, (size_t)(close - open - 1));
            buf_add(&addr, it, (size_t)(open - it));
        } else {
            buf_adds(&addr, it);
        }
    }
    {
        char *a = str_trim(addr.p ? addr.p : (char *)""), *nm = str_trim(name.p ? name.p : (char *)"");
        if (*a || *nm) fn(ctx, nm, a);
    }
    buf_free(&name);
    buf_free(&addr);
    buf_free(&item);
}

void addr_parse(const char *s, addr_fn fn, void *ctx)
{
    const char *start = s;
    int inq = 0, ang = 0, par = 0;
    if (!s) return;
    for (; *s; s++) {
        if (*s == '\\' && s[1]) { s++; continue; }
        if (*s == '"') inq = !inq;
        else if (!inq && *s == '<') ang = 1;
        else if (!inq && *s == '>') ang = 0;
        else if (!inq && *s == '(') par++;
        else if (!inq && *s == ')' && par) par--;
        else if (!inq && !ang && !par && (*s == ',' || *s == ';')) {
            emit_addr(start, (size_t)(s - start), fn, ctx);
            start = s + 1;
        }
    }
    emit_addr(start, (size_t)(s - start), fn, ctx);
}

/* ---------------- dates ---------------- */

static int64_t days_from_civil(int64_t y, unsigned m, unsigned d)
{
    y -= m <= 2;
    int64_t era = (y >= 0 ? y : y - 399) / 400;
    unsigned yoe = (unsigned)(y - era * 400);
    unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
    unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return era * 146097 + (int64_t)doe - 719468;
}

static const char *MONTHS[] = {"jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"};
static const char *DAYS[] = {"Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"};

static int month_index(const char *s)
{
    for (int i = 0; i < 12; i++)
        if (strnieq(s, MONTHS[i], 3)) return i + 1;
    return 0;
}

static int zone_offset(const char *z)
{
    while (*z == ' ') z++;
    if (*z == '+' || *z == '-') {
        int v = atoi(z + 1);
        int off = (v / 100) * 60 + v % 100;
        return *z == '-' ? -off : off;
    }
    if (starts_with_i(z, "EDT")) return -240;
    if (starts_with_i(z, "EST") || starts_with_i(z, "CDT")) return -300;
    if (starts_with_i(z, "CST") || starts_with_i(z, "MDT")) return -360;
    if (starts_with_i(z, "MST") || starts_with_i(z, "PDT")) return -420;
    if (starts_with_i(z, "PST")) return -480;
    return 0;   /* GMT, UT, Z, unknown */
}

time_t parse_rfc5322_date(const char *s)
{
    int d = 0, mon = 0, y = 0, hh = 0, mm = 0, ss = 0;
    const char *p = s;
    if (!s) return 0;
    while (*p && !isdigit((unsigned char)*p)) p++;          /* skip "Thu, " */
    d = atoi(p);
    while (isdigit((unsigned char)*p)) p++;
    while (*p == ' ' || *p == '-') p++;
    mon = month_index(p);
    while (*p && *p != ' ' && *p != '-') p++;
    while (*p == ' ' || *p == '-') p++;
    y = atoi(p);
    while (isdigit((unsigned char)*p)) p++;
    if (y < 100) y += y < 50 ? 2000 : 1900;
    while (*p == ' ') p++;
    hh = atoi(p);
    while (isdigit((unsigned char)*p)) p++;
    if (*p == ':') { mm = atoi(++p); while (isdigit((unsigned char)*p)) p++; }
    if (*p == ':') { ss = atoi(++p); while (isdigit((unsigned char)*p)) p++; }
    if (!mon || !d || !y) return 0;
    return (time_t)(days_from_civil(y, (unsigned)mon, (unsigned)d) * 86400 + hh * 3600 + mm * 60 + ss -
                    zone_offset(p) * 60);
}

time_t parse_imap_date(const char *s)
{
    return parse_rfc5322_date(s);   /* "17-Jul-1996 02:44:25 -0700" parses the same way */
}

char *rfc5322_date(time_t t)
{
    int64_t days = (int64_t)t / 86400, secs = (int64_t)t % 86400;
    int64_t z, era, y;
    unsigned doe, yoe, doy, mp, d, m;
    if (secs < 0) secs += 86400, days--;
    z = days + 719468;
    era = (z >= 0 ? z : z - 146096) / 146097;
    doe = (unsigned)(z - era * 146097);
    yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    y = (int64_t)yoe + era * 400;
    doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    mp = (5 * doy + 2) / 153;
    d = doy - (153 * mp + 2) / 5 + 1;
    m = mp < 10 ? mp + 3 : mp - 9;
    if (m <= 2) y++;
    {
        static const char *M[] = {"Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
        int wd = (int)((days % 7 + 11) % 7);   /* 1970-01-01 was a Thursday */
        return xprintf("%s, %02u %s %04lld %02d:%02d:%02d +0000", DAYS[wd], d, M[m - 1], (long long)y,
                       (int)(secs / 3600), (int)(secs / 60 % 60), (int)(secs % 60));
    }
}

/* ---------------- building ---------------- */

static int needs_quotes(const char *name)
{
    for (; *name; name++)
        if (strchr("()<>[]:;@\\,.\"", *name)) return 1;
    return 0;
}

static void add_mailbox(buf_t *out, const char *name, const char *addr)
{
    if (name && *name) {
        if (!is_ascii(name)) encode_header_text(out, name, 0);
        else if (needs_quotes(name)) {
            buf_addc(out, '"');
            for (; *name; name++) {
                if (*name == '"' || *name == '\\') buf_addc(out, '\\');
                buf_addc(out, *name);
            }
            buf_addc(out, '"');
        } else buf_adds(out, name);
        buf_adds(out, " <");
        buf_adds(out, addr);
        buf_addc(out, '>');
    } else {
        buf_adds(out, addr);
    }
}

typedef struct { buf_t *out; int count; int col; } list_ctx;

static void list_add(void *ctx, const char *name, const char *addr)
{
    list_ctx *l = ctx;
    size_t before;
    if (!*addr) return;
    if (l->count) {
        buf_adds(l->out, ",");
        if (l->col > 60) { buf_adds(l->out, "\r\n "); l->col = 1; }
        else { buf_addc(l->out, ' '); l->col += 2; }
    }
    before = l->out->len;
    add_mailbox(l->out, name, addr);
    l->col += (int)(l->out->len - before);
    l->count++;
}

static void add_list_header(buf_t *out, const char *hname, const char *list)
{
    buf_t tmp;
    list_ctx l;
    if (!list || !*list) return;
    buf_init(&tmp);
    l.out = &tmp;
    l.count = 0;
    l.col = (int)strlen(hname) + 2;
    addr_parse(list, list_add, &l);
    if (l.count) {
        buf_adds(out, hname);
        buf_adds(out, ": ");
        buf_add(out, buf_str(&tmp), tmp.len);
        buf_adds(out, "\r\n");
    }
    buf_free(&tmp);
}

char *mime_build(const mime_out *o, size_t *len, char **msgid)
{
    buf_t out, body;
    unsigned char rnd[12];
    char *date = rfc5322_date(time(NULL));
    int ascii_body, long_lines = 0;
    const char *dom = o->msgid_domain && *o->msgid_domain ? o->msgid_domain : "retrobird.invalid";
    buf_t id;
    buf_init(&out);
    buf_init(&body);
    buf_init(&id);
    random_bytes(rnd, sizeof(rnd));
    buf_adds(&id, "<");
    hex_encode(&id, rnd, sizeof(rnd));
    buf_printf(&id, ".rb@%s>", dom);

    buf_printf(&out, "Date: %s\r\n", date);
    buf_adds(&out, "From: ");
    add_mailbox(&out, o->from_name, o->from_addr);
    buf_adds(&out, "\r\n");
    add_list_header(&out, "To", o->to);
    add_list_header(&out, "Cc", o->cc);
    buf_adds(&out, "Subject: ");
    encode_header_text(&out, o->subject ? o->subject : "", 9);
    buf_adds(&out, "\r\n");
    buf_printf(&out, "Message-ID: %s\r\n", buf_str(&id));
    if (o->in_reply_to && *o->in_reply_to) buf_printf(&out, "In-Reply-To: %s\r\n", o->in_reply_to);
    if (o->references && *o->references) buf_printf(&out, "References: %s\r\n", o->references);
    buf_adds(&out, "MIME-Version: 1.0\r\nUser-Agent: Retrobird/" RB_VERSION " (Windows Phone 8.1)\r\n");

    {
        const char *b = o->body ? o->body : "";
        const char *line = b;
        ascii_body = is_ascii(b);
        while (*line) {
            const char *nl = strchr(line, '\n');
            size_t l = nl ? (size_t)(nl - line) : strlen(line);
            if (l > 900) long_lines = 1;
            line += l + (nl ? 1 : 0);
        }
        buf_adds(&out, "Content-Type: text/plain; charset=utf-8\r\n");
        if (ascii_body && !long_lines) {
            buf_adds(&out, "Content-Transfer-Encoding: 7bit\r\n\r\n");
            for (const char *p = b; *p; p++) {
                if (*p == '\r') continue;
                if (*p == '\n') buf_adds(&body, "\r\n");
                else buf_addc(&body, *p);
            }
        } else {
            buf_adds(&out, "Content-Transfer-Encoding: quoted-printable\r\n\r\n");
            qp_encode(&body, b, strlen(b));
        }
        buf_add(&out, buf_str(&body), body.len);
        if (!out.len || out.p[out.len - 1] != '\n') buf_adds(&out, "\r\n");
    }
    free(date);
    buf_free(&body);
    if (msgid) *msgid = buf_detach(&id);
    else buf_free(&id);
    *len = out.len;
    return buf_detach(&out);
}

/* ---------------- preview line ---------------- */

/* One line for the message list: the first words of the text (or of the HTML as text), with
   quoted lines left out and whitespace collapsed. hdr holds at least Content-Type; text may be
   cut anywhere, since the list only fetches its first few KB. */
char *make_preview(const char *hdr, size_t hdr_len, const char *text, size_t text_len)
{
    buf_t whole, t, clean, out;
    mime_msg *m;
    mime_view v;
    const char *p, *end;
    int space = 1, chars = 0;
    buf_init(&whole);
    buf_add(&whole, hdr ? hdr : "", hdr ? hdr_len : 0);
    while (whole.len && (whole.p[whole.len - 1] == '\n' || whole.p[whole.len - 1] == '\r')) whole.p[--whole.len] = '\0';
    buf_adds(&whole, "\r\n\r\n");
    buf_add(&whole, text ? text : "", text ? text_len : 0);
    m = mime_parse(buf_str(&whole), whole.len);
    buf_free(&whole);
    mime_view_get(m, &v);
    buf_init(&t);
    if (v.text) mime_text(v.text, &t);
    else if (v.html) {
        buf_t raw;
        buf_init(&raw);
        mime_text(v.html, &raw);
        html_to_text(&t, buf_str(&raw), raw.len);
        buf_free(&raw);
    }
    mime_view_free(&v);
    mime_free(m);
    buf_init(&clean);
    utf8_sanitize(&clean, buf_str(&t), t.len);
    buf_free(&t);
    buf_init(&out);
    p = buf_str(&clean);
    end = p + clean.len;
    while (p < end && chars < 160) {
        const char *eol = memchr(p, '\n', (size_t)(end - p)), *q;
        if (!eol) eol = end;
        q = p;
        while (q < eol && (*q == ' ' || *q == '\t')) q++;
        if (q < eol && *q == '>') { p = eol + 1; continue; }  /* quoted reply */
        for (; q < eol; q++) {
            unsigned char c = (unsigned char)*q;
            if ((c & 0xC0) != 0x80 && chars >= 160) break;
            if (c == ' ' || c == '\t' || c == '\r' || c == '\v' || c == '\f') {
                if (!space) { buf_addc(&out, ' '); space = 1; }
                continue;
            }
            if (c < 0x20 || c == 0x7F) continue;
            /* zero width and non-breaking spaces that newsletters pad with */
            if (c == 0xC2 && q + 1 < eol && (unsigned char)q[1] == 0xA0) {
                if (!space) { buf_addc(&out, ' '); space = 1; }
                q++;
                continue;
            }
            if (c == 0xE2 && q + 2 < eol && (unsigned char)q[1] == 0x80 &&
                ((unsigned char)q[2] == 0x8B || (unsigned char)q[2] == 0x8C || (unsigned char)q[2] == 0x8D)) {
                q += 2;
                continue;
            }
            if (c == 0xEF && q + 2 < eol && (unsigned char)q[1] == 0xBB && (unsigned char)q[2] == 0xBF) { q += 2; continue; }
            buf_addc(&out, (char)c);
            space = 0;
            if ((c & 0xC0) != 0x80) chars++;
        }
        if (!space) { buf_addc(&out, ' '); space = 1; }
        p = eol + 1;
    }
    buf_free(&clean);
    /* a cut UTF-8 sequence at the end */
    while (out.len && ((unsigned char)out.p[out.len - 1] & 0xC0) == 0x80) {
        size_t i = out.len;
        while (i && ((unsigned char)out.p[i - 1] & 0xC0) == 0x80) i--;
        if (!i) { out.len = 0; break; }
        {
            unsigned char lead = (unsigned char)out.p[i - 1];
            size_t need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : 2;
            if (out.len - (i - 1) >= need) break;
            out.len = i - 1;
        }
    }
    while (out.len && out.p[out.len - 1] == ' ') out.len--;
    if (out.p) out.p[out.len] = '\0';
    return buf_detach(&out);
}
