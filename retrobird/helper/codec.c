/* Encodings: base64, quoted-printable, charsets, RFC 2047 headers, IMAP modified UTF-7, HTML. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include "rb.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <iconv.h>
#endif

/* ---------------- base64 / qp / hex ---------------- */

static const char B64[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

void b64_encode(buf_t *out, const void *data, size_t n, int wrap76)
{
    const unsigned char *s = data;
    size_t i;
    int col = 0;
    for (i = 0; i < n; i += 3) {
        unsigned v = (unsigned)s[i] << 16;
        if (i + 1 < n) v |= (unsigned)s[i + 1] << 8;
        if (i + 2 < n) v |= s[i + 2];
        buf_addc(out, B64[(v >> 18) & 63]);
        buf_addc(out, B64[(v >> 12) & 63]);
        buf_addc(out, i + 1 < n ? B64[(v >> 6) & 63] : '=');
        buf_addc(out, i + 2 < n ? B64[v & 63] : '=');
        col += 4;
        if (wrap76 && col >= 76 && i + 3 < n) { buf_adds(out, "\r\n"); col = 0; }
    }
}

static int b64val(int c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+' || c == '-') return 62;
    if (c == '/' || c == '_') return 63;
    return -1;
}

void b64_decode(buf_t *out, const char *in, size_t n)
{
    unsigned v = 0;
    int bits = 0;
    size_t i;
    for (i = 0; i < n; i++) {
        int d;
        if (in[i] == '=') break;
        d = b64val((unsigned char)in[i]);
        if (d < 0) continue;
        v = (v << 6) | (unsigned)d;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            buf_addc(out, (v >> bits) & 0xff);
        }
    }
}

static int hexval(int c)
{
    if (c >= '0' && c <= '9') return c - '0';
    c = ascii_lower(c);
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

void qp_decode(buf_t *out, const char *in, size_t n, int header)
{
    size_t i;
    for (i = 0; i < n; i++) {
        char c = in[i];
        if (c == '=') {
            int h, l;
            if (i + 1 < n && (in[i + 1] == '\r' || in[i + 1] == '\n')) {   /* soft line break */
                i++;
                if (in[i] == '\r' && i + 1 < n && in[i + 1] == '\n') i++;
                continue;
            }
            /* soft break with trailing spaces before the newline */
            if (i + 1 < n && (in[i + 1] == ' ' || in[i + 1] == '\t')) {
                size_t j = i + 1;
                while (j < n && (in[j] == ' ' || in[j] == '\t')) j++;
                if (j >= n || in[j] == '\r' || in[j] == '\n') {
                    i = j;
                    if (i < n && in[i] == '\r' && i + 1 < n && in[i + 1] == '\n') i++;
                    continue;
                }
            }
            h = i + 1 < n ? hexval((unsigned char)in[i + 1]) : -1;
            l = i + 2 < n ? hexval((unsigned char)in[i + 2]) : -1;
            if (h >= 0 && l >= 0) {
                buf_addc(out, h * 16 + l);
                i += 2;
                continue;
            }
            buf_addc(out, '=');
        } else if (header && c == '_') {
            buf_addc(out, ' ');
        } else {
            buf_addc(out, c);
        }
    }
}

void qp_encode(buf_t *out, const char *in, size_t n)
{
    size_t i;
    int col = 0;
    for (i = 0; i < n; i++) {
        unsigned char c = (unsigned char)in[i];
        char tmp[4];
        int len;
        if (c == '\r' && i + 1 < n && in[i + 1] == '\n') continue;
        if (c == '\n') {
            buf_adds(out, "\r\n");
            col = 0;
            continue;
        }
        if ((c >= 33 && c <= 126 && c != '=') ||
            ((c == ' ' || c == '\t') && i + 1 < n && in[i + 1] != '\n' && in[i + 1] != '\r')) {
            tmp[0] = (char)c;
            len = 1;
        } else {
            snprintf(tmp, sizeof(tmp), "=%02X", c);
            len = 3;
        }
        /* a dot at the start of a line is fine for SMTP (we dot-stuff), but keep "From " safe */
        if (col + len > 75) {
            buf_adds(out, "=\r\n");
            col = 0;
        }
        if (col == 0 && c == '.') { buf_adds(out, "=2E"); col += 3; continue; }
        buf_add(out, tmp, (size_t)len);
        col += len;
    }
}

void hex_encode(buf_t *out, const void *data, size_t n)
{
    const unsigned char *s = data;
    size_t i;
    for (i = 0; i < n; i++) buf_printf(out, "%02x", s[i]);
}

int hex_decode(buf_t *out, const char *hex)
{
    while (hex[0] && hex[1]) {
        int h = hexval((unsigned char)hex[0]), l = hexval((unsigned char)hex[1]);
        if (h < 0 || l < 0) return -1;
        buf_addc(out, h * 16 + l);
        hex += 2;
    }
    return *hex ? -1 : 0;
}

/* ---------------- UTF-8 and charsets ---------------- */

void utf8_put(buf_t *out, uint32_t cp)
{
    if (cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;
    if (cp < 0x80) buf_addc(out, (int)cp);
    else if (cp < 0x800) { buf_addc(out, 0xC0 | (cp >> 6)); buf_addc(out, 0x80 | (cp & 63)); }
    else if (cp < 0x10000) {
        buf_addc(out, 0xE0 | (cp >> 12)); buf_addc(out, 0x80 | ((cp >> 6) & 63)); buf_addc(out, 0x80 | (cp & 63));
    } else {
        buf_addc(out, 0xF0 | (cp >> 18)); buf_addc(out, 0x80 | ((cp >> 12) & 63));
        buf_addc(out, 0x80 | ((cp >> 6) & 63)); buf_addc(out, 0x80 | (cp & 63));
    }
}

/* length of a valid UTF-8 sequence at s, or 0 */
static int utf8_seq(const unsigned char *s, size_t n)
{
    uint32_t cp;
    int len, i;
    if (s[0] < 0x80) return 1;
    if (s[0] >= 0xC2 && s[0] <= 0xDF) len = 2, cp = s[0] & 0x1F;
    else if (s[0] >= 0xE0 && s[0] <= 0xEF) len = 3, cp = s[0] & 0x0F;
    else if (s[0] >= 0xF0 && s[0] <= 0xF4) len = 4, cp = s[0] & 0x07;
    else return 0;
    if ((size_t)len > n) return 0;
    for (i = 1; i < len; i++) {
        if ((s[i] & 0xC0) != 0x80) return 0;
        cp = (cp << 6) | (s[i] & 63);
    }
    if ((len == 3 && cp < 0x800) || (len == 4 && (cp < 0x10000 || cp > 0x10FFFF)) ||
        (cp >= 0xD800 && cp <= 0xDFFF))
        return 0;
    return len;
}

int utf8_valid(const char *in, size_t n)
{
    size_t i = 0;
    while (i < n) {
        int l = utf8_seq((const unsigned char *)in + i, n - i);
        if (!l) return 0;
        i += (size_t)l;
    }
    return 1;
}

void utf8_sanitize(buf_t *out, const char *in, size_t n)
{
    size_t i = 0;
    while (i < n) {
        int l = utf8_seq((const unsigned char *)in + i, n - i);
        if (!l) {
            utf8_put(out, 0xFFFD);
            i++;
        } else {
            if (!(l == 1 && in[i] == '\0')) buf_add(out, in + i, (size_t)l);
            i += (size_t)l;
        }
    }
}

/* true if the text holds at least one valid multi-byte UTF-8 sequence */
static int has_multibyte(const char *in, size_t n)
{
    for (size_t i = 0; i < n; i++) {
        if ((unsigned char)in[i] < 0x80) continue;
        if (utf8_seq((const unsigned char *)in + i, n - i) > 1) return 1;
    }
    return 0;
}

int is_ascii(const char *s)
{
    for (; *s; s++)
        if ((unsigned char)*s >= 0x80) return 0;
    return 1;
}

static const uint16_t CP1252[32] = {
    0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039,
    0x0152, 0x008D, 0x017D, 0x008F, 0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
    0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178};

static void latin_to_utf8(buf_t *out, const char *in, size_t n, int variant)
{
    size_t i;
    for (i = 0; i < n; i++) {
        unsigned c = (unsigned char)in[i];
        uint32_t cp = c;
        if (variant == 1252 && c >= 0x80 && c < 0xA0) cp = CP1252[c - 0x80];
        if (variant == 15) {
            switch (c) {
            case 0xA4: cp = 0x20AC; break; case 0xA6: cp = 0x0160; break; case 0xA8: cp = 0x0161; break;
            case 0xB4: cp = 0x017D; break; case 0xB8: cp = 0x017E; break; case 0xBC: cp = 0x0152; break;
            case 0xBD: cp = 0x0153; break; case 0xBE: cp = 0x0178; break;
            }
        }
        if (cp == 0) continue;
        utf8_put(out, cp);
    }
}

static void norm_charset(const char *cs, char *out, size_t len)
{
    size_t n = 0;
    for (; cs && *cs && n + 1 < len; cs++) {
        if (*cs == '"' || *cs == ' ' || *cs == '\'') continue;
        if (*cs == '*') break;     /* RFC 2231 language suffix */
        out[n++] = (char)ascii_lower((unsigned char)*cs);
    }
    out[n] = '\0';
}

#ifdef _WIN32
static const struct { const char *name; unsigned cp; } CODEPAGES[] = {
    {"windows-1250", 1250}, {"windows-1251", 1251}, {"windows-1253", 1253}, {"windows-1254", 1254},
    {"windows-1255", 1255}, {"windows-1256", 1256}, {"windows-1257", 1257}, {"windows-1258", 1258},
    {"cp1250", 1250}, {"cp1251", 1251}, {"cp1256", 1256},
    {"iso-8859-2", 28592}, {"iso-8859-3", 28593}, {"iso-8859-4", 28594}, {"iso-8859-5", 28595},
    {"iso-8859-6", 28596}, {"iso-8859-7", 28597}, {"iso-8859-8", 28598}, {"iso-8859-8-i", 38598},
    {"iso-8859-9", 28599}, {"iso-8859-13", 28603}, {"koi8-r", 20866}, {"koi8-u", 21866},
    {"shift_jis", 932}, {"shift-jis", 932}, {"sjis", 932}, {"euc-jp", 51932}, {"iso-2022-jp", 50220},
    {"gb2312", 936}, {"gbk", 936}, {"gb18030", 54936}, {"big5", 950}, {"euc-kr", 949},
    {"ks_c_5601-1987", 949}, {"iso-2022-kr", 50225}, {"tis-620", 874}, {"windows-874", 874},
    {"utf-16", 1200}, {"utf-16le", 1200}, {"utf-16be", 1201}, {"ibm866", 866}, {"cp866", 866},
    {NULL, 0}};

static int platform_convert(buf_t *out, const char *cs, const char *in, size_t n)
{
    unsigned cp = 0;
    int i, wn, un;
    WCHAR *w;
    for (i = 0; CODEPAGES[i].name; i++)
        if (!strcmp(CODEPAGES[i].name, cs)) { cp = CODEPAGES[i].cp; break; }
    if (!cp || n == 0 || n > 0x7fffffff) return -1;
    if (cp == 1200 || cp == 1201) {
        size_t k;
        for (k = 0; k + 1 < n; k += 2) {
            uint32_t u = cp == 1200 ? ((unsigned char)in[k] | (unsigned char)in[k + 1] << 8)
                                    : ((unsigned char)in[k] << 8 | (unsigned char)in[k + 1]);
            if (u >= 0xD800 && u <= 0xDBFF && k + 3 < n) {
                uint32_t lo = cp == 1200 ? ((unsigned char)in[k + 2] | (unsigned char)in[k + 3] << 8)
                                         : ((unsigned char)in[k + 2] << 8 | (unsigned char)in[k + 3]);
                if (lo >= 0xDC00 && lo <= 0xDFFF) { u = 0x10000 + ((u - 0xD800) << 10) + (lo - 0xDC00); k += 2; }
            }
            if (u == 0xFEFF && k == 0) continue;
            utf8_put(out, u);
        }
        return 0;
    }
    wn = MultiByteToWideChar(cp, 0, in, (int)n, NULL, 0);
    if (wn <= 0) return -1;
    w = xmalloc(sizeof(WCHAR) * (size_t)wn);
    MultiByteToWideChar(cp, 0, in, (int)n, w, wn);
    un = WideCharToMultiByte(CP_UTF8, 0, w, wn, NULL, 0, NULL, NULL);
    if (un > 0) {
        char *u = xmalloc((size_t)un);
        WideCharToMultiByte(CP_UTF8, 0, w, wn, u, un, NULL, NULL);
        utf8_sanitize(out, u, (size_t)un);
        free(u);
    }
    free(w);
    return un > 0 ? 0 : -1;
}
#else
static int platform_convert(buf_t *out, const char *cs, const char *in, size_t n)
{
    iconv_t cd = iconv_open("UTF-8", cs);
    char tmp[4096];
    char *ip = (char *)in, *op;
    size_t il = n, ol;
    buf_t raw;
    if (cd == (iconv_t)-1) return -1;
    buf_init(&raw);
    while (il > 0) {
        op = tmp;
        ol = sizeof(tmp);
        if (iconv(cd, &ip, &il, &op, &ol) == (size_t)-1 && op == tmp) {
            /* skip one bad byte and carry on */
            buf_adds(&raw, "\xEF\xBF\xBD");
            ip++;
            il--;
            continue;
        }
        buf_add(&raw, tmp, (size_t)(op - tmp));
    }
    iconv_close(cd);
    utf8_sanitize(out, raw.p ? raw.p : "", raw.len);
    buf_free(&raw);
    return 0;
}
#endif

int charset_to_utf8(buf_t *out, const char *charset, const char *in, size_t n)
{
    char cs[64];
    norm_charset(charset, cs, sizeof(cs));
    if (!cs[0] || !strcmp(cs, "utf-8") || !strcmp(cs, "utf8") || !strcmp(cs, "us-ascii") ||
        !strcmp(cs, "ascii") || !strcmp(cs, "unknown-8bit") || !strcmp(cs, "x-unknown")) {
        if (utf8_valid(in, n) || has_multibyte(in, n)) { utf8_sanitize(out, in, n); return 0; }
        latin_to_utf8(out, in, n, 1252);   /* mislabelled latin-1 */
        return cs[0] && strcmp(cs, "unknown-8bit") && strcmp(cs, "x-unknown") ? 0 : -1;
    }
    if (!strcmp(cs, "iso-8859-1") || !strcmp(cs, "latin1") || !strcmp(cs, "iso8859-1") ||
        !strcmp(cs, "windows-1252") || !strcmp(cs, "cp1252") || !strcmp(cs, "iso_8859-1")) {
        /* real-world "latin1" is nearly always windows-1252 */
        latin_to_utf8(out, in, n, 1252);
        return 0;
    }
    if (!strcmp(cs, "iso-8859-15") || !strcmp(cs, "latin9")) {
        latin_to_utf8(out, in, n, 15);
        return 0;
    }
    if (platform_convert(out, cs, in, n) == 0) return 0;
    if (utf8_valid(in, n)) utf8_sanitize(out, in, n);
    else latin_to_utf8(out, in, n, 1252);
    return -1;
}

/* ---------------- headers ---------------- */

/* Parses "=?cs?E?text?=" at s; fills the parts and returns the length, or 0. */
static size_t encoded_word(const char *s, char *cs, size_t cslen, char *enc, const char **text, size_t *textlen)
{
    const char *p = s + 2, *q, *e;
    if (s[0] != '=' || s[1] != '?') return 0;
    q = strchr(p, '?');
    if (!q || (size_t)(q - p) >= cslen || q == p) return 0;
    if (!q[1] || q[2] != '?') return 0;
    *enc = (char)ascii_lower((unsigned char)q[1]);
    if (*enc != 'b' && *enc != 'q') return 0;
    e = strstr(q + 3, "?=");
    if (!e) return 0;
    for (const char *t = q + 3; t < e; t++)
        if (*t == ' ' || *t == '\t' || *t == '\r' || *t == '\n') return 0;
    memcpy(cs, p, (size_t)(q - p));
    cs[q - p] = '\0';
    *text = q + 3;
    *textlen = (size_t)(e - (q + 3));
    return (size_t)(e + 2 - s);
}

char *decode_header(const char *raw)
{
    buf_t out, pend;
    char pend_cs[64] = "";
    const char *s = raw;
    buf_init(&out);
    buf_init(&pend);
    if (!raw) return xstrdup("");
    while (*s) {
        char cs[64], enc;
        const char *text;
        size_t tl, wl = encoded_word(s, cs, sizeof(cs), &enc, &text, &tl);
        if (wl) {
            buf_t bytes;
            buf_init(&bytes);
            if (enc == 'b') b64_decode(&bytes, text, tl);
            else qp_decode(&bytes, text, tl, 1);
            if (pend.len && !strieq(pend_cs, cs)) {
                charset_to_utf8(&out, pend_cs, pend.p, pend.len);
                buf_clear(&pend);
            }
            norm_charset(cs, pend_cs, sizeof(pend_cs));
            buf_add(&pend, bytes.p ? bytes.p : "", bytes.len);
            buf_free(&bytes);
            s += wl;
            /* whitespace between two encoded words is dropped */
            {
                const char *t = s;
                char c2[64], e2;
                const char *x2;
                size_t l2;
                while (*t == ' ' || *t == '\t' || *t == '\r' || *t == '\n') t++;
                if (t != s && encoded_word(t, c2, sizeof(c2), &e2, &x2, &l2)) s = t;
            }
            continue;
        }
        if (pend.len) {
            charset_to_utf8(&out, pend_cs, pend.p, pend.len);
            buf_clear(&pend);
        }
        if (*s == '\r' || *s == '\n') { s++; continue; }   /* unfold */
        if (*s == '\t') { buf_addc(&out, ' '); s++; continue; }
        {
            /* run of plain text up to the next possible encoded word */
            const char *e = s + 1;
            while (*e && !(e[0] == '=' && e[1] == '?') && *e != '\r' && *e != '\n') e++;
            if (utf8_valid(s, (size_t)(e - s))) buf_add(&out, s, (size_t)(e - s));
            else latin_to_utf8(&out, s, (size_t)(e - s), 1252);
            s = e;
        }
    }
    if (pend.len) charset_to_utf8(&out, pend_cs, pend.p, pend.len);
    buf_free(&pend);
    {
        char *r = buf_detach(&out);
        /* collapse runs of spaces left over from folding */
        char *w = r, *p = r;
        int sp = 0;
        for (; *p; p++) {
            if (*p == ' ') { if (sp) continue; sp = 1; }
            else sp = 0;
            *w++ = *p;
        }
        *w = '\0';
        return r;
    }
}

void encode_header_text(buf_t *out, const char *utf8, size_t used)
{
    size_t n = strlen(utf8), i = 0;
    (void)used;
    if (is_ascii(utf8)) {
        buf_adds(out, utf8);
        return;
    }
    while (i < n) {
        size_t chunk = 0;
        /* up to 45 bytes per word (60 base64 chars), never splitting a UTF-8 sequence */
        while (i + chunk < n) {
            int l = utf8_seq((const unsigned char *)utf8 + i + chunk, n - i - chunk);
            if (!l) l = 1;
            if (chunk + (size_t)l > 45) break;
            chunk += (size_t)l;
        }
        if (i) buf_adds(out, "\r\n ");
        buf_adds(out, "=?UTF-8?B?");
        b64_encode(out, utf8 + i, chunk, 0);
        buf_adds(out, "?=");
        i += chunk;
    }
}

char *imap_mutf7_decode(const char *s)
{
    buf_t out;
    buf_init(&out);
    while (*s) {
        if (*s != '&') { buf_addc(&out, *s++); continue; }
        s++;
        if (*s == '-') { buf_addc(&out, '&'); s++; continue; }
        {
            uint32_t bits = 0, hi = 0;
            int nbits = 0;
            while (*s && *s != '-') {
                int d = *s == ',' ? 63 : b64val((unsigned char)*s);
                s++;
                if (d < 0) continue;
                bits = (bits << 6) | (uint32_t)d;
                nbits += 6;
                if (nbits >= 16) {
                    uint32_t u = (bits >> (nbits - 16)) & 0xFFFF;
                    nbits -= 16;
                    if (u >= 0xD800 && u <= 0xDBFF) { hi = u; continue; }
                    if (u >= 0xDC00 && u <= 0xDFFF && hi) { u = 0x10000 + ((hi - 0xD800) << 10) + (u - 0xDC00); hi = 0; }
                    utf8_put(&out, u);
                }
            }
            if (*s == '-') s++;
        }
    }
    return buf_detach(&out);
}

/* ---------------- HTML ---------------- */

static const struct { const char *name; uint32_t cp; } ENTITIES[] = {
    {"amp", '&'}, {"lt", '<'}, {"gt", '>'}, {"quot", '"'}, {"apos", '\''}, {"nbsp", 0xA0},
    {"copy", 0xA9}, {"reg", 0xAE}, {"trade", 0x2122}, {"hellip", 0x2026}, {"mdash", 0x2014},
    {"ndash", 0x2013}, {"lsquo", 0x2018}, {"rsquo", 0x2019}, {"ldquo", 0x201C}, {"rdquo", 0x201D},
    {"bull", 0x2022}, {"middot", 0xB7}, {"euro", 0x20AC}, {"pound", 0xA3}, {"yen", 0xA5},
    {"cent", 0xA2}, {"sect", 0xA7}, {"deg", 0xB0}, {"times", 0xD7}, {"divide", 0xF7},
    {"laquo", 0xAB}, {"raquo", 0xBB}, {"zwnj", 0x200C}, {"zwj", 0x200D}, {"shy", 0xAD},
    {"auml", 0xE4}, {"ouml", 0xF6}, {"uuml", 0xFC}, {"Auml", 0xC4}, {"Ouml", 0xD6}, {"Uuml", 0xDC},
    {"szlig", 0xDF}, {"eacute", 0xE9}, {"egrave", 0xE8}, {"agrave", 0xE0}, {"aacute", 0xE1},
    {"ccedil", 0xE7}, {"ntilde", 0xF1}, {"oacute", 0xF3}, {"iacute", 0xED}, {"uacute", 0xFA},
    {"rarr", 0x2192}, {"larr", 0x2190}, {"thinsp", 0x2009}, {"ensp", 0x2002}, {"emsp", 0x2003},
    {NULL, 0}};

/* Decodes the entity at s ('&...'), returns chars consumed or 0. */
static size_t entity(const char *s, size_t n, uint32_t *cp)
{
    size_t i = 1;
    if (n < 3 || s[0] != '&') return 0;
    if (s[1] == '#') {
        uint32_t v = 0;
        int hex = (s[2] == 'x' || s[2] == 'X'), digits = 0;
        i = hex ? 3 : 2;
        for (; i < n && i < 12; i++) {
            int d = hex ? hexval((unsigned char)s[i]) : (s[i] >= '0' && s[i] <= '9' ? s[i] - '0' : -1);
            if (d < 0) break;
            v = v * (hex ? 16 : 10) + (uint32_t)d;
            digits++;
        }
        if (!digits) return 0;
        if (i < n && s[i] == ';') i++;
        *cp = v ? v : 0xFFFD;
        return i;
    }
    for (int k = 0; ENTITIES[k].name; k++) {
        size_t l = strlen(ENTITIES[k].name);
        if (l + 1 < n && !strncmp(s + 1, ENTITIES[k].name, l) && s[1 + l] == ';') {
            *cp = ENTITIES[k].cp;
            return l + 2;
        }
    }
    return 0;
}

/* Reads a tag name at p (after '<' and optional '/'), lower-cased into name. */
static size_t tag_name(const char *p, const char *end, char *name, size_t len)
{
    size_t n = 0;
    const char *s = p;
    while (s < end && (isalnum((unsigned char)*s) || *s == '-' || *s == ':' || *s == '!')) {
        if (n + 1 < len) name[n++] = (char)ascii_lower((unsigned char)*s);
        s++;
    }
    name[n] = '\0';
    return (size_t)(s - p);
}

/* End of a tag starting at '<' (position after '>'), honouring quoted attributes. */
static const char *tag_end(const char *p, const char *end)
{
    char q = 0;
    for (p++; p < end; p++) {
        if (q) { if (*p == q) q = 0; }
        else if (*p == '"' || *p == '\'') q = *p;
        else if (*p == '>') return p + 1;
    }
    return end;
}

/* Finds the closing tag </name> from p, case-insensitively; returns its '<' or end. */
static const char *find_close(const char *p, const char *end, const char *name)
{
    size_t l = strlen(name);
    for (; p + l + 2 <= end; p++)
        if (p[0] == '<' && p[1] == '/' && strnieq(p + 2, name, l) &&
            (p + 2 + l >= end || !isalnum((unsigned char)p[2 + l])))
            return p;
    return end;
}

static int is_block(const char *t)
{
    static const char *B[] = {"p", "div", "br", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6",
                              "blockquote", "table", "ul", "ol", "hr", "pre", "section", "article",
                              "header", "footer", "dt", "dd", "center", NULL};
    for (int i = 0; B[i]; i++)
        if (!strcmp(t, B[i])) return 1;
    return 0;
}

void html_to_text(buf_t *out, const char *html, size_t n)
{
    const char *p = html, *end = html + n;
    int nl = 2, space = 1, pre = 0;
    size_t start = out->len;
    while (p < end) {
        if (*p == '<') {
            char name[32];
            int closing = p + 1 < end && p[1] == '/';
            const char *te;
            if (p + 3 < end && !strncmp(p, "<!--", 4)) {
                const char *c = strstr(p + 4, "-->");
                p = c && c < end ? c + 3 : end;
                continue;
            }
            tag_name(p + 1 + closing, end, name, sizeof(name));
            te = tag_end(p, end);
            if (!closing && (!strcmp(name, "script") || !strcmp(name, "style") || !strcmp(name, "head") ||
                             !strcmp(name, "title"))) {
                const char *c = find_close(te, end, name);
                p = c < end ? tag_end(c, end) : end;
                continue;
            }
            if (!strcmp(name, "pre")) pre = !closing;
            if (is_block(name)) {
                int want = (!strcmp(name, "p") || !strcmp(name, "blockquote") || !strcmp(name, "table") ||
                            name[0] == 'h') ? 2 : 1;
                if (!strcmp(name, "br")) {
                    buf_addc(out, '\n');
                    nl++;
                } else {
                    while (nl < want && out->len > start) { buf_addc(out, '\n'); nl++; }
                }
                space = 1;
                if (!closing && !strcmp(name, "li")) { buf_adds(out, "\xE2\x80\xA2 "); nl = 0; }
                if (!closing && !strcmp(name, "hr")) { buf_adds(out, "----------\n"); nl = 1; }
            } else if (!closing && (!strcmp(name, "td") || !strcmp(name, "th"))) {
                if (!space) { buf_addc(out, ' '); space = 1; }
            }
            p = te;
            continue;
        }
        if (*p == '&') {
            uint32_t cp;
            size_t l = entity(p, (size_t)(end - p), &cp);
            if (l) {
                if (cp == 0xA0 || cp == ' ') { if (!space) buf_addc(out, ' '); space = 1; }
                else { utf8_put(out, cp); space = 0; nl = 0; }
                p += l;
                continue;
            }
        }
        if (!pre && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) {
            if (!space && nl == 0) buf_addc(out, ' ');
            space = 1;
            p++;
            continue;
        }
        if (pre && *p == '\n') { buf_addc(out, '\n'); nl = 1; p++; continue; }
        if (pre && *p == '\r') { p++; continue; }
        buf_addc(out, *p++);
        space = 0;
        nl = 0;
    }
    /* trim trailing whitespace */
    while (out->len > start && (out->p[out->len - 1] == '\n' || out->p[out->len - 1] == ' '))
        out->p[--out->len] = '\0';
}

static int url_is_remote(const char *v, size_t n)
{
    while (n && (*v == ' ' || *v == '\t' || *v == '\n' || *v == '\r' || *v == '"' || *v == '\'')) v++, n--;
    if (n >= 5 && strnieq(v, "data:", 5)) return 0;
    if (n >= 4 && strnieq(v, "cid:", 4)) return 0;
    if (n >= 1 && v[0] == '#') return 0;
    return 1;
}

/* Neutralises url(...) and @import in CSS unless remote loads are allowed. */
static void css_filter(buf_t *out, const char *s, size_t n, int allow_remote, int *blocked)
{
    size_t i = 0;
    while (i < n) {
        if (!allow_remote && i + 4 <= n && strnieq(s + i, "url(", 4) && url_is_remote(s + i + 4, n - i - 4)) {
            buf_adds(out, "x-blocked(");
            *blocked = 1;
            i += 4;
            continue;
        }
        if (!allow_remote && i + 7 <= n && strnieq(s + i, "@import", 7)) {
            buf_adds(out, "@x-blocked");
            *blocked = 1;
            i += 7;
            continue;
        }
        if (i + 11 <= n && strnieq(s + i, "expression(", 11)) {
            buf_adds(out, "x-blocked(");
            i += 11;
            continue;
        }
        buf_addc(out, s[i++]);
    }
}

static int dangerous_url(const char *v)
{
    while (*v == ' ' || *v == '\t' || *v == '\n' || *v == '\r') v++;
    return starts_with_i(v, "javascript:") || starts_with_i(v, "vbscript:") ||
           starts_with_i(v, "data:text/html") || starts_with_i(v, "file:");
}

static void attr_value_escape(buf_t *out, const char *v)
{
    for (; *v; v++) {
        if (*v == '"') buf_adds(out, "&quot;");
        else buf_addc(out, *v);
    }
}

/* Re-emits one start tag with only safe attributes. */
static void emit_tag(buf_t *out, const char *tag, const char *te, const char *name, int allow_remote,
                     cid_resolver resolve, void *ctx, int *blocked)
{
    const char *p = tag + 1;
    const char *end = te - 1;   /* at '>' */
    int selfclose = end > tag && end[-1] == '/';
    while (p < end && !isspace((unsigned char)*p) && *p != '/' && *p != '>') p++;
    buf_addc(out, '<');
    buf_adds(out, name);
    while (p < end) {
        char an[32];
        size_t al = 0;
        const char *vs = NULL;
        size_t vl = 0;
        while (p < end && (isspace((unsigned char)*p) || *p == '/')) p++;
        if (p >= end) break;
        while (p < end && !isspace((unsigned char)*p) && *p != '=' && *p != '>' && *p != '/') {
            if (al + 1 < sizeof(an)) an[al++] = (char)ascii_lower((unsigned char)*p);
            p++;
        }
        an[al] = '\0';
        while (p < end && isspace((unsigned char)*p)) p++;
        if (p < end && *p == '=') {
            p++;
            while (p < end && isspace((unsigned char)*p)) p++;
            if (p < end && (*p == '"' || *p == '\'')) {
                char q = *p++;
                vs = p;
                while (p < end && *p != q) p++;
                vl = (size_t)(p - vs);
                if (p < end) p++;
            } else {
                vs = p;
                while (p < end && !isspace((unsigned char)*p)) p++;
                vl = (size_t)(p - vs);
            }
        }
        if (!al) { if (p < end) p++; continue; }
        if (an[0] == 'o' && an[1] == 'n') continue;           /* event handlers */
        if (!strcmp(an, "srcdoc") || !strcmp(an, "formaction") || !strcmp(an, "action")) continue;
        {
            buf_t raw, val;
            buf_init(&raw);
            buf_init(&val);
            if (vs) {
                /* decode entities inside the value so "javascript&#58;" is caught */
                const char *q = vs, *qe = vs + vl;
                while (q < qe) {
                    uint32_t cp;
                    size_t l = *q == '&' ? entity(q, (size_t)(qe - q), &cp) : 0;
                    if (l) { utf8_put(&raw, cp); q += l; }
                    else buf_addc(&raw, *q++);
                }
            }
            if (!strcmp(an, "href") || !strcmp(an, "src") || !strcmp(an, "background") ||
                !strcmp(an, "poster") || !strcmp(an, "srcset") || !strcmp(an, "lowsrc") ||
                !strcmp(an, "dynsrc") || !strcmp(an, "xlink:href")) {
                const char *v = buf_str(&raw);
                if (dangerous_url(v)) { buf_free(&raw); continue; }
                if (strcmp(an, "href")) {
                    while (*v == ' ') v++;
                    if (starts_with_i(v, "cid:")) {
                        buf_t uri;
                        buf_init(&uri);
                        if (resolve && resolve(ctx, v + 4, &uri) == 0) buf_add(&val, uri.p, uri.len);
                        buf_free(&uri);
                        if (!val.len) { buf_free(&raw); continue; }
                    } else if (url_is_remote(v, strlen(v)) && !allow_remote) {
                        *blocked = 1;
                        buf_free(&raw);
                        continue;
                    } else {
                        buf_adds(&val, v);
                    }
                } else {
                    buf_adds(&val, v);
                }
            } else if (!strcmp(an, "style")) {
                css_filter(&val, buf_str(&raw), raw.len, allow_remote, blocked);
            } else {
                buf_add(&val, buf_str(&raw), raw.len);
            }
            buf_addc(out, ' ');
            buf_adds(out, an);
            if (vs) {
                buf_adds(out, "=\"");
                attr_value_escape(out, buf_str(&val));
                buf_addc(out, '"');
            }
            buf_free(&raw);
            buf_free(&val);
        }
    }
    if (selfclose) buf_adds(out, " /");
    buf_addc(out, '>');
}

void html_sanitize(buf_t *out, const char *html, size_t n, int allow_remote,
                   cid_resolver resolve, void *ctx, int *blocked)
{
    static const char *DROP_ALL[] = {"script", "iframe", "frame", "frameset", "object", "embed",
                                     "applet", "svg", "math", "template", NULL};
    static const char *DROP_TAG[] = {"meta", "base", "form", "input", "button", "select", "textarea",
                                     "noscript", "link", "html", "body", "head", "!doctype", "title", NULL};
    const char *p = html, *end = html + n;
    *blocked = 0;
    while (p < end) {
        const char *lt = memchr(p, '<', (size_t)(end - p));
        char name[32];
        int closing;
        const char *te;
        int i, drop = 0;
        if (!lt) { buf_add(out, p, (size_t)(end - p)); break; }
        buf_add(out, p, (size_t)(lt - p));
        p = lt;
        if (p + 3 < end && !strncmp(p, "<!--", 4)) {
            const char *c = strstr(p + 4, "-->");
            p = c && c < end ? c + 3 : end;
            continue;
        }
        closing = p + 1 < end && p[1] == '/';
        if (!tag_name(p + 1 + closing, end, name, sizeof(name))) {
            buf_adds(out, "&lt;");
            p++;
            continue;
        }
        te = tag_end(p, end);
        for (i = 0; DROP_ALL[i]; i++)
            if (!strcmp(name, DROP_ALL[i])) drop = 2;
        for (i = 0; DROP_TAG[i]; i++)
            if (!strcmp(name, DROP_TAG[i])) drop = 1;
        if (!strcmp(name, "link") && !closing && !allow_remote) *blocked = 1;
        if (drop == 2) {
            if (!closing && !(te - 2 >= p && te[-2] == '/')) {
                const char *c = find_close(te, end, name);
                te = c < end ? tag_end(c, end) : end;
            }
            p = te;
            continue;
        }
        if (drop == 1) {
            if (!strcmp(name, "title") && !closing) {   /* the title text isn't shown either */
                const char *c = find_close(te, end, name);
                te = c < end ? tag_end(c, end) : end;
            }
            p = te;
            continue;
        }
        if (!strcmp(name, "style") && !closing) {
            const char *c = find_close(te, end, "style");
            buf_adds(out, "<style>");
            css_filter(out, te, (size_t)(c - te), allow_remote, blocked);
            buf_adds(out, "</style>");
            p = c < end ? tag_end(c, end) : end;
            continue;
        }
        if (closing) {
            buf_adds(out, "</");
            buf_adds(out, name);
            buf_addc(out, '>');
        } else {
            emit_tag(out, p, te, name, allow_remote, resolve, ctx, blocked);
        }
        p = te;
    }
}

void text_to_html(buf_t *out, const char *text, size_t n)
{
    size_t i = 0;
    while (i < n) {
        char c = text[i];
        if ((n - i > 8 && (strnieq(text + i, "https://", 8))) || (n - i > 7 && strnieq(text + i, "http://", 7))) {
            size_t j = i;
            while (j < n && !isspace((unsigned char)text[j]) && text[j] != '<' && text[j] != '>' &&
                   text[j] != '"')
                j++;
            while (j > i && strchr(".,;:!?)]'", text[j - 1])) j--;
            buf_adds(out, "<a href=\"");
            for (size_t k = i; k < j; k++) {
                if (text[k] == '&') buf_adds(out, "&amp;");
                else buf_addc(out, text[k]);
            }
            buf_adds(out, "\">");
            for (size_t k = i; k < j; k++) {
                if (text[k] == '&') buf_adds(out, "&amp;");
                else buf_addc(out, text[k]);
            }
            buf_adds(out, "</a>");
            i = j;
            continue;
        }
        switch (c) {
        case '&': buf_adds(out, "&amp;"); break;
        case '<': buf_adds(out, "&lt;"); break;
        case '>': buf_adds(out, "&gt;"); break;
        case '"': buf_adds(out, "&quot;"); break;
        case '\r': break;
        default: buf_addc(out, c);
        }
        i++;
    }
}
