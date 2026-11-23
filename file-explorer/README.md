# Root Explorer (RetroFiles)

Shows up on the phone as **Root Explorer**; the project, helper and folders keep the RetroFiles name.

A root file manager for Windows Phone 8.1 (made for an interop-unlocked Lumia 820 with CMD Injector).

- Browse the whole phone: C:\ (MainOS), C:\Data, C:\EFIESP, the SD card, app folders.
- Copy / cut / paste (also across drives), delete, rename, new folder, new text file.
- Properties: size (folder sizes too), dates, read-only / hidden / system / archive toggles.
- Viewer: text (edit line by line and save, keeps encoding and line endings), hex, images.
- Tapping a file opens text and images in the built-in viewer and hands everything else (music,
  video, PDF, Office...) to the phone's app for that type, falling back to the viewer. "Open with..."
  and "view as text/hex" in the select menu force either one.
- **Web access** (globe button): manage files from a PC browser on the same Wi-Fi: browse, upload
  (button or drag and drop), download, new folder, rename, copy/move, delete. Password protected;
  the app shows the address and password. Stays on until you turn it off or the phone restarts.
- Bookmarks, type-a-path, sort by name/size/date, show or hide hidden/system items.
- Warnings before changing system areas; the helper refuses to delete, move or rename drive
  roots and the top system folders.

## How it works

The app runs in the normal app sandbox, so it can't touch most of the phone itself. It starts
**rfxfsd**, a small native ARM helper, through CMD Injector's SYSTEM shell on 127.0.0.1:9999
(the same channel CMD Injector's own app uses), and then does all file work through it:

1. On launch the app connects to 127.0.0.1:8093 with its key (random, made on first run).
2. If nothing answers, it writes `rfxstart.cmd` to its own folder and types only that path into
   CMD Injector's shell. The script copies `Helper\rfxfsd.exe` from the package to
   `C:\Data\SharedData\RetroFiles\` and runs it there with the key; its output goes to
   `rfxstart.log`, which the app shows if the helper still doesn't answer.
   rfxfsd starts a detached copy of itself (`--fg` skips that) so it outlives the shell session.
3. The helper only listens on loopback and only serves clients that send the key.

Nothing runs at boot and no PC is involved. The helper keeps running until the phone restarts.

## Building (Windows)

`.\build.ps1` builds rfxfsd with the retrofox ARM toolchain and the app with VS 2015 (MSBuild 14),
then installs it on the USB-connected, unlocked phone. `-NoDeploy` builds only.

## Testing the helper without a phone

`daemon\test\test_rfxfsd.py` runs 45 protocol and web checks against any running rfxfsd (it was
developed against a Windows build under Wine).

See [PROTOCOL.md](PROTOCOL.md).
