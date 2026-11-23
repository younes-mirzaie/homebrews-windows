/* Growable buffers and small string helpers. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"

void *xmalloc(size_t n)
{
    void *p = malloc(n ? n : 1);
    if (!p) { fputs("rbmaild: out of memory\n", stderr); abort(); }
    return p;
}

void *xcalloc(size_t n, size_t m)
{
    void *p = calloc(n ? n : 1, m ? m : 1);
    if (!p) { fputs("rbmaild: out of memory\n", stderr); abort(); }
    return p;
}

void *xrealloc(void *p, size_t n)
{
    p = realloc(p, n ? n : 1);
    if (!p) { fputs("rbmaild: out of memory\n", stderr); abort(); }
    return p;
}

char *xstrdup(const char *s) { return xstrndup(s ? s : "", strlen(s ? s : "")); }

char *xstrndup(const char *s, size_t n)
{
    char *p = xmalloc(n + 1);
    memcpy(p, s, n);
    p[n] = '\0';
    return p;
}

char *xprintf(const char *fmt, ...)
{
    buf_t b;
    va_list ap;
    buf_init(&b);
    va_start(ap, fmt);
    buf_vprintf(&b, fmt, ap);
    va_end(ap);
    return buf_detach(&b);
}

void buf_init(buf_t *b) { b->p = NULL; b->len = b->cap = 0; }

void buf_free(buf_t *b)
{
    free(b->p);
    buf_init(b);
}

void buf_clear(buf_t *b)
{
    b->len = 0;
    if (b->p) b->p[0] = '\0';
}

static void grow(buf_t *b, size_t extra)
{
    size_t need = b->len + extra + 1;
    if (need <= b->cap) return;
    size_t cap = b->cap ? b->cap : 64;
    while (cap < need) cap *= 2;
    b->p = xrealloc(b->p, cap);
    b->cap = cap;
}

void buf_add(buf_t *b, const void *data, size_t n)
{
    grow(b, n);
    if (n) memcpy(b->p + b->len, data, n);
    b->len += n;
    b->p[b->len] = '\0';
}

void buf_adds(buf_t *b, const char *s) { if (s) buf_add(b, s, strlen(s)); }

void buf_addc(buf_t *b, int c)
{
    grow(b, 1);
    b->p[b->len++] = (char)c;
    b->p[b->len] = '\0';
}

void buf_vprintf(buf_t *b, const char *fmt, va_list ap)
{
    va_list cp;
    int n;
    va_copy(cp, ap);
    n = vsnprintf(NULL, 0, fmt, cp);
    va_end(cp);
    if (n < 0) return;
    grow(b, (size_t)n);
    vsnprintf(b->p + b->len, (size_t)n + 1, fmt, ap);
    b->len += (size_t)n;
}

void buf_printf(buf_t *b, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    buf_vprintf(b, fmt, ap);
    va_end(ap);
}

char *buf_detach(buf_t *b)
{
    char *p = b->p ? b->p : xstrdup("");
    buf_init(b);
    return p;
}

const char *buf_str(buf_t *b) { return b->p ? b->p : ""; }

int ascii_lower(int c) { return (c >= 'A' && c <= 'Z') ? c + 32 : c; }

int strieq(const char *a, const char *b)
{
    if (!a || !b) return a == b;
    while (*a && ascii_lower((unsigned char)*a) == ascii_lower((unsigned char)*b)) a++, b++;
    return *a == *b;
}

int strnieq(const char *a, const char *b, size_t n)
{
    size_t i;
    for (i = 0; i < n; i++) {
        int x = ascii_lower((unsigned char)a[i]), y = ascii_lower((unsigned char)b[i]);
        if (x != y) return 0;
        if (!x) return 1;
    }
    return 1;
}

const char *stristr(const char *hay, const char *needle)
{
    size_t n = strlen(needle);
    if (!n) return hay;
    for (; *hay; hay++)
        if (strnieq(hay, needle, n)) return hay;
    return NULL;
}

int starts_with_i(const char *s, const char *prefix) { return strnieq(s, prefix, strlen(prefix)); }

char *str_trim(char *s)
{
    char *e;
    while (*s == ' ' || *s == '\t' || *s == '\r' || *s == '\n') s++;
    e = s + strlen(s);
    while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r' || e[-1] == '\n')) *--e = '\0';
    return s;
}

void str_lower(char *s) { for (; s && *s; s++) *s = (char)ascii_lower((unsigned char)*s); }

void field_escape_n(buf_t *out, const char *s, size_t n)
{
    size_t i, start = 0;
    for (i = 0; i < n; i++) {
        const char *rep = NULL;
        switch (s[i]) {
        case '\\': rep = "\\\\"; break;
        case '\t': rep = "\\t"; break;
        case '\n': rep = "\\n"; break;
        case '\r': rep = "\\r"; break;
        case '\0': rep = " "; break;
        }
        if (rep) {
            buf_add(out, s + start, i - start);
            buf_adds(out, rep);
            start = i + 1;
        }
    }
    buf_add(out, s + start, n - start);
}

void field_escape(buf_t *out, const char *s) { if (s) field_escape_n(out, s, strlen(s)); }

void field_unescape_inplace(char *s)
{
    char *w = s;
    for (; *s; s++) {
        if (*s == '\\' && s[1]) {
            s++;
            *w++ = *s == 't' ? '\t' : *s == 'n' ? '\n' : *s == 'r' ? '\r' : *s;
        } else {
            *w++ = *s;
        }
    }
    *w = '\0';
}

int split_fields(char *line, char **out, int max)
{
    int n = 0;
    char *p = line;
    while (n < max) {
        char *tab = strchr(p, '\t');
        out[n++] = p;
        if (!tab) break;
        *tab = '\0';
        p = tab + 1;
    }
    for (int i = 0; i < n; i++) field_unescape_inplace(out[i]);
    return n;
}
