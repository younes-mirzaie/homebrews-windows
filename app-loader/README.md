# App Loader

Installs and updates apps on an interop-unlocked Windows Phone 8.1 from the phone itself, without
a PC and without the CMD Injector app.

It runs the phone's own installer (`TH.exe`) as SYSTEM through the system shell that CMD Injector
sets up (telnet on 127.0.0.1:9999 or 23). That shell is a system service and keeps working even
if the CMD Injector app is gone, so App Loader can also put CMD Injector back.

## Stage 1 (this)

- **packages**: the .xap/.appx files in Downloads and Documents, on the phone and the SD card.
  Tap one to install it.
- **installed**: the installed apps, where they live (phone or SD card) and their product IDs.
- **log**: what the installer said last time.

How an install goes (`Core/Phone.cs`):

1. The package is copied into the app's own folder under a plain name (ASCII, no spaces), and
   everything after works on that copy.
2. Its manifest is read (`Core/PackageInfo.cs`, `Core/Zip.cs`: product ID, name, version,
   whether it refuses the SD card).
3. If the app is installed, it gets an in-place update (`TH.exe <pkg> -uda`) and keeps its data;
   if not, a deploy (`-di`).
4. Refused up front: a package that refuses the SD card, over an installed copy that lives on the
   SD card. The phone refuses that update and the app can be lost (this is how CMD Injector
   disappeared on 9 Nov 2026). The user moves the app to the phone first.
5. It never uninstalls anything, not even as a fallback.
6. TH runs detached from the app: `Helper\aldetach.exe` (`helper/aldetach.c`) starts the install
   batch outside the telnet session's console and job, and the batch reports through
   `run-N.log` and `run-N.done` in the app's folder. The install is remembered, so if the screen
   locks, the user leaves the app, or App Loader updates itself, it carries on and the result is
   shown when the app is opened again. The screen is kept on during an install, and one install
   runs at a time.
7. Catalogue installs (stage 2) can pass an expected product ID and SHA-256, checked on the
   staged copy before TH runs.

`Core/PackageInstaller.cs` is the install step alone: a local package path and a mode in, TH.exe's
output and exit status out (or a run name to read later). `Core/Shell.cs` runs batch commands as SYSTEM. The app doesn't read
or keep any secrets (no rfxdev token).

## Stage 2: online catalogue

- **catalogue**: apps from the signed catalogue (public repo `wp-homebrew`), with what's installed.
  Tap one for its details page: description, license, versions, install / update / reinstall.
- **updates**: catalogue apps with a newer version than the installed one.

How it works (`Core/CatalogService.cs`, design in [catalog/DESIGN.md](catalog/DESIGN.md)):

1. `Helper\rfxfetch.exe` ([fetch/](fetch), NSS) downloads `index.json` and its signature; works on
   Wi-Fi and mobile data. The index is checked with the Ed25519 key built into the app
   (`CatalogConfig.PublicKey`, [catalog/client](catalog/client)) and must not be older than the
   newest one this phone accepted. The last good index is kept for offline use.
2. Installed versions are read from the installed manifests (`C:\Data\Programs\{id}\Install`,
   `D:\WPSystem\Apps\{id}\Install`); where they aren't found the version shows as unknown.
3. A package is downloaded by rfxfetch in the background (detached, like installs), pinned by
   the index's SHA-256 and size, then installed by stage 1 with the expected product ID and
   SHA-256, checked again on the staged copy.

To publish: `catalog/tools/catalog.py` (`keygen` once, then `add` and `build`), see the
`wp-homebrew` README. Until `CatalogConfig.PublicKey` is filled in, the catalogue panes say the
catalogue isn't set up.

## Build

`.\build.ps1` (VS 2015 / MSBuild 14, Windows Phone 8.1 SDK). The first install needs the PC
(`rfx install <xap> -Mode di`) or a shell on the phone; after that it can update itself.
