# rbmaild protocol

The app talks to `rbmaild` over TCP on `127.0.0.1:8096` (loopback only). One request per line,
fields separated by TAB, lines end with `\n`. Inside a field, `\\`, `\t`, `\n` and `\r` are escaped.

Replies start with `OK` or `ERR<TAB>code<TAB>message`. Replies that carry rows (lists, messages)
end with an empty line. Error codes:

| code | meaning |
|---|---|
| `AUTH` | the mail server refused the login (or a bad app token: the connection then closes) |
| `NET` | could not reach the server, timeout, TLS failure |
| `CERT` | untrusted certificate: `ERR CERT text fingerprint imap\|smtp`. Retry `SAVE` with `imap_pin`/`smtp_pin` = fingerprint to trust it |
| `SERVER` | the server answered with an error |
| `ARG`, `NOTFOUND` | bad request |

## Commands

| request | reply |
|---|---|
| `AUTH token` | first line on every connection. `OK rbmaild version pid` |
| `PING` | `OK rbmaild version pid` |
| `ACCOUNTS` | `OK`, rows `A id name email unseen lastsync lasterror haspass` |
| `GET id` | `OK`, rows `key value` (all settings except the password; `has_pass` 1/0) |
| `SAVE id\|"" key=value ...` | logs in to IMAP and SMTP first (`check=0` skips that), then `OK id`. Keys: `name email user pass imap_host imap_port imap_sec smtp_host smtp_port smtp_sec imap_pin smtp_pin save_sent`. `*_sec` is `tls`, `starttls` or `plain`. An empty `pass` when editing keeps the old one |
| `DELACCT id` | `OK` (removes the account and its cache) |
| `FOLDERS id` | `OK online` or `OK offline error`, then rows `F name display role unseen total noselect` from the cache. Roles: `inbox sent drafts trash junk archive` or empty |
| `SYNC id folder` | `OK added unseen` (new mail, flag changes, deletions) |
| `MORE id folder count` | `OK added unseen` (fetches `count` older headers) |
| `LIST id folder offset count` | `OK cached exists`, rows `M uid flags date size att from to subject`, newest first. Flags letters: `S` seen, `F` flagged, `A` answered, `D` draft |
| `MSG id folder uid [images]` | `OK`, rows `key value` for `from to cc replyto subject date msgid references blocked text html`, and `att idx name ctype size inline`. Marks the message read. `html` is sanitized; remote images are blocked unless `images` is `1` |
| `SAVEATT id folder uid idx dir` | `OK path` |
| `FLAG id folder uid +S\|-S\|+F\|-F\|+A\|-A` | `OK` |
| `DELETE id folder uid` | `OK trash` or `OK expunged` |
| `MOVE id folder uid dest` | `OK moved` |
| `SEND id` + lines `key value` + empty line | `OK [warning]`. Keys: `to cc bcc subject body in_reply_to references reply_folder reply_uid`. Copies to Sent when `save_sent` is on, marks the original answered |
| `SETTINGS sync_minutes N` | `OK` (0 = off, minimum 5) |
| `QUIT` | stops the helper |

Dates are Unix seconds (UTC). Folder names are the server's raw (mUTF-7) names; `display` is decoded.

## Files

Under the data folder (`C:\Data\SharedData\Retrobird\data` on the phone):

- `accounts.conf`: settings, passwords sealed with AES-256-GCM under a key derived from the app token
- `mail/<account>/folders`: folder cache
- `mail/<account>/<hex folder name>/index`: header cache; `<uid>.eml`: downloaded messages
- `rbmaild.log`
