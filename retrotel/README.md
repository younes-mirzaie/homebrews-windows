# RetroTel

Telegram client for Windows Phone 8.1 (Silverlight 8.1, ARM). It talks to Telegram's servers
directly over MTProto 2.0 (API layer 165), so it needs no relay server or PC.

Based on [Amigram](https://github.com/t4llfly/Amigram) by tallfly (MIT, commit `93dc6e1`,
2026-09-01). Upstream's README is kept as [UPSTREAM-README.md](UPSTREAM-README.md); see
[LICENSE](LICENSE).

## What works (from upstream, not yet tested on the Lumia 820)

- Sign-in with phone number, code and two-step verification password (SRP)
- Chats list with pinned chats and unread counts, contacts, profiles, channel members
- Text messages, replies, forward, copy, delete for me / everyone
- Photos: view (pinch zoom, save to phone) and send from the camera roll
- Live tile with unread count

## Changes from Amigram

- Renamed to RetroTel, new app IDs (installs next to Amigram)
- Crash log: unhandled exceptions go to `crash.txt` in the app's isolated storage and are shown
  once on the next launch (`Services/CrashLog.cs`)
- UI text in US English; Telegram error codes shown as plain sentences (`Services/FriendlyErrors.cs`)
- Login key sealed with the phone's data protection API instead of plain isolated storage
  (`Crypto/SecureStore.cs`); old plain keys are migrated on first start
- Metro pass: follows the phone's dark/light theme and accent, system Segoe WP fonts and type
  ramp, no rounded corners or iOS colors, lowercase group headers
- Telegram API key kept out of git (see below); phone and login code no longer written to the debug log

## Telegram API key

Every Telegram client needs its own `api_id` and `api_hash` from my.telegram.org. They are never
committed. `build.ps1` reads them from `TG_API_ID` / `TG_API_HASH`, or from a git-ignored
`retrotel\telegram-api.local`:

```
TG_API_ID=1234567
TG_API_HASH=0123456789abcdef0123456789abcdef
```

and generates `app\MetroTelegram\AppConfig.Secrets.cs` (git-ignored) before building.

## Building

Needs Visual Studio 2015 (Update 3) and the Windows Phone 8.1 SDK.

```
powershell -ExecutionPolicy Bypass -File retrotel\build.ps1
```

It restores NuGet packages, builds Release|ARM and copies `RetroTel_Release_ARM.xap` to
`retrotel\out\`.

GitHub Actions: `.github/workflows/retrotel.yml` (run it by hand) builds on a self-hosted Windows
runner labeled `wp81` and passes the repository secrets `TG_API_ID` / `TG_API_HASH` (also accepts
`TELEGRAM_API_ID` / `API_ID` names). GitHub's own runners don't have the WP 8.1 SDK.

## Not done yet

- Background notifications (planned: 30-minute background check, like Retrobird; real push isn't
  possible for a sideloaded 8.1 app)
- Files, voice notes, stickers, video
- Debug log lines and some code comments are still in Russian
