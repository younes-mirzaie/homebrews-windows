# rfxfsd protocol (v1)

TCP on 127.0.0.1:8093 (loopback only). One request per line, fields separated by TAB, UTF-8,
ending in `\n`. Windows paths can't contain TAB or newline, so no escaping is needed.

The first line must be `AUTH\t<token>`; the token is the one rfxfsd was started with
(`--token`). A wrong token gets `ERR` and the connection is closed.

Replies: `OK[\tfield...]` or `ERR\t<win32 code>\t<message>`.
List replies are `OK` followed by one row per line and an empty line.
Long operations (COPY, MOVE, DELETE, DU) may send `P\t<bytes>\t<items>\t<current path>` lines
(at most 4 a second) before the final reply. Sending any byte while one runs cancels it
(`ERR\t1223\tcanceled...`).

| request | reply |
|---|---|
| `AUTH token` / `PING` | `OK rfxfsd <version> <pid>` |
| `DRIVES` | rows: `root label filesystem totalBytes freeBytes` (drives, plus mount points C:\Data, C:\EFIESP, ...) |
| `LIST dir` | rows: `name attrsHex size mtime ctime` (times are FILETIME UTC as decimal) |
| `STAT path` | `OK attrsHex size ctime atime mtime` |
| `DU dir` | `OK bytes files folders` (junctions not followed) |
| `MKDIR path`, `NEWFILE path` | `OK` |
| `COPY src dst [1=overwrite]` | recursive; keeps times and attributes; skips junctions inside the tree |
| `MOVE src dst [1=overwrite]` | rename, or copy + delete across volumes |
| `RENAME src dst` | same volume only |
| `DELETE path` | recursive, clears read-only; removes junctions as links; refuses drive roots, C:\Windows, C:\Windows\System32, C:\Data, C:\EFIESP |
| `ATTR path attrsHex` | sets read-only/hidden/system/archive/... |
| `READ path offset count` | `OK n fileSize` then n raw bytes (max 4 MB per call) |
| `WRITE path size` + size raw bytes | replaces the file via `path.rfxtmp`, keeps attributes |
| `WEB on port password` / `WEB off` / `WEB status` | `OK on\|off port phoneIP`; see "Web access" |
| `QUIT` | `OK`, then the helper exits |

## Web access

`WEB on 8095 PASSWORD` starts an HTTP server on all interfaces (Wi-Fi) until `WEB off` or the
helper exits. It serves a file manager page at `/` and a small JSON API:
`GET /api/drives`, `GET /api/list?p=`, `GET /dl?p=` (download),
`POST /api/upload?p=FOLDER&n=NAME` (body = file), `POST /api/mkdir?p=`, `POST /api/delete?p=`,
`POST /api/rename?p=&to=`, `POST /api/copy?p=&to=`, `POST /api/move?p=&to=`.
Every request needs HTTP Basic auth with the password (any user name); POSTs also need the
header `X-RFX: 1`, so other web pages can't drive it through the browser. Plain HTTP, for a home
network. The page lives in `daemon/webpage.html`; run `daemon/gen-webpage.py` after editing it.
