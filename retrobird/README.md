# Retrobird

A Thunderbird-style mail client for Windows Phone 8.1 (made on a Lumia 820). Works with any
IMAP/SMTP mail service, runs entirely inside the app, modern TLS.

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
- Outlook.com / Hotmail: Microsoft sign-in with a short code (OAuth 2.0 device code flow) entered
  at microsoft.com/link in Retrofox or any browser; XOAUTH2 for IMAP and SMTP. Needs a Microsoft
  app (client) ID: built in as `Provider.MicrosoftClientId`, or typed under server settings.

Not yet: Google sign-in (Gmail uses an app password), sending attachments, push (IDLE), search, signatures.

## How it works

The mail engine is C code on NSPR/NSS (WP 8.1's own networking has no modern TLS). It is built
as **rbmail.dll** and packaged with the app. The app loads it and runs `RetrobirdMain` on a thread of
its own, the way Retrofox runs Gecko inside its app. The app talks to it over loopback
(127.0.0.1:8097) inside its own process, with a random key. Mail cache, settings and the log
live in the app's own folder (`LocalState\data`). No CMD Injector, no SYSTEM process, nothing
that outlives the app. The only network traffic is to your mail servers.

Passwords are stored encrypted (AES-256-GCM) with a key derived from the app's key, which is kept
in the app's settings. New mail is checked every N minutes while the app is open. While it's
closed, a background task (`app/RetrobirdTasks`, a Windows timer about every 30 minutes, the
shortest the phone allows) calls `RetrobirdCheck` in the same rbmail.dll for one pass over every
inbox, then updates the live tile (unread count, newest senders and subjects) and shows a
notification for new mail. No push server is involved.

See [PROTOCOL.md](PROTOCOL.md).

## Layout

| path | what |
|---|---|
| `helper/` | rbmaild: `imap.c`, `smtp.c`, `mime.c`, `codec.c` (charsets, HTML sanitizer), `net.c` (NSS TLS, pinning, secrets), `store.c` (cache), `server.c` |
| `helper/test/` | `mimetest.c` (62 unit checks), `test_rbmaild.py` (82 checks against Dovecot and a fake SMTP server), `run-tests.sh` |
| `app/Retrobird/` | Windows Phone 8.1 app (WinRT XAML, like Retrofox: Silverlight apps can't load native DLLs); `Core/` holds the protocol client and models, testable on Mono |
| `app/tests/ClientTest.cs` | 20 checks of the C# client against a live helper; `InProcessTest.cs` runs them with the helper loaded as a library in the same process |
| `app/art/`, `app/tools/render-art.js` | Younes's Retrobird art (SVG) and the script that renders the tile, icon, store logo and splash PNGs; `make-icons.py` draws the app bar icons |

## Building (Windows)

`.\build.ps1` builds rbmail.dll with the retrofox ARM toolchain and NSS build (`C:\rfx\obj-nss`,
see retrofox notes/step2-nss.md), copies it with `nssckbi.dll` and the NSPR DLLs into
`app\Retrobird\Helper`, builds the app with VS 2015 (MSBuild 14) and installs it on the
USB-connected phone. `-NoDeploy` builds only; the .appx can also go through the rfxdev portal or Unesbrews.

## Testing without a phone (Linux)

Needs `libnss3-dev`, `dovecot-imapd`, `python3`, and `mono-mcs` for the C# test:

    cd helper && make && test/run-tests.sh      # KEEP=1 keeps the temp folder

`make asan` builds with AddressSanitizer/UBSan; the suite runs clean under it.
