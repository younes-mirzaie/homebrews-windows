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
6. TH runs detached from the app: `Helperldetach.exe` (`helper/aldetach.c`) starts the install
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

## Stage 2 (planned)

An online catalogue: a list file with each app's name, version, download link and checksum; the
loader shows updates, downloads, checks and installs. Design notes live with the project thread.

## Build

`.\build.ps1` (VS 2015 / MSBuild 14, Windows Phone 8.1 SDK). The first install needs the PC
(`rfx install <xap> -Mode di`) or a shell on the phone; after that it can update itself.
