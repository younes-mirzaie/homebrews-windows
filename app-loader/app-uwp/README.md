# Unesbrews for Windows 10 Mobile

The Windows 10 version of Unesbrews: a native UWP app with the same functions as the Windows Phone
8.1 app (Catalog, Installed with its updates, Sideload, Settings with notifications and automatic
updates, the activity log; see ../README.md), and a UI built to the Windows 10 (MDL2) design
guidelines:

- a navigation pane behind the hamburger button (overlay on phones, a 48 px rail from 641 px, docked
  open from 1008 px, so it also works on a desktop or with Continuum);
- a 48 px top bar with the page title, a progress row while something runs;
- command bars at the bottom on phones and at the top on wider windows;
- content dialogs with verb buttons (Install, Update, Cancel), plain-language results;
- the phone's light or dark theme and accent color, Segoe MDL2 Assets icons, standard page
  transitions (entrance for top-level pages, drill-in for an app's details);
- Back closes the pane, then goes back, then leaves the app.

## Shared core

The logic is not copied. `Unesbrews\Unesbrews.csproj` links the 8.1 app's core
(`..\app\AppLoader\Core`) and the catalog client (`..\catalog\client`), so a fix there reaches both
apps. What differs on Windows 10 is small:

| | Windows Phone 8.1 | Windows 10 Mobile |
|---|---|---|
| Installer | `TH.exe <pkg> -uda` / `-di` | `MinDeployAppX.exe /Update` / `/Add /PackagePath:<pkg> /DeploymentOption:32768` |
| Installed apps | TH's app list | the installed manifests, plus Windows' package list for names and icons |
| Files and settings | Silverlight isolated storage | the app's local folder (`Compat\IsolatedStorage.cs` gives the shared code the same calls) |
| Background | a scheduled agent | an extended execution session while an install runs, and a timer task (every 4 hours, in-process) for update checks |
| Reaching the shell | always allowed | needs a loopback exemption (below) |

Those places are marked `#if WINDOWS_UWP` in the shared files, or live in `Unesbrews\Core10`.
The 8.1 build doesn't define that symbol, so it compiles exactly as before.

It is a separate app from the 8.1 version (its own product ID and package name). Installing it
doesn't touch the 8.1 Unesbrews if that is on the phone too.

## Install by hand

You need a Windows 10 Mobile phone unlocked with CMD Injector.

1. Copy `release\Unesbrews_<version>_ARM.appx` and the files in `release\Dependencies` to the phone
   (Downloads is fine).
2. If the app doesn't install, or doesn't start, install the files from `Dependencies` first
   (most phones already have them from Store apps).
3. In CMD Injector, open the app manager, install the .appx with the "skip signature check"
   deployment option. The package is signed with a self-signed certificate (`release\Unesbrews.cer`),
   which phones don't trust by default, so the check has to be skipped (or the certificate trusted).
4. Still in CMD Injector's app manager: pick Unesbrews and tap **Enable Loopback**. Windows 10 apps
   may not connect to the phone itself until they are allowed to, and Unesbrews works through the
   phone's system shell. This is needed once; after that, Unesbrews keeps the exemption when it
   updates itself.

The same from a shell on the phone:

```
MinDeployAppX.exe /Add /PackagePath:"C:\Data\Users\Public\Downloads\Unesbrews_0.3.0.0_ARM.appx" /DeploymentOption:32768
CheckNetIsolation.exe loopbackexempt -a -n=Unesbrews_1mwg862xgfggy
```

## Build

`..\build-uwp.ps1` (Visual Studio 2017 or later, or its Build Tools, with the Universal Windows
Platform build tools) and the Windows 10 SDK 10.0.17763.0. It writes the package, its public
certificate and any framework packages to `release\`.

Why SDK 17763: the app's minimum version is 15063 (Windows 10 Mobile), which ties it to .NET
Native 1.7, and that compiler crashes with newer SDKs.

Signing: Windows 10 Mobile refuses a package with no signature at all (0x800B0100), even with
signature checks skipped. The script signs with a self-signed `CN=Unesbrews` certificate whose
private key stays outside the repository (`C:\rfx\keys\unesbrews-signing.pfx` by default, made on
the first run). Never commit the .pfx.
