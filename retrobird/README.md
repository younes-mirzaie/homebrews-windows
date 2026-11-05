# Retrobird

A Thunderbird-style mail client for Windows Phone 8.1 (made for an interop-unlocked Lumia 820
with CMD Injector). Works with any IMAP/SMTP mail service, runs entirely on the phone, modern TLS.

- Several accounts; settings filled in from the address for common providers (Gmail, Yahoo,
  iCloud, GMX, WEB.DE, Fastmail, Yandex, Zoho) or guessed, editable by hand.
- TLS 1.2/1.3 (SSL/TLS or STARTTLS) through NSS, the same library as the Retrofox browser.
  A self-signed certificate can be trusted by its exact SHA-256 fingerprint.
- Folder list with unread counts, offline cache of headers and opened messages, "load older".
- Reader: sanitized HTML (no scripts, remote images blocked until you allow them), plain text
  view, inline images, attachments open in the phone's app for their type.
- Reply, reply all, forward, compose; sent mail saved to Sent; drafts open in compose.
- Flag, mark unread, move, delete (to Trash), background check for new mail every N minutes,
  unread count on the live tile.

Not yet: OAuth2 sign-in (Outlook.com/Hotmail need it, so they won't work; Gmail needs an app
password), sending attachments, push (IDLE), search, signatures.

## How it works

The app runs in the normal sandbox, where WP 8.1 has no modern TLS. It starts **rbmaild**, a
native ARM helper (C on NSPR/NSS), through CMD Injector's SYSTEM shell, the same way File
Explorer starts its helper:

1. The app connects to 127.0.0.1:8096 with its key (random, made on first run).
2. If nothing answers, it copies `Helper\*` from its package to `C:\Data\SharedData\Retrobird\`
   and starts `rbmaild.exe` there. A helper left from an older version is told to quit first.
3. The helper only listens on loopback and only serves clients that send the key. It does all
   IMAP/SMTP/MIME work, keeps the mail cache and checks INBOXes in the background.

Passwords are stored encrypted (AES-256-GCM) with a key derived from the app's key, so the
helper's data folder alone does not reveal them. No PC or server is involved.

See [PROTOCOL.md](PROTOCOL.md).

## Layout

| path | what |
|---|---|
| `helper/` | rbmaild: `imap.c`, `smtp.c`, `mime.c`, `codec.c` (charsets, HTML sanitizer), `net.c` (NSS TLS, pinning, secrets), `store.c` (cache), `server.c` |
| `helper/test/` | `mimetest.c` (62 unit checks), `test_rbmaild.py` (82 checks against Dovecot and a fake SMTP server), `run-tests.sh` |
| `app/Retrobird/` | Silverlight 8.1 app; `Core/` holds the protocol client and models, testable on Mono |
| `app/tests/ClientTest.cs` | 20 checks of the C# client against a live helper |
| `app/tools/make-icons.py` | generates app bar icons, tiles and the splash screen |

## Building (Windows)

`.\build.ps1` builds rbmaild with the retrofox ARM toolchain and NSS build (`C:\rfx\obj-nss`,
see retrofox notes/step2-nss.md), copies it with `nssckbi.dll` and the NSPR DLLs into
`app\Retrobird\Helper`, builds the app with VS 2015 (MSBuild 14) and installs it on the
USB-connected phone. `-NoDeploy` builds only; the .xap can also go through the rfxdev portal.

## Testing without a phone (Linux)

Needs `libnss3-dev`, `dovecot-imapd`, `python3`, and `mono-mcs` for the C# test:

    cd helper && make && test/run-tests.sh      # KEEP=1 keeps the temp folder

`make asan` builds with AddressSanitizer/UBSan; the suite runs clean under it.
