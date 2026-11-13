/* rfxfsd: the elevated file helper behind the RetroFiles app.

   Runs on the phone as whatever account CMD Injector's shell uses (SYSTEM), listens on
   127.0.0.1 only, and does file work the app's sandbox can't: list any folder, copy, move,
   delete, rename, attributes, read and write files. The app starts it through the shell on
   127.0.0.1:9999 and passes a fresh token; every connection must send that token first.

   Usage: rfxfsd.exe --token TOKEN [--port 8093] [--bind 127.0.0.1]

   Protocol (see PROTOCOL.md): one request per line, fields separated by TAB, UTF-8.
     reply  OK[\tfield...]\n   or   ERR\tcode\tmessage\n
     lists  OK\n then one row per line, ended by an empty line
     long operations send  P\tbytes\tfiles\tcurrent-path\n  lines before the final OK/ERR;
     the client cancels by sending any byte (or closing). */
#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0603
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <process.h>
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#define RFXFSD_VERSION "2"
#define LINE_MAX_BYTES 8192
#define IO_CHUNK (256 * 1024)
#define READ_MAX (4 * 1024 * 1024)

static char gToken[128];
static volatile LONG gQuit;
static SOCKET gListen = INVALID_SOCKET;

/* ---------- per-connection state ---------- */

typedef struct Conn {
    SOCKET s;
    char in[LINE_MAX_BYTES];
    int inLen;
    int cancelled;
    int quiet;               /* no socket to report to (web requests): no progress, no cancel */
    /* progress */
    unsigned long long bytes;
    unsigned long long files;
    DWORD lastTick;
    BYTE *buf;               /* IO_CHUNK copy buffer */
} Conn;

static int send_all(Conn *c, const void *data, int len)
{
    const char *p = (const char *)data;
    while (len > 0) {
        int n = send(c->s, p, len, 0);
        if (n <= 0) { c->cancelled = 1; return 0; }
        p += n; len -= n;
    }
    return 1;
}

static int send_str(Conn *c, const char *s) { return send_all(c, s, (int)strlen(s)); }

static int sendf(Conn *c, const char *fmt, ...)
{
    char tmp[LINE_MAX_BYTES * 2];
    va_list ap;
    int n;
    va_start(ap, fmt);
    n = _vsnprintf(tmp, sizeof tmp - 1, fmt, ap);
    va_end(ap);
    if (n < 0) n = sizeof tmp - 1;
    tmp[n] = 0;
    return send_all(c, tmp, n);
}

/* ---------- text helpers ---------- */

static WCHAR *utf8_to_w(const char *s)
{
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    WCHAR *w;
    if (n <= 0) return NULL;
    w = (WCHAR *)malloc(n * sizeof(WCHAR));
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}

static char *w_to_utf8(const WCHAR *w)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
    char *s;
    if (n <= 0) { s = (char *)malloc(1); s[0] = 0; return s; }
    s = (char *)malloc(n);
    WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL);
    return s;
}

static WCHAR *path_join(const WCHAR *dir, const WCHAR *name)
{
    size_t a = wcslen(dir), b = wcslen(name);
    WCHAR *p = (WCHAR *)malloc((a + b + 2) * sizeof(WCHAR));
    memcpy(p, dir, a * sizeof(WCHAR));
    if (a && dir[a - 1] != L'\\') p[a++] = L'\\';
    memcpy(p + a, name, (b + 1) * sizeof(WCHAR));
    return p;
}

static unsigned long long ft64(FILETIME ft)
{
    return ((unsigned long long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
}

static int send_err(Conn *c, DWORD code, const WCHAR *where)
{
    char msg[512];
    char *w = where ? w_to_utf8(where) : NULL;
    DWORD n = FormatMessageA(FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, code,
                             0, msg, sizeof msg, NULL);
    while (n && (msg[n - 1] == '\r' || msg[n - 1] == '\n' || msg[n - 1] == ' ' || msg[n - 1] == '.')) msg[--n] = 0;
    if (!n) _snprintf(msg, sizeof msg, "the phone couldn't do that (code %lu)", (unsigned long)code);
    sendf(c, "ERR\t%lu\t%s%s%s\n", (unsigned long)code, msg, w ? ": " : "", w ? w : "");
    free(w);
    return 0;
}

static int send_msg(Conn *c, DWORD code, const char *msg)
{
    return sendf(c, "ERR\t%lu\t%s\n", (unsigned long)code, msg);
}

/* Cancellation: any byte from the client (or a closed socket) during a long operation. */
static int check_cancel(Conn *c)
{
    fd_set rd;
    struct timeval tv = {0, 0};
    if (c->cancelled || c->quiet) return c->cancelled;
    FD_ZERO(&rd);
    FD_SET(c->s, &rd);
    if (select(0, &rd, NULL, NULL, &tv) > 0) {
        char b;
        recv(c->s, &b, 1, 0);   /* the cancel byte, or 0 on close */
        c->cancelled = 1;
    }
    return c->cancelled;
}

static void progress(Conn *c, const WCHAR *path, int force)
{
    DWORD now = GetTickCount();
    if (c->quiet) return;
    if (force || now - c->lastTick >= 250) {
        char *u = w_to_utf8(path);
        sendf(c, "P\t%llu\t%llu\t%s\n", c->bytes, c->files, u);
        free(u);
        c->lastTick = now;
    }
}

/* ---------- file primitives ---------- */

static HANDLE open_file(const WCHAR *path, DWORD access, DWORD share, DWORD disp, DWORD flags)
{
    CREATEFILE2_EXTENDED_PARAMETERS p;
    memset(&p, 0, sizeof p);
    p.dwSize = sizeof p;
    p.dwFileFlags = flags;
    p.dwFileAttributes = FILE_ATTRIBUTE_NORMAL;
    return CreateFile2(path, access, share, disp, &p);
}

static int get_attr(const WCHAR *path, WIN32_FILE_ATTRIBUTE_DATA *d)
{
    return GetFileAttributesExW(path, GetFileExInfoStandard, d) != 0;
}

/* Clears read-only/system/hidden so delete/overwrite works, like "del /f". */
#define SETTABLE_ATTRS (FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_ARCHIVE | \
                        FILE_ATTRIBUTE_TEMPORARY | FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_NOT_CONTENT_INDEXED)

static BOOL set_attrs(const WCHAR *path, DWORD attrs)
{
    attrs &= SETTABLE_ATTRS;
    return SetFileAttributesW(path, attrs ? attrs : FILE_ATTRIBUTE_NORMAL);
}

static void make_writable(const WCHAR *path, DWORD attrs)
{
    if (attrs & (FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_HIDDEN))
        set_attrs(path, attrs & ~(FILE_ATTRIBUTE_READONLY | FILE_ATTRIBUTE_SYSTEM | FILE_ATTRIBUTE_HIDDEN));
}

/* Copies one file's bytes, times and attributes. Returns 0 (and fills *err) on failure. */
static int copy_one(Conn *c, const WCHAR *src, const WCHAR *dst, int overwrite, DWORD *err)
{
    HANDLE in, out;
    FILE_BASIC_INFO bi;
    WIN32_FILE_ATTRIBUTE_DATA dd;
    DWORD got, put;
    int ok = 1;

    in = open_file(src, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, OPEN_EXISTING,
                   FILE_FLAG_SEQUENTIAL_SCAN);
    if (in == INVALID_HANDLE_VALUE) { *err = GetLastError(); return 0; }
    if (get_attr(dst, &dd)) {
        if (!overwrite) { CloseHandle(in); *err = ERROR_FILE_EXISTS; return 0; }
        make_writable(dst, dd.dwFileAttributes);
    }
    out = open_file(dst, GENERIC_WRITE, 0, CREATE_ALWAYS, FILE_FLAG_SEQUENTIAL_SCAN);
    if (out == INVALID_HANDLE_VALUE) { *err = GetLastError(); CloseHandle(in); return 0; }

    for (;;) {
        if (!ReadFile(in, c->buf, IO_CHUNK, &got, NULL)) { *err = GetLastError(); ok = 0; break; }
        if (got == 0) break;
        if (!WriteFile(out, c->buf, got, &put, NULL) || put != got) { *err = GetLastError(); ok = 0; break; }
        c->bytes += got;
        progress(c, src, 0);
        if (check_cancel(c)) { *err = ERROR_CANCELLED; ok = 0; break; }
    }
    if (ok && GetFileInformationByHandleEx(in, FileBasicInfo, &bi, sizeof bi)) {
        DWORD attrs = bi.FileAttributes;
        bi.FileAttributes = 0;   /* times only; attributes after close (read-only would block) */
        SetFileInformationByHandle(out, FileBasicInfo, &bi, sizeof bi);
        CloseHandle(out);
        set_attrs(dst, attrs);
    } else {
        CloseHandle(out);
    }
    CloseHandle(in);
    if (!ok) DeleteFileW(dst);
    else c->files++;
    return ok;
}

/* Is dst equal to src or inside it? (copying a folder into itself would never end) */
static int is_inside(const WCHAR *src, const WCHAR *dst)
{
    size_t n = wcslen(src);
    while (n && src[n - 1] == L'\\') n--;
    return _wcsnicmp(src, dst, n) == 0 && (dst[n] == 0 || dst[n] == L'\\');
}

/* Recursive copy. Junctions, symlinks and mount points inside the tree are skipped, so a copy
   never wanders into another volume. */
static int copy_tree(Conn *c, const WCHAR *src, const WCHAR *dst, int overwrite, DWORD *err, WCHAR **errPath)
{
    WIN32_FILE_ATTRIBUTE_DATA a;
    WIN32_FIND_DATAW fd;
    HANDLE h;
    WCHAR *pattern;
    int ok = 1;

    if (!get_attr(src, &a)) { *err = GetLastError(); *errPath = _wcsdup(src); return 0; }
    if (!(a.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
        if (!copy_one(c, src, dst, overwrite, err)) { *errPath = _wcsdup(src); return 0; }
        return 1;
    }
    if (a.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) return 1;
    if (!CreateDirectoryW(dst, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) {
        *err = GetLastError(); *errPath = _wcsdup(dst); return 0;
    }
    pattern = path_join(src, L"*");
    h = FindFirstFileExW(pattern, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, 0);
    free(pattern);
    if (h == INVALID_HANDLE_VALUE) {
        DWORD e = GetLastError();
        if (e == ERROR_FILE_NOT_FOUND || e == ERROR_NO_MORE_FILES) return 1;
        *err = e; *errPath = _wcsdup(src); return 0;
    }
    do {
        WCHAR *s, *d;
        if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
            continue;
        s = path_join(src, fd.cFileName);
        d = path_join(dst, fd.cFileName);
        ok = copy_tree(c, s, d, overwrite, err, errPath);
        free(s); free(d);
    } while (ok && FindNextFileW(h, &fd));
    FindClose(h);
    if (ok) set_attrs(dst, a.dwFileAttributes);
    return ok;
}

/* Recursive delete. A junction/symlink is removed as a link: its target is never touched. */
static int delete_tree(Conn *c, const WCHAR *path, DWORD *err, WCHAR **errPath)
{
    WIN32_FILE_ATTRIBUTE_DATA a;
    WIN32_FIND_DATAW fd;
    HANDLE h;
    WCHAR *pattern;
    int ok = 1;

    if (check_cancel(c)) { *err = ERROR_CANCELLED; *errPath = _wcsdup(path); return 0; }
    if (!get_attr(path, &a)) { *err = GetLastError(); *errPath = _wcsdup(path); return 0; }
    make_writable(path, a.dwFileAttributes);
    if (!(a.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
        if (!DeleteFileW(path)) { *err = GetLastError(); *errPath = _wcsdup(path); return 0; }
        c->files++;
        c->bytes += ((unsigned long long)a.nFileSizeHigh << 32) | a.nFileSizeLow;
        progress(c, path, 0);
        return 1;
    }
    if (!(a.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
        pattern = path_join(path, L"*");
        h = FindFirstFileExW(pattern, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, 0);
        free(pattern);
        if (h != INVALID_HANDLE_VALUE) {
            do {
                WCHAR *s;
                if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
                s = path_join(path, fd.cFileName);
                ok = delete_tree(c, s, err, errPath);
                free(s);
            } while (ok && FindNextFileW(h, &fd));
            FindClose(h);
        }
    }
    if (ok && !RemoveDirectoryW(path)) { *err = GetLastError(); *errPath = _wcsdup(path); ok = 0; }
    if (ok) c->files++;
    return ok;
}

/* ---------- commands ---------- */

static void cmd_list(Conn *c, const WCHAR *dir)
{
    WIN32_FIND_DATAW fd;
    HANDLE h;
    WCHAR *pattern = path_join(dir, L"*");
    h = FindFirstFileExW(pattern, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    free(pattern);
    if (h == INVALID_HANDLE_VALUE) {
        DWORD e = GetLastError();
        if (e != ERROR_FILE_NOT_FOUND) { send_err(c, e, dir); return; }
        send_str(c, "OK\n\n");
        return;
    }
    send_str(c, "OK\n");
    do {
        char *u;
        if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
        u = w_to_utf8(fd.cFileName);
        sendf(c, "%s\t%lx\t%llu\t%llu\t%llu\n", u, (unsigned long)fd.dwFileAttributes,
              ((unsigned long long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow,
              ft64(fd.ftLastWriteTime), ft64(fd.ftCreationTime));
        free(u);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    send_str(c, "\n");
}

static void cmd_stat(Conn *c, const WCHAR *path)
{
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!get_attr(path, &a)) { send_err(c, GetLastError(), path); return; }
    sendf(c, "OK\t%lx\t%llu\t%llu\t%llu\t%llu\n", (unsigned long)a.dwFileAttributes,
          ((unsigned long long)a.nFileSizeHigh << 32) | a.nFileSizeLow, ft64(a.ftCreationTime),
          ft64(a.ftLastAccessTime), ft64(a.ftLastWriteTime));
}

/* Folder size: bytes, files, folders (junctions not followed). */
static void du(Conn *c, const WCHAR *dir, unsigned long long *bytes, unsigned long long *files, unsigned long long *dirs)
{
    WIN32_FIND_DATAW fd;
    HANDLE h;
    WCHAR *pattern = path_join(dir, L"*");
    h = FindFirstFileExW(pattern, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    free(pattern);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            (*dirs)++;
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) {
                WCHAR *s = path_join(dir, fd.cFileName);
                du(c, s, bytes, files, dirs);
                free(s);
            }
        } else {
            (*files)++;
            *bytes += ((unsigned long long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        }
    } while (!check_cancel(c) && FindNextFileW(h, &fd));
    FindClose(h);
}

static void cmd_du(Conn *c, const WCHAR *dir)
{
    unsigned long long b = 0, f = 0, d = 0;
    du(c, dir, &b, &f, &d);
    if (c->cancelled) { send_msg(c, ERROR_CANCELLED, "canceled"); return; }
    sendf(c, "OK\t%llu\t%llu\t%llu\n", b, f, d);
}

static void cmd_drives(Conn *c)
{
    WCHAR root[4] = L"A:\\";
    send_str(c, "OK\n");
    for (root[0] = L'A'; root[0] <= L'Z'; root[0]++) {
        ULARGE_INTEGER avail, total, freeb;
        WCHAR label[MAX_PATH + 1] = L"", fs[MAX_PATH + 1] = L"";
        char *ul, *uf, r[4];
        if (!GetDiskFreeSpaceExW(root, &avail, &total, &freeb)) continue;
        GetVolumeInformationW(root, label, MAX_PATH, NULL, NULL, NULL, fs, MAX_PATH);
        ul = w_to_utf8(label); uf = w_to_utf8(fs);
        r[0] = (char)root[0]; r[1] = ':'; r[2] = '\\'; r[3] = 0;
        sendf(c, "%s\t%s\t%s\t%llu\t%llu\n", r, ul, uf, total.QuadPart, avail.QuadPart);
        free(ul); free(uf);
    }
    /* Mount points the phone uses inside C: (Data, EFIESP, DPP...) show up as folders; list the
       well-known ones so the app can show their free space too. */
    {
        static const WCHAR *mounts[] = {L"C:\\Data\\", L"C:\\EFIESP\\", L"C:\\DPP\\", L"C:\\MMOS\\", NULL};
        int i;
        for (i = 0; mounts[i]; i++) {
            ULARGE_INTEGER avail, total, freeb;
            WCHAR label[MAX_PATH + 1] = L"", fs[MAX_PATH + 1] = L"";
            WIN32_FILE_ATTRIBUTE_DATA a;
            char *um, *ul, *uf;
            if (!get_attr(mounts[i], &a) || !(a.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) continue;
            if (!GetDiskFreeSpaceExW(mounts[i], &avail, &total, &freeb)) continue;
            GetVolumeInformationW(mounts[i], label, MAX_PATH, NULL, NULL, NULL, fs, MAX_PATH);
            um = w_to_utf8(mounts[i]); ul = w_to_utf8(label); uf = w_to_utf8(fs);
            sendf(c, "%s\t%s\t%s\t%llu\t%llu\n", um, ul, uf, total.QuadPart, avail.QuadPart);
            free(um); free(ul); free(uf);
        }
    }
    send_str(c, "\n");
}

/* Drive roots and the top system folders are never deleted, moved or renamed, by the app or the web page. */
static int is_protected(const WCHAR *path)
{
    static const WCHAR *top[] = {L"C:\\Windows", L"C:\\Windows\\System32", L"C:\\Data", L"C:\\EFIESP",
                                 L"C:\\Data\\Users", L"C:\\Data\\Programs", L"C:\\Programs", NULL};
    WCHAR tmp[MAX_PATH];
    size_t n = wcslen(path);
    int i;
    while (n > 0 && path[n - 1] == L'\\') n--;
    if (n <= 2 || n >= MAX_PATH) return 1;
    memcpy(tmp, path, n * sizeof(WCHAR));
    tmp[n] = 0;
    for (i = 0; top[i]; i++) if (!_wcsicmp(tmp, top[i])) return 1;
    return 0;
}

static void cmd_copy(Conn *c, const WCHAR *src, const WCHAR *dst, int overwrite)
{
    DWORD err = 0;
    WCHAR *errPath = NULL;
    if (is_inside(src, dst)) { send_msg(c, ERROR_INVALID_PARAMETER, "can't copy a folder into itself"); return; }
    if (!copy_tree(c, src, dst, overwrite, &err, &errPath)) {
        send_err(c, err, errPath);
        free(errPath);
        return;
    }
    progress(c, src, 1);
    sendf(c, "OK\t%llu\t%llu\n", c->bytes, c->files);
}

static void cmd_move(Conn *c, const WCHAR *src, const WCHAR *dst, int overwrite, int renameOnly)
{
    DWORD err;
    WCHAR *errPath = NULL;
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (is_protected(src)) {
        send_msg(c, ERROR_ACCESS_DENIED, "refusing to move or rename a drive or top system folder");
        return;
    }
    if (is_inside(src, dst) && _wcsicmp(src, dst) != 0) {
        send_msg(c, ERROR_INVALID_PARAMETER, "can't move a folder into itself");
        return;
    }
    if (overwrite && get_attr(dst, &a) && !(a.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
        make_writable(dst, a.dwFileAttributes);
    if (MoveFileExW(src, dst, overwrite ? MOVEFILE_REPLACE_EXISTING : 0)) { send_str(c, "OK\n"); return; }
    err = GetLastError();
    if (renameOnly || err != ERROR_NOT_SAME_DEVICE) { send_err(c, err, src); return; }
    /* other volume (SD card, Data partition): copy, then delete the original */
    if (!copy_tree(c, src, dst, overwrite, &err, &errPath)) { send_err(c, err, errPath); free(errPath); return; }
    c->bytes = 0; c->files = 0;
    if (!delete_tree(c, src, &err, &errPath)) { send_err(c, err, errPath); free(errPath); return; }
    send_str(c, "OK\n");
}

static void cmd_delete(Conn *c, const WCHAR *path)
{
    DWORD err = 0;
    WCHAR *errPath = NULL;
    if (is_protected(path)) {
        send_msg(c, ERROR_ACCESS_DENIED, "refusing to delete a drive or top system folder");
        return;
    }
    if (!delete_tree(c, path, &err, &errPath)) { send_err(c, err, errPath); free(errPath); return; }
    progress(c, path, 1);
    sendf(c, "OK\t%llu\t%llu\n", c->bytes, c->files);
}

static void cmd_read(Conn *c, const WCHAR *path, unsigned long long offset, unsigned long count)
{
    HANDLE h;
    LARGE_INTEGER pos, size;
    DWORD got;
    BYTE *data;
    FILE_STANDARD_INFO si;
    if (count > READ_MAX) count = READ_MAX;
    h = open_file(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, OPEN_EXISTING, 0);
    if (h == INVALID_HANDLE_VALUE) { send_err(c, GetLastError(), path); return; }
    size.QuadPart = GetFileInformationByHandleEx(h, FileStandardInfo, &si, sizeof si) ? si.EndOfFile.QuadPart : 0;
    pos.QuadPart = (LONGLONG)offset;
    SetFilePointerEx(h, pos, NULL, FILE_BEGIN);
    data = (BYTE *)malloc(count ? count : 1);
    if (!ReadFile(h, data, count, &got, NULL)) { DWORD e = GetLastError(); free(data); CloseHandle(h); send_err(c, e, path); return; }
    CloseHandle(h);
    sendf(c, "OK\t%lu\t%llu\n", (unsigned long)got, (unsigned long long)size.QuadPart);
    send_all(c, data, (int)got);
    free(data);
}

static int recv_exact(Conn *c, BYTE *dst, unsigned long n)
{
    /* bytes already buffered after the request line come first */
    unsigned long take = (unsigned long)c->inLen < n ? (unsigned long)c->inLen : n;
    memcpy(dst, c->in, take);
    memmove(c->in, c->in + take, c->inLen - take);
    c->inLen -= take;
    while (take < n) {
        int r = recv(c->s, (char *)dst + take, (int)(n - take), 0);
        if (r <= 0) return 0;
        take += r;
    }
    return 1;
}

/* WRITE path size: replaces the whole file. Goes through a temp file next to it so a failure
   never leaves half a file; keeps the old attributes. */
static int cmd_write(Conn *c, const WCHAR *path, unsigned long long size)
{
    WCHAR *tmp = (WCHAR *)malloc((wcslen(path) + 16) * sizeof(WCHAR));
    HANDLE h;
    WIN32_FILE_ATTRIBUTE_DATA a;
    int had = get_attr(path, &a);
    unsigned long long left = size;
    DWORD err = 0;
    wcscpy(tmp, path);
    wcscat(tmp, L".rfxtmp");
    h = open_file(tmp, GENERIC_WRITE, 0, CREATE_ALWAYS, 0);
    if (h == INVALID_HANDLE_VALUE) err = GetLastError();
    while (left > 0) {
        unsigned long n = left > IO_CHUNK ? IO_CHUNK : (unsigned long)left;
        DWORD put;
        if (!recv_exact(c, c->buf, n)) { if (h != INVALID_HANDLE_VALUE) { CloseHandle(h); DeleteFileW(tmp); } free(tmp); return 0; }
        if (h != INVALID_HANDLE_VALUE && !err && (!WriteFile(h, c->buf, n, &put, NULL) || put != n)) err = GetLastError();
        left -= n;
    }
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (!err) {
        if (had) make_writable(path, a.dwFileAttributes);
        if (!MoveFileExW(tmp, path, MOVEFILE_REPLACE_EXISTING)) err = GetLastError();
        else if (had) set_attrs(path, a.dwFileAttributes);
    }
    if (err) { DeleteFileW(tmp); send_err(c, err, path); }
    else send_str(c, "OK\n");
    free(tmp);
    return 1;
}

static void cmd_newfile(Conn *c, const WCHAR *path)
{
    HANDLE h = open_file(path, GENERIC_WRITE, 0, CREATE_NEW, 0);
    if (h == INVALID_HANDLE_VALUE) { send_err(c, GetLastError(), path); return; }
    CloseHandle(h);
    send_str(c, "OK\n");
}

/* ---------- web access (off until the app turns it on) ---------- */

#include "webpage.h"
#include "web.c"

/* ---------- connection loop ---------- */

/* Reads one line (without \n) into out. Returns 0 on close. */
static int read_line(Conn *c, char *out, int max)
{
    for (;;) {
        char *nl = (char *)memchr(c->in, '\n', c->inLen);
        if (nl) {
            int len = (int)(nl - c->in);
            int copy = len < max - 1 ? len : max - 1;
            memcpy(out, c->in, copy);
            out[copy] = 0;
            if (copy && out[copy - 1] == '\r') out[copy - 1] = 0;
            memmove(c->in, nl + 1, c->inLen - len - 1);
            c->inLen -= len + 1;
            return 1;
        }
        if (c->inLen >= (int)sizeof c->in) return 0;   /* line too long */
        {
            int r = recv(c->s, c->in + c->inLen, (int)sizeof c->in - c->inLen, 0);
            if (r <= 0) return 0;
            c->inLen += r;
        }
    }
}

static int split(char *line, char **f, int max)
{
    int n = 0;
    f[n++] = line;
    while (*line && n < max) {
        if (*line == '\t') { *line = 0; f[n++] = line + 1; }
        line++;
    }
    return n;
}

static int token_ok(const char *t)
{
    size_t a = strlen(t), b = strlen(gToken), i;
    unsigned diff = (unsigned)(a ^ b);
    for (i = 0; i < a && i < b; i++) diff |= (unsigned)(t[i] ^ gToken[i]);
    return diff == 0 && b > 0;
}

static unsigned __stdcall serve(void *arg)
{
    Conn *c = (Conn *)calloc(1, sizeof(Conn));
    char line[LINE_MAX_BYTES];
    char *f[8];
    int authed = 0;
    c->s = (SOCKET)(UINT_PTR)arg;
    c->buf = (BYTE *)malloc(IO_CHUNK);

    while (!gQuit && read_line(c, line, sizeof line)) {
        int n = split(line, f, 8);
        WCHAR *p1 = NULL, *p2 = NULL;
        const char *cmd = f[0];
        c->bytes = c->files = 0;
        c->cancelled = 0;
        c->lastTick = GetTickCount();

        if (!authed) {
            if (!strcmp(cmd, "AUTH") && n >= 2 && token_ok(f[1])) {
                authed = 1;
                sendf(c, "OK\trfxfsd\t" RFXFSD_VERSION "\t%lu\n", (unsigned long)GetCurrentProcessId());
                continue;
            }
            send_msg(c, ERROR_ACCESS_DENIED, "bad token");
            break;
        }
        if (n >= 2) p1 = utf8_to_w(f[1]);
        if (n >= 3) p2 = utf8_to_w(f[2]);

        if (!strcmp(cmd, "PING")) sendf(c, "OK\trfxfsd\t" RFXFSD_VERSION "\t%lu\n", (unsigned long)GetCurrentProcessId());
        else if (!strcmp(cmd, "DRIVES")) cmd_drives(c);
        else if (!strcmp(cmd, "LIST") && p1) cmd_list(c, p1);
        else if (!strcmp(cmd, "STAT") && p1) cmd_stat(c, p1);
        else if (!strcmp(cmd, "DU") && p1) cmd_du(c, p1);
        else if (!strcmp(cmd, "MKDIR") && p1) {
            if (CreateDirectoryW(p1, NULL)) send_str(c, "OK\n"); else send_err(c, GetLastError(), p1);
        }
        else if (!strcmp(cmd, "NEWFILE") && p1) cmd_newfile(c, p1);
        else if (!strcmp(cmd, "COPY") && p2) cmd_copy(c, p1, p2, n >= 4 && f[3][0] == '1');
        else if (!strcmp(cmd, "MOVE") && p2) cmd_move(c, p1, p2, n >= 4 && f[3][0] == '1', 0);
        else if (!strcmp(cmd, "RENAME") && p2) cmd_move(c, p1, p2, 0, 1);
        else if (!strcmp(cmd, "DELETE") && p1) cmd_delete(c, p1);
        else if (!strcmp(cmd, "ATTR") && n >= 3) {
            DWORD attrs = strtoul(f[2], NULL, 16);
            if (set_attrs(p1, attrs)) send_str(c, "OK\n");
            else send_err(c, GetLastError(), p1);
        }
        else if (!strcmp(cmd, "READ") && n >= 4) cmd_read(c, p1, _strtoui64(f[2], NULL, 10), strtoul(f[3], NULL, 10));
        else if (!strcmp(cmd, "WRITE") && n >= 3) { if (!cmd_write(c, p1, _strtoui64(f[2], NULL, 10))) { free(p1); free(p2); break; } }
        else if (!strcmp(cmd, "WEB") && n >= 2) cmd_web(c, n, f);
        else if (!strcmp(cmd, "QUIT")) {
            send_str(c, "OK\n");
            gQuit = 1;
            closesocket(gListen);
        }
        else send_msg(c, ERROR_INVALID_FUNCTION, "unknown command or missing arguments");
        free(p1); free(p2);
    }
    closesocket(c->s);
    free(c->buf);
    free(c);
    return 0;
}

/* Started from CMD Injector's telnet shell, the helper would share that session's console and
   job and die with it. So the first instance starts a copy of itself detached from both and
   exits; the copy gets --fg. CreateProcessW and GetCommandLineW are looked up at run time
   because the phone's import libs may lack them. */
typedef BOOL(WINAPI *CreateProcessW_t)(LPCWSTR, LPWSTR, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
                                       BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION);
typedef LPWSTR(WINAPI *GetCommandLineW_t)(void);

static FARPROC find_proc(const char *name)
{
    static const WCHAR *dlls[] = {L"KERNELBASE.DLL", L"KERNEL32LEGACY.DLL", L"KERNEL32.DLL"};
    int i;
    for (i = 0; i < (int)(sizeof dlls / sizeof dlls[0]); i++) {
        HMODULE m = LoadLibraryExW(dlls[i], NULL, 0);
        FARPROC p = m ? GetProcAddress(m, name) : NULL;
        if (p) return p;
    }
    return NULL;
}

/* 1: a detached copy is running, this process should exit. 0: run here. */
static int detach_self(void)
{
    static const DWORD modes[] = {DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB,
                                  DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP, CREATE_NO_WINDOW};
    CreateProcessW_t create = (CreateProcessW_t)(void *)find_proc("CreateProcessW");
    GetCommandLineW_t getcl = (GetCommandLineW_t)(void *)find_proc("GetCommandLineW");
    const WCHAR *cl = getcl ? getcl() : NULL;
    WCHAR exe[MAX_PATH], *line;
    size_t n;
    int i;
    if (!create || !cl || !GetModuleFileNameW(NULL, exe, MAX_PATH)) return 0;
    n = wcslen(cl) + 8;
    line = (WCHAR *)calloc(n, sizeof(WCHAR));
    for (i = 0; i < (int)(sizeof modes / sizeof modes[0]); i++) {
        STARTUPINFOW si;
        PROCESS_INFORMATION pi;
        memset(&si, 0, sizeof si);
        si.cb = sizeof si;
        _snwprintf(line, n, L"%ls --fg", cl);   /* CreateProcessW may write to the buffer */
        line[n - 1] = 0;
        if (create(exe, line, NULL, NULL, FALSE, modes[i], NULL, NULL, &si, &pi)) {
            printf("rfxfsd started (pid %lu)\n", (unsigned long)pi.dwProcessId);
            fflush(stdout);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            free(line);
            return 1;
        }
        printf("rfxfsd: detach mode %d failed (%lu)\n", i, (unsigned long)GetLastError());
    }
    free(line);
    return 0;
}

int main(int argc, char **argv)
{
    WSADATA wsa;
    struct sockaddr_in addr;
    const char *bind_ip = "127.0.0.1";
    int port = 8093, i, one = 1, fg = 0;

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--token") && i + 1 < argc) strncpy(gToken, argv[++i], sizeof gToken - 1);
        else if (!strcmp(argv[i], "--port") && i + 1 < argc) port = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--bind") && i + 1 < argc) bind_ip = argv[++i];
        else if (!strcmp(argv[i], "--fg")) fg = 1;   /* stay in this process (tests, debugging) */
    }
    if (strlen(gToken) < 16) {
        fprintf(stderr, "usage: rfxfsd --token TOKEN(16+ chars) [--port 8093] [--bind 127.0.0.1] [--fg]\n");
        return 2;
    }
    if (!fg && detach_self()) return 0;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    web_init();
    gListen = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    setsockopt(gListen, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char *)&one, sizeof one);
    memset(&addr, 0, sizeof addr);
    addr.sin_family = AF_INET;
    addr.sin_port = htons((u_short)port);
    addr.sin_addr.s_addr = inet_addr(bind_ip);
    if (bind(gListen, (struct sockaddr *)&addr, sizeof addr) || listen(gListen, 8)) {
        fprintf(stderr, "rfxfsd: can't listen on %s:%d (error %d); already running?\n", bind_ip, port, WSAGetLastError());
        return 1;
    }
    printf("rfxfsd %s listening on %s:%d\n", RFXFSD_VERSION, bind_ip, port);
    fflush(stdout);
    while (!gQuit) {
        SOCKET s = accept(gListen, NULL, NULL);
        HANDLE t;
        if (s == INVALID_SOCKET) break;
        setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof one);
        t = (HANDLE)_beginthreadex(NULL, 0, serve, (void *)(UINT_PTR)s, 0, NULL);
        if (t) CloseHandle(t); else closesocket(s);
    }
    Sleep(200);   /* let a QUIT reply go out */
    WSACleanup();
    return 0;
}
