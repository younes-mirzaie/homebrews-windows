/* rfxfsd web access: a small HTTP server for managing the phone's files from a browser on the
   same Wi-Fi. Off until the app turns it on (WEB on PORT PASSWORD); the app shows the address
   and password. #included by rfxfsd.c (uses its file helpers).

   Security: HTTP Basic auth (any user name, the password), slowed down after failures; state-
   changing requests are POSTs that must carry "X-RFX: 1" (a header other sites can't add
   without a CORS preflight, which is never answered), so a web page elsewhere can't use the
   browser's saved login to delete files. Plain HTTP: meant for a home network. */

#define WEB_HDR_MAX 8192

static volatile LONG gWebOn;
static SOCKET gWebListen = INVALID_SOCKET;
static int gWebPort;
static char gWebPass[64];
static volatile LONG gWebFailures;


/* ---------- small string helpers ---------- */

typedef struct Buf { char *p; size_t len, cap; } Buf;

static void buf_add(Buf *b, const char *s, size_t n)
{
    if (b->len + n + 1 > b->cap) {
        b->cap = (b->len + n + 1) * 2;
        b->p = (char *)realloc(b->p, b->cap);
    }
    memcpy(b->p + b->len, s, n);
    b->len += n;
    b->p[b->len] = 0;
}
static void buf_str(Buf *b, const char *s) { buf_add(b, s, strlen(s)); }
static void buf_fmt(Buf *b, const char *fmt, ...)
{
    char tmp[512];
    va_list ap;
    int n;
    va_start(ap, fmt);
    n = _vsnprintf(tmp, sizeof tmp - 1, fmt, ap);
    va_end(ap);
    if (n < 0) n = sizeof tmp - 1;
    buf_add(b, tmp, n);
}
static void buf_json_str(Buf *b, const char *s)
{
    buf_add(b, "\"", 1);
    for (; *s; s++) {
        unsigned char c = (unsigned char)*s;
        if (c == '"' || c == '\\') { buf_add(b, "\\", 1); buf_add(b, s, 1); }
        else if (c < 0x20) buf_fmt(b, "\\u%04x", c);
        else buf_add(b, s, 1);
    }
    buf_add(b, "\"", 1);
}

static int hexval(int c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

/* Finds query parameter `name` in "a=1&b=2" and URL-decodes it into a malloc'd string. */
static char *query_param(const char *q, const char *name)
{
    size_t nl = strlen(name);
    while (q && *q) {
        const char *amp = strchr(q, '&');
        const char *end = amp ? amp : q + strlen(q);
        if ((size_t)(end - q) > nl && !strncmp(q, name, nl) && q[nl] == '=') {
            const char *v = q + nl + 1;
            char *out = (char *)malloc(end - v + 1), *o = out;
            while (v < end) {
                if (*v == '%' && v + 2 < end && hexval(v[1]) >= 0 && hexval(v[2]) >= 0) {
                    *o++ = (char)(hexval(v[1]) * 16 + hexval(v[2]));
                    v += 3;
                } else if (*v == '+') { *o++ = ' '; v++; }
                else *o++ = *v++;
            }
            *o = 0;
            return out;
        }
        q = amp ? amp + 1 : NULL;
    }
    return NULL;
}

static int b64val(int c)
{
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

static int b64decode(const char *in, char *out, int max)
{
    int bits = 0, n = 0, acc = 0;
    for (; *in && *in != '='; in++) {
        int v = b64val((unsigned char)*in);
        if (v < 0) break;
        acc = (acc << 6) | v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            if (n < max - 1) out[n++] = (char)((acc >> bits) & 0xFF);
        }
    }
    out[n] = 0;
    return n;
}

/* ---------- HTTP plumbing ---------- */

typedef struct Req {
    SOCKET s;
    char method[8];
    char path[2048];      /* without query */
    char *query;          /* points into path storage */
    char auth[256];
    char xrfx;
    unsigned long long contentLength;
    char *body;           /* bytes already read past the headers */
    int bodyLen;
} Req;

static int web_send(SOCKET s, const void *p, int n)
{
    const char *c = (const char *)p;
    while (n > 0) {
        int k = send(s, c, n, 0);
        if (k <= 0) return 0;
        c += k; n -= k;
    }
    return 1;
}

static void web_reply(SOCKET s, int status, const char *type, const char *body, size_t len, const char *extra)
{
    char head[1024];
    const char *text = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 403 ? "Forbidden" :
                       status == 404 ? "Not Found" : status == 400 ? "Bad Request" : "Error";
    int n = _snprintf(head, sizeof head - 1,
                      "HTTP/1.1 %d %s\r\nContent-Type: %s\r\nContent-Length: %lu\r\nConnection: close\r\n"
                      "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n%s\r\n",
                      status, text, type, (unsigned long)len, extra ? extra : "");
    web_send(s, head, n);
    if (len) web_send(s, body, (int)len);
}

static void web_json_error(SOCKET s, int status, const char *msg)
{
    Buf b = {0};
    buf_str(&b, "{\"error\":");
    buf_json_str(&b, msg);
    buf_str(&b, "}");
    web_reply(s, status, "application/json; charset=utf-8", b.p, b.len, NULL);
    free(b.p);
}

static void web_win_error(SOCKET s, DWORD code, const WCHAR *where)
{
    char msg[512], full[1024];
    char *w = where ? w_to_utf8(where) : NULL;
    DWORD n = FormatMessageA(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, code, 0, msg, sizeof msg, NULL);
    while (n && (msg[n - 1] == '\r' || msg[n - 1] == '\n' || msg[n - 1] == ' ' || msg[n - 1] == '.')) msg[--n] = 0;
    if (!n) _snprintf(msg, sizeof msg, "Windows error %lu", (unsigned long)code);
    _snprintf(full, sizeof full - 1, "%s%s%s", msg, w ? ": " : "", w ? w : "");
    full[sizeof full - 1] = 0;
    free(w);
    web_json_error(s, 500, full);
}

static void web_ok(SOCKET s) { web_reply(s, 200, "application/json", "{\"ok\":true}", 11, NULL); }

/* Reads the request line and headers. Returns 0 on a bad or closed request. */
static int web_read_request(Req *r, char *buf, int cap)
{
    int len = 0;
    char *end = NULL, *line, *next;
    while (!end) {
        int k;
        if (len >= cap - 1) return 0;
        k = recv(r->s, buf + len, cap - 1 - len, 0);
        if (k <= 0) return 0;
        len += k;
        buf[len] = 0;
        end = strstr(buf, "\r\n\r\n");
    }
    *end = 0;
    r->body = end + 4;
    r->bodyLen = (int)(len - (r->body - buf));

    line = buf;
    next = strstr(line, "\r\n");
    if (next) *next = 0;
    {
        char *sp1 = strchr(line, ' '), *sp2;
        if (!sp1) return 0;
        *sp1 = 0;
        strncpy(r->method, line, sizeof r->method - 1);
        sp2 = strchr(sp1 + 1, ' ');
        if (sp2) *sp2 = 0;
        strncpy(r->path, sp1 + 1, sizeof r->path - 1);
        r->query = strchr(r->path, '?');
        if (r->query) *r->query++ = 0;
    }
    while (next) {
        line = next + 2;
        next = strstr(line, "\r\n");
        if (next) *next = 0;
        if (!_strnicmp(line, "Authorization:", 14)) {
            const char *v = line + 14;
            while (*v == ' ') v++;
            strncpy(r->auth, v, sizeof r->auth - 1);
        } else if (!_strnicmp(line, "Content-Length:", 15)) {
            r->contentLength = _strtoui64(line + 15, NULL, 10);
        } else if (!_strnicmp(line, "X-RFX:", 6)) {
            r->xrfx = 1;
        }
    }
    return 1;
}

static int web_authorized(Req *r)
{
    char dec[192], *colon;
    size_t a, b, i;
    unsigned diff;
    if (_strnicmp(r->auth, "Basic ", 6)) return 0;
    b64decode(r->auth + 6, dec, sizeof dec);
    colon = strchr(dec, ':');
    if (!colon) return 0;
    colon++;
    a = strlen(colon); b = strlen(gWebPass);
    diff = (unsigned)(a ^ b);
    for (i = 0; i < a && i < b; i++) diff |= (unsigned)(colon[i] ^ gWebPass[i]);
    return diff == 0 && b > 0;
}

/* ---------- API ---------- */

static WCHAR *param_w(Req *r, const char *name)
{
    char *v = query_param(r->query, name);
    WCHAR *w;
    if (!v) return NULL;
    w = utf8_to_w(v);
    free(v);
    return w;
}

static void api_drives(SOCKET s)
{
    WCHAR root[4] = L"A:\\";
    Buf b = {0};
    int first = 1;
    buf_str(&b, "[");
    for (root[0] = L'A'; root[0] <= L'Z'; root[0]++) {
        ULARGE_INTEGER avail, total, freeb;
        WCHAR label[MAX_PATH + 1] = L"";
        char *ul;
        if (!GetDiskFreeSpaceExW(root, &avail, &total, &freeb)) continue;
        GetVolumeInformationW(root, label, MAX_PATH, NULL, NULL, NULL, NULL, 0);
        ul = w_to_utf8(label);
        buf_fmt(&b, "%s{\"p\":\"%c:\\\\\",\"label\":", first ? "" : ",", (char)root[0]);
        buf_json_str(&b, ul);
        buf_fmt(&b, ",\"total\":%llu,\"free\":%llu}", total.QuadPart, avail.QuadPart);
        free(ul);
        first = 0;
    }
    buf_str(&b, "]");
    web_reply(s, 200, "application/json; charset=utf-8", b.p, b.len, NULL);
    free(b.p);
}

static void api_list(SOCKET s, const WCHAR *dir)
{
    WIN32_FIND_DATAW fd;
    HANDLE h;
    Buf b = {0};
    int first = 1;
    WCHAR *pattern = path_join(dir, L"*");
    h = FindFirstFileExW(pattern, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    free(pattern);
    if (h == INVALID_HANDLE_VALUE && GetLastError() != ERROR_FILE_NOT_FOUND) { web_win_error(s, GetLastError(), dir); return; }
    buf_str(&b, "[");
    if (h != INVALID_HANDLE_VALUE) {
        do {
            char *u;
            if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
            u = w_to_utf8(fd.cFileName);
            buf_str(&b, first ? "{\"n\":" : ",{\"n\":");
            buf_json_str(&b, u);
            buf_fmt(&b, ",\"a\":%lu,\"s\":%llu,\"m\":%llu}", (unsigned long)fd.dwFileAttributes,
                    ((unsigned long long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow, ft64(fd.ftLastWriteTime));
            free(u);
            first = 0;
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    buf_str(&b, "]");
    web_reply(s, 200, "application/json; charset=utf-8", b.p, b.len, NULL);
    free(b.p);
}

static void api_download(SOCKET s, const WCHAR *path)
{
    HANDLE h = open_file(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, OPEN_EXISTING,
                         FILE_FLAG_SEQUENTIAL_SCAN);
    FILE_STANDARD_INFO si;
    char head[1536], *name, *enc, *o;
    const char *n;
    BYTE *buf;
    DWORD got;
    int len;
    if (h == INVALID_HANDLE_VALUE) { web_win_error(s, GetLastError(), path); return; }
    if (!GetFileInformationByHandleEx(h, FileStandardInfo, &si, sizeof si) || si.Directory) {
        CloseHandle(h);
        web_json_error(s, 400, "not a file");
        return;
    }
    /* RFC 5987 file name so non-ASCII names survive */
    {
        const WCHAR *slash = wcsrchr(path, L'\\');
        name = w_to_utf8(slash ? slash + 1 : path);
    }
    enc = (char *)malloc(strlen(name) * 3 + 1);
    for (n = name, o = enc; *n; n++) {
        unsigned char c = (unsigned char)*n;
        if (isalnum(c) || c == '.' || c == '-' || c == '_') *o++ = (char)c;
        else o += sprintf(o, "%%%02X", c);
    }
    *o = 0;
    len = _snprintf(head, sizeof head - 1,
                    "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: %llu\r\n"
                    "Content-Disposition: attachment; filename*=UTF-8''%s\r\nConnection: close\r\n\r\n",
                    (unsigned long long)si.EndOfFile.QuadPart, enc);
    free(name); free(enc);
    if (len < 0) len = sizeof head - 1;
    web_send(s, head, len);
    buf = (BYTE *)malloc(IO_CHUNK);
    while (ReadFile(h, buf, IO_CHUNK, &got, NULL) && got > 0)
        if (!web_send(s, buf, (int)got)) break;
    free(buf);
    CloseHandle(h);
}

/* POST /api/upload?p=FOLDER&n=NAME, body = the file. Streams to NAME.rfxtmp, then renames. */
static void api_upload(Req *r, const WCHAR *dir, const WCHAR *name)
{
    WCHAR *dst = path_join(dir, name), *tmp;
    HANDLE h;
    unsigned long long left = r->contentLength;
    BYTE *buf;
    DWORD err = 0, put;
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!*name || wcschr(name, L'\\') || wcschr(name, L'/') || wcschr(name, L':') || !wcscmp(name, L".") || !wcscmp(name, L"..")) { free(dst); web_json_error(r->s, 400, "bad file name"); return; }
    tmp = (WCHAR *)malloc((wcslen(dst) + 16) * sizeof(WCHAR));
    wcscpy(tmp, dst);
    wcscat(tmp, L".rfxtmp");
    h = open_file(tmp, GENERIC_WRITE, 0, CREATE_ALWAYS, FILE_FLAG_SEQUENTIAL_SCAN);
    if (h == INVALID_HANDLE_VALUE) err = GetLastError();
    buf = (BYTE *)malloc(IO_CHUNK);
    if (r->bodyLen > 0) {
        int n = (unsigned long long)r->bodyLen > left ? (int)left : r->bodyLen;
        if (!err && (!WriteFile(h, r->body, n, &put, NULL) || put != (DWORD)n)) err = GetLastError();
        left -= n;
    }
    while (left > 0) {
        int k = recv(r->s, (char *)buf, left > IO_CHUNK ? IO_CHUNK : (int)left, 0);
        if (k <= 0) { if (!err) err = ERROR_CONNECTION_ABORTED; break; }
        if (!err && (!WriteFile(h, buf, k, &put, NULL) || put != (DWORD)k)) err = GetLastError();
        left -= k;
    }
    free(buf);
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (!err) {
        if (get_attr(dst, &a)) make_writable(dst, a.dwFileAttributes);
        if (!MoveFileExW(tmp, dst, MOVEFILE_REPLACE_EXISTING)) err = GetLastError();
    }
    if (err) { DeleteFileW(tmp); web_win_error(r->s, err, dst); }
    else web_ok(r->s);
    free(tmp); free(dst);
}

static void web_handle(Req *r)
{
    const char *p = r->path;
    int post = !strcmp(r->method, "POST");
    WCHAR *a = NULL, *b = NULL;

    if (!web_authorized(r)) {
        LONG f = InterlockedIncrement(&gWebFailures);
        if (f > 5) Sleep(f > 50 ? 5000 : 1000);   /* slow down guessing */
        web_reply(r->s, 401, "text/plain", "password needed", 15, "WWW-Authenticate: Basic realm=\"Lumia files\", charset=\"UTF-8\"\r\n");
        return;
    }
    gWebFailures = 0;

    if (!strcmp(p, "/") && !post) { web_reply(r->s, 200, "text/html; charset=utf-8", kWebPage, strlen(kWebPage), NULL); return; }
    if (!strcmp(p, "/favicon.ico")) { web_reply(r->s, 404, "text/plain", "", 0, NULL); return; }
    if (post && !r->xrfx) { web_json_error(r->s, 403, "missing X-RFX header"); return; }

    a = param_w(r, "p");
    b = param_w(r, "to");
    if (!strcmp(p, "/api/drives")) api_drives(r->s);
    else if (!a) web_json_error(r->s, 400, "missing p");
    else if (!strcmp(p, "/api/list")) api_list(r->s, a);
    else if (!strcmp(p, "/dl")) api_download(r->s, a);
    else if (!post) web_json_error(r->s, 404, "unknown request");
    else if (!strcmp(p, "/api/upload")) {
        WCHAR *n = param_w(r, "n");
        if (!n) web_json_error(r->s, 400, "missing n"); else api_upload(r, a, n);
        free(n);
    }
    else if (!strcmp(p, "/api/mkdir")) {
        if (CreateDirectoryW(a, NULL)) web_ok(r->s); else web_win_error(r->s, GetLastError(), a);
    }
    else if (!strcmp(p, "/api/delete")) {
        Conn c;
        DWORD err = 0;
        WCHAR *errPath = NULL;
        memset(&c, 0, sizeof c);
        c.quiet = 1;
        if (is_protected(a)) web_json_error(r->s, 403, "refusing to delete a drive or top system folder");
        else if (!delete_tree(&c, a, &err, &errPath)) { web_win_error(r->s, err, errPath); free(errPath); }
        else web_ok(r->s);
    }
    else if (!b) web_json_error(r->s, 400, "missing to");
    else if ((!strcmp(p, "/api/rename") || !strcmp(p, "/api/move")) && is_protected(a))
        web_json_error(r->s, 403, "refusing to move or rename a drive or top system folder");
    else if (!strcmp(p, "/api/rename")) {
        if (MoveFileExW(a, b, 0)) web_ok(r->s); else web_win_error(r->s, GetLastError(), a);
    }
    else if (!strcmp(p, "/api/copy") || !strcmp(p, "/api/move")) {
        Conn c;
        DWORD err = 0;
        WCHAR *errPath = NULL;
        int move = !strcmp(p, "/api/move");
        memset(&c, 0, sizeof c);
        c.quiet = 1;
        c.buf = (BYTE *)malloc(IO_CHUNK);
        if (is_inside(a, b)) web_json_error(r->s, 400, "can't put a folder inside itself");
        else if (move && MoveFileExW(a, b, 0)) web_ok(r->s);
        else if (move && GetLastError() != ERROR_NOT_SAME_DEVICE) web_win_error(r->s, GetLastError(), a);
        else if (!copy_tree(&c, a, b, 0, &err, &errPath)) { web_win_error(r->s, err, errPath); free(errPath); }
        else if (move && !delete_tree(&c, a, &err, &errPath)) { web_win_error(r->s, err, errPath); free(errPath); }
        else web_ok(r->s);
        free(c.buf);
    }
    else web_json_error(r->s, 404, "unknown request");
    free(a); free(b);
}

static unsigned __stdcall web_serve(void *arg)
{
    Req r;
    char *buf = (char *)malloc(WEB_HDR_MAX);
    memset(&r, 0, sizeof r);
    r.s = (SOCKET)(UINT_PTR)arg;
    {
        DWORD tmo = 120000;
        setsockopt(r.s, SOL_SOCKET, SO_RCVTIMEO, (const char *)&tmo, sizeof tmo);
    }
    if (web_read_request(&r, buf, WEB_HDR_MAX)) web_handle(&r);
    else web_reply(r.s, 400, "text/plain", "bad request", 11, NULL);
    shutdown(r.s, SD_SEND);
    closesocket(r.s);
    free(buf);
    return 0;
}

static unsigned __stdcall web_accept_loop(void *arg)
{
    SOCKET ls = (SOCKET)(UINT_PTR)arg;
    for (;;) {
        SOCKET s = accept(ls, NULL, NULL);
        HANDLE t;
        if (s == INVALID_SOCKET) break;
        t = (HANDLE)_beginthreadex(NULL, 0, web_serve, (void *)(UINT_PTR)s, 0, NULL);
        if (t) CloseHandle(t); else closesocket(s);
    }
    return 0;
}

/* The phone's Wi-Fi address: "connect" a UDP socket (sends nothing) and read the local end. */
static void local_ip(char *out, int max)
{
    SOCKET u = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    struct sockaddr_in a, me;
    int len = sizeof me;
    strncpy(out, "", max);
    memset(&a, 0, sizeof a);
    a.sin_family = AF_INET;
    a.sin_port = htons(53);
    a.sin_addr.s_addr = inet_addr("8.8.8.8");
    if (u != INVALID_SOCKET && !connect(u, (struct sockaddr *)&a, sizeof a) && !getsockname(u, (struct sockaddr *)&me, &len)) {
        unsigned char *b = (unsigned char *)&me.sin_addr;
        _snprintf(out, max, "%u.%u.%u.%u", b[0], b[1], b[2], b[3]);
    }
    if (u != INVALID_SOCKET) closesocket(u);
}

static CRITICAL_SECTION gWebLock;

static void web_init(void) { InitializeCriticalSectionEx(&gWebLock, 0, 0); }

static void web_stop_locked(void)
{
    if (gWebListen != INVALID_SOCKET) closesocket(gWebListen);
    gWebListen = INVALID_SOCKET;
    gWebOn = 0;
}

/* WEB on PORT PASSWORD | WEB off | WEB status  ->  OK on|off port ip */
static void cmd_web(Conn *c, int n, char **f)
{
    char ip[64];
    EnterCriticalSection(&gWebLock);
    if (n >= 4 && !strcmp(f[1], "on")) {
        int port = atoi(f[2]);
        if (strlen(f[3]) < 4 || port <= 0 || port > 65535) { LeaveCriticalSection(&gWebLock); send_msg(c, ERROR_INVALID_PARAMETER, "bad port or password"); return; }
        web_stop_locked();
        {
            struct sockaddr_in addr;
            int one = 1;
            SOCKET ls = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
            HANDLE t;
            setsockopt(ls, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&one, sizeof one);
            memset(&addr, 0, sizeof addr);
            addr.sin_family = AF_INET;
            addr.sin_port = htons((u_short)port);
            addr.sin_addr.s_addr = htonl(INADDR_ANY);
            if (bind(ls, (struct sockaddr *)&addr, sizeof addr) || listen(ls, 16)) {
                int e = WSAGetLastError();
                closesocket(ls);
                LeaveCriticalSection(&gWebLock);
                send_err(c, (DWORD)e, NULL);
                return;
            }
            strncpy(gWebPass, f[3], sizeof gWebPass - 1);
            gWebPass[sizeof gWebPass - 1] = 0;
            gWebPort = port;
            gWebListen = ls;
            gWebOn = 1;
            gWebFailures = 0;
            t = (HANDLE)_beginthreadex(NULL, 0, web_accept_loop, (void *)(UINT_PTR)ls, 0, NULL);
            if (t) CloseHandle(t);
        }
    } else if (n >= 2 && !strcmp(f[1], "off")) {
        web_stop_locked();
    }
    local_ip(ip, sizeof ip);
    sendf(c, "OK\t%s\t%d\t%s\n", gWebOn ? "on" : "off", gWebPort, ip);
    LeaveCriticalSection(&gWebLock);
}
