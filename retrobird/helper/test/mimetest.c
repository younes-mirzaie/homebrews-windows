/* Unit checks for the codecs, MIME parser, HTML sanitizer and secret sealing. No network. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "rb.h"

static int fails, checks;

#define CHECK(cond, ...) do { checks++; if (!(cond)) { fails++; printf("FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } } while (0)
#define EQS(a, b) do { const char *_a = (a), *_b = (b); CHECK(_a && !strcmp(_a, _b), "got [%s] want [%s]", _a ? _a : "(null)", _b); } while (0)

static void t_base64(void)
{
    buf_t e, d;
    buf_init(&e); buf_init(&d);
    b64_encode(&e, "hello world!", 12, 0);
    EQS(e.p, "aGVsbG8gd29ybGQh");
    b64_decode(&d, e.p, e.len);
    EQS(d.p, "hello world!");
    buf_clear(&d);
    b64_decode(&d, "aGVs\r\nbG8=", 10);
    EQS(d.p, "hello");
    buf_free(&e); buf_free(&d);
}

static void t_qp(void)
{
    buf_t d, e;
    buf_init(&d); buf_init(&e);
    qp_decode(&d, "caf=C3=A9 =\r\nsoft=20break=3D", 28, 0);
    EQS(d.p, "caf\xc3\xa9 soft break=");
    buf_clear(&d);
    qp_decode(&d, "a=  \r\nb", 7, 0);
    EQS(d.p, "ab");
    qp_encode(&e, "na\xc3\xafve = ok\nline two \n.dot", 27);
    buf_clear(&d);
    qp_decode(&d, e.p, e.len, 0);
    EQS(d.p, "na\xc3\xafve = ok\r\nline two \r\n.dot");
    CHECK(!strstr(e.p, "\r\n."), "line starting with a dot must be encoded: %s", e.p);
    buf_free(&d); buf_free(&e);
}

static void t_headers(void)
{
    char *s;
    s = decode_header("=?UTF-8?B?w6TDtsO8?=");
    EQS(s, "\xc3\xa4\xc3\xb6\xc3\xbc"); free(s);
    s = decode_header("=?UTF-8?Q?Gr=C3=BC=C3=9Fe_aus?= =?UTF-8?Q?_Berlin?=");
    EQS(s, "Gr\xc3\xbc\xc3\x9f" "e aus Berlin"); free(s);
    s = decode_header("Re: =?iso-8859-1?q?caf=E9?= time");
    EQS(s, "Re: caf\xc3\xa9 time"); free(s);
    /* a UTF-8 character split across two encoded words */
    s = decode_header("=?utf-8?B?4oKs?= =?utf-8?B?IDEw?=");
    EQS(s, "\xe2\x82\xac 10"); free(s);
    s = decode_header("=?koi8-r?B?8NLJ18XU?=");
    EQS(s, "\xd0\x9f\xd1\x80\xd0\xb8\xd0\xb2\xd0\xb5\xd1\x82"); free(s);
    s = decode_header("Folded\r\n subject\r\n\tline");
    EQS(s, "Folded subject line"); free(s);
    s = decode_header("Raw latin1 caf\xe9");
    EQS(s, "Raw latin1 caf\xc3\xa9"); free(s);
    s = decode_header("not =?encoded");
    EQS(s, "not =?encoded"); free(s);

    s = hdr_param("attachment; filename*=UTF-8''%E2%82%AC%20rates.pdf", "filename");
    EQS(s, "\xe2\x82\xac rates.pdf"); free(s);
    s = hdr_param("attachment; filename*0=\"long \"; filename*1=\"name.txt\"", "filename");
    EQS(s, "long name.txt"); free(s);
    s = hdr_param("text/plain; charset=\"ISO-8859-1\"; format=flowed", "charset");
    EQS(s, "ISO-8859-1"); free(s);
    s = hdr_param("application/pdf; name=\"=?UTF-8?B?w6RiYy5wZGY=?=\"", "name");
    EQS(s, "\xc3\xa4" "bc.pdf"); free(s);
    s = hdr_param("multipart/mixed; boundary=abc", "charset");
    CHECK(s == NULL, "missing param should be NULL"); free(s);

    {
        buf_t o;
        buf_init(&o);
        encode_header_text(&o, "Gr\xc3\xbc\xc3\x9f" "e", 0);
        s = decode_header(o.p);
        EQS(s, "Gr\xc3\xbc\xc3\x9f" "e"); free(s);
        buf_free(&o);
    }
}

static void t_mutf7(void)
{
    char *s = imap_mutf7_decode("Entw&APw-rfe");
    EQS(s, "Entw\xc3\xbcrfe"); free(s);
    s = imap_mutf7_decode("A&-B");
    EQS(s, "A&B"); free(s);
    s = imap_mutf7_decode("&ZeVnLIqe-");
    EQS(s, "\xe6\x97\xa5\xe6\x9c\xac\xe8\xaa\x9e"); free(s);
}

static const char *MSG =
    "From: =?UTF-8?Q?J=C3=BCrgen?= <j@example.org>\r\n"
    "To: Ann <ann@example.com>, bob@example.com\r\n"
    "Subject: Report\r\n"
    "Date: Thu, 05 Nov 2026 13:44:41 +0100\r\n"
    "Message-ID: <abc@example.org>\r\n"
    "MIME-Version: 1.0\r\n"
    "Content-Type: multipart/mixed; boundary=\"outer\"\r\n"
    "\r\n"
    "preamble\r\n"
    "--outer\r\n"
    "Content-Type: multipart/alternative; boundary=inner\r\n"
    "\r\n"
    "--inner\r\n"
    "Content-Type: text/plain; charset=iso-8859-1\r\n"
    "Content-Transfer-Encoding: quoted-printable\r\n"
    "\r\n"
    "Hall=F6chen\r\n"
    "--inner\r\n"
    "Content-Type: text/html; charset=utf-8\r\n"
    "\r\n"
    "<p onclick=\"evil()\">Hi <b>there</b><script>alert(1)</script><img src=\"cid:logo@x\"><img src=\"http://track.example/p.gif\"></p>\r\n"
    "--inner--\r\n"
    "--outer\r\n"
    "Content-Type: image/png; name=logo.png\r\n"
    "Content-ID: <logo@x>\r\n"
    "Content-Transfer-Encoding: base64\r\n"
    "\r\n"
    "iVBORw0KGgo=\r\n"
    "--outer\r\n"
    "Content-Type: application/pdf\r\n"
    "Content-Disposition: attachment; filename=\"report.pdf\"\r\n"
    "Content-Transfer-Encoding: base64\r\n"
    "\r\n"
    "JVBERi0xLjQK\r\n"
    "--outer--\r\n"
    "epilogue\r\n";

static int resolve(void *ctx, const char *cid, buf_t *uri)
{
    mime_msg *m = ctx;
    for (int i = 0; i < m->nleaves; i++) {
        mime_part *p = mime_leaf(m, i);
        if (p && p->cid && !strcmp(p->cid, cid)) { buf_adds(uri, "data:image/png;base64,AAAA"); return 0; }
    }
    return -1;
}

static void t_mime(void)
{
    mime_msg *m = mime_parse(MSG, strlen(MSG));
    mime_view v;
    buf_t t, h, raw;
    int blocked = 0;
    char *from;
    mime_view_get(m, &v);
    CHECK(m->nleaves == 4, "leaves %d", m->nleaves);
    CHECK(v.text && v.html, "text and html found");
    CHECK(v.natts == 2, "atts %d", v.natts);
    buf_init(&t); buf_init(&h); buf_init(&raw);
    if (v.text) { mime_text(v.text, &t); EQS(t.p, "Hall\xc3\xb6" "chen"); }
    if (v.natts == 2) {
        EQS(v.atts[0]->cid, "logo@x");
        EQS(v.atts[1]->filename, "report.pdf");
        buf_t pdf;
        buf_init(&pdf);
        mime_decode(v.atts[1], &pdf);
        EQS(pdf.p, "%PDF-1.4\n");
        buf_free(&pdf);
    }
    if (v.html) {
        mime_text(v.html, &raw);
        html_sanitize(&h, raw.p, raw.len, 0, resolve, m, &blocked);
        CHECK(!strstr(h.p, "script") && !strstr(h.p, "alert"), "script removed: %s", h.p);
        CHECK(!strstr(h.p, "onclick"), "handler removed: %s", h.p);
        CHECK(strstr(h.p, "data:image/png;base64,AAAA") != NULL, "cid resolved: %s", h.p);
        CHECK(!strstr(h.p, "track.example"), "remote image blocked: %s", h.p);
        CHECK(blocked, "blocked flag set");
        buf_clear(&h);
        html_sanitize(&h, raw.p, raw.len, 1, resolve, m, &blocked);
        CHECK(strstr(h.p, "track.example") != NULL, "remote allowed when asked: %s", h.p);
    }
    from = hdr_get(m->root->hdr, m->root->hdr_len, "From");
    {
        char *d = decode_header(from);
        EQS(d, "J\xc3\xbcrgen <j@example.org>");
        free(d);
    }
    free(from);
    buf_free(&t); buf_free(&h); buf_free(&raw);
    mime_view_free(&v);
    mime_free(m);

    /* single-part, no Content-Type, LF line ends */
    m = mime_parse("Subject: x\n\nplain body\n", 24);
    mime_view_get(m, &v);
    CHECK(v.text && !v.html && v.natts == 0, "simple message");
    if (v.text) { buf_init(&t); mime_text(v.text, &t); EQS(t.p, "plain body\n"); buf_free(&t); }
    mime_view_free(&v);
    mime_free(m);
}

static void t_html(void)
{
    buf_t o;
    int blocked;
    buf_init(&o);
    html_to_text(&o, "<html><head><style>p{}</style></head><body><p>One &amp; two</p><p>Three<br>Four</p>"
                     "<ul><li>a</li><li>b</li></ul>&#8364;&nbsp;5</body></html>", 135);
    EQS(o.p, "One & two\n\nThree\nFour\n\n\xe2\x80\xa2 a\n\xe2\x80\xa2 b\n\xe2\x82\xac 5");
    buf_clear(&o);
    html_sanitize(&o, "<a href=\"javascript:alert(1)\">x</a><a href=\"JaVaScRiPt&#58;evil\">y</a><a href=\"https://ok.example/\">z</a>", 104, 0, NULL, NULL, &blocked);
    CHECK(!stristr(o.p, "javascript") && strstr(o.p, "https://ok.example/"), "links: %s", o.p);
    buf_clear(&o);
    html_sanitize(&o, "<div style=\"background:url(http://x/y.png)\">t</div><iframe src=x>in</iframe><style>@import url(http://a/b.css);</style>", 113, 0, NULL, NULL, &blocked);
    CHECK(!strstr(o.p, "url(http") && !strstr(o.p, "iframe") && !strstr(o.p, "@import") && blocked, "css: %s", o.p);
    buf_clear(&o);
    text_to_html(&o, "see https://a.example/x?y=1&z=2. <b>", 36);
    EQS(o.p, "see <a href=\"https://a.example/x?y=1&amp;z=2\">https://a.example/x?y=1&amp;z=2</a>. &lt;b&gt;");
    buf_free(&o);
}

typedef struct { char out[512]; } acc;
static void on_addr(void *ctx, const char *name, const char *addr)
{
    acc *a = ctx;
    size_t l = strlen(a->out);
    snprintf(a->out + l, sizeof(a->out) - l, "[%s|%s]", name, addr);
}

static void t_addr(void)
{
    acc a = {""};
    addr_parse("\"Doe, John\" <john@x.org>, ann@y.org; Bob (work) <bob@z.org>, Team: c@d.org, e@f.org;", on_addr, &a);
    EQS(a.out, "[Doe, John|john@x.org][|ann@y.org][Bob (work)|bob@z.org][|c@d.org][|e@f.org]");
}

static void t_build(void)
{
    mime_out o;
    size_t len;
    char *msgid, *raw;
    mime_msg *m;
    mime_view v;
    buf_t t;
    memset(&o, 0, sizeof(o));
    o.from_name = "J\xc3\xbcrgen";
    o.from_addr = "j@example.org";
    o.to = "Ann <ann@example.com>, \"Doe, John\" <john@x.org>";
    o.subject = "Gr\xc3\xbc\xc3\x9f" "e und ein sehr langer Betreff, der umbrochen werden muss, damit alles passt";
    o.body = "Hallo,\n\nd\xc3\xa4s ist ein Test.\n.\nEnde";
    o.in_reply_to = "<abc@example.org>";
    raw = mime_build(&o, &len, &msgid);
    CHECK(strstr(raw, "In-Reply-To: <abc@example.org>") != NULL, "in-reply-to");
    CHECK(msgid && strstr(raw, msgid), "message-id");
    CHECK(strstr(raw, "\"Doe, John\" <john@x.org>") != NULL, "quoted name kept: %s", raw);
    m = mime_parse(raw, len);
    {
        char *s = hdr_get(m->root->hdr, m->root->hdr_len, "Subject"), *d = decode_header(s);
        EQS(d, o.subject);
        free(s); free(d);
        s = hdr_get(m->root->hdr, m->root->hdr_len, "From"); d = decode_header(s);
        EQS(d, "J\xc3\xbcrgen <j@example.org>");
        free(s); free(d);
    }
    mime_view_get(m, &v);
    buf_init(&t);
    if (v.text) mime_text(v.text, &t);
    EQS(t.p, "Hallo,\r\n\r\nd\xc3\xa4s ist ein Test.\r\n.\r\nEnde\r\n");
    buf_free(&t);
    mime_view_free(&v);
    mime_free(m);
    free(raw);
    free(msgid);
}

static void t_dates(void)
{
    char *s;
    CHECK(parse_rfc5322_date("Thu, 05 Nov 2026 13:44:41 +0100") == 1793882681, "date %lld", (long long)parse_rfc5322_date("Thu, 05 Nov 2026 13:44:41 +0100"));
    CHECK(parse_imap_date("05-Nov-2026 12:44:41 +0000") == 1793882681, "imap date");
    CHECK(parse_rfc5322_date("5 Nov 26 12:44 GMT") == 1793882640, "short date");
    s = rfc5322_date(1793882681);
    EQS(s, "Thu, 05 Nov 2026 12:44:41 +0000");
    free(s);
}

static void t_seal(void)
{
    char *s = secret_seal("token-123456", "p\xc3\xa4ss word!");
    char *o = s ? secret_open("token-123456", s) : NULL;
    char *bad = s ? secret_open("other-token", s) : NULL;
    CHECK(s != NULL, "sealed");
    EQS(o, "p\xc3\xa4ss word!");
    CHECK(bad == NULL, "wrong key must fail");
    free(s); free(o); free(bad);
}

static void t_charsets(void)
{
    buf_t o;
    buf_init(&o);
    charset_to_utf8(&o, "windows-1252", "\x93quoted\x94 \x80", 10);
    EQS(o.p, "\xe2\x80\x9cquoted\xe2\x80\x9d \xe2\x82\xac");
    buf_clear(&o);
    charset_to_utf8(&o, "windows-1256", "\xd3\xe1\xc7\xe3", 4);   /* salaam */
    EQS(o.p, "\xd8\xb3\xd9\x84\xd8\xa7\xd9\x85");
    buf_clear(&o);
    charset_to_utf8(&o, "utf-8", "bad \xff byte", 10);
    EQS(o.p, "bad \xc3\xbf byte");                      /* really latin-1 */
    buf_clear(&o);
    charset_to_utf8(&o, "utf-8", "\xc3\xa9 \xff", 4);
    EQS(o.p, "\xc3\xa9 \xef\xbf\xbd");                     /* real UTF-8 with one bad byte */
    buf_free(&o);
}

int main(void)
{
    if (net_init(NULL) != 0) { printf("NSS init failed\n"); return 1; }
    t_base64();
    t_qp();
    t_headers();
    t_mutf7();
    t_mime();
    t_html();
    t_addr();
    t_build();
    t_dates();
    t_seal();
    t_charsets();
    printf("%d checks, %d failed\n", checks, fails);
    return fails ? 1 : 0;
}
