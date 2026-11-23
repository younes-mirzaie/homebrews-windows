# Retrobird for Windows 10

The Windows 10 version of Retrobird: a native UWP app for Windows 10 Mobile (and the PC) with the
same functions as the Windows Phone 8.1 app (see ../README.md), and a UI built to the Windows 10
(MDL2) design guidelines:

- a navigation pane behind the hamburger button with new mail, every inbox (all inboxes first),
  the current account's folders with unread counts, and settings at the bottom. It is an overlay
  on phones, a 48 px rail from 641 px, and docked open from 1008 px;
- the inbox as a pivot of all, unread and flagged mail grouped by day ("Today", "Yesterday"...),
  unread mail with the sender in Semibold and the subject in the accent color;
- master/details from 720 px: the open message sits beside the list, and below 720 px it opens
  as its own page;
- a 48 px top bar with the page title, and a busy line under it while something runs;
- command bars at the bottom on phones and at the top on wider windows;
- content dialogs, the user's light or dark theme and accent color, Segoe MDL2 Assets icons;
- Back closes the pane, then the open message, then goes back, then leaves the app;
- live tiles in small, medium, wide and large, with the unread count and the newest mail, and
  account inboxes that can be pinned to Start.

## Shared code

Nothing is copied. `Retrobird10\Retrobird10.csproj` links the 8.1 app's core
(`..\app\Retrobird\Core`: the mail client, the in-app engine, the inbox, tiles and notifications,
replies, the message page HTML) and the code behind most pages (message, compose, settings,
account, add an account, licenses). Windows 10 has its own XAML for each page, its own shell and
inbox page, and `Retrobird10\Core10` for the few calls that differ:

| | Windows Phone 8.1 | Windows 10 |
|---|---|---|
| Page base (`RbPage`) | phone back key, status bar progress | shell frame, master/details, top bar title, busy line |
| `Ui` helpers | page app bar, list picker, message boxes, lowercase labels | page command bar, menus, content dialogs, sentence case |
| Background check | `RetrobirdTasks` (a separate task that runs the engine once) | in the app's process (`Core10\BackgroundCheck.cs`), using the same engine |
| Live tiles | 8.1 icon templates | adaptive tiles (`#if WINDOWS_UWP` in `Core\Notify.cs`) |

Other differences are marked `#if WINDOWS_UWP` in the shared files. The 8.1 build doesn't define
that symbol, so it compiles exactly as before.

The mail engine is the 8.1 app's: `rbmail.dll` and Mozilla's NSPR and NSS DLLs (ARM), built by
`..\build.ps1`. So the Windows 10 package is ARM only, for phones.

It is a separate app from the 8.1 version (package name `Retrobird10`, its own product ID), so
both can be on one phone. They don't share accounts or mail.

## Install by hand

You need a Windows 10 Mobile phone unlocked with CMD Injector.

1. Copy `release\Retrobird10_<version>_ARM.appx` and the files in `release\Dependencies` to the
   phone (Downloads is fine).
2. If the app doesn't install, or doesn't start, install the files from `Dependencies` first
   (most phones already have them from Store apps).
3. In CMD Injector's app manager, install the .appx with the "skip signature check" deployment
   option. The package is signed with a self-signed certificate (`release\Retrobird10.cer`),
   which phones don't trust by default.

The same from a shell on the phone:

```
MinDeployAppX.exe /Add /PackagePath:"C:\Data\Users\Public\Downloads\Retrobird10_0.7.0.0_ARM.appx" /DeploymentOption:32768
```

Retrobird talks to its engine inside its own process, which Windows allows without a loopback
exemption. If it ever says the mail engine didn't answer, turning on **Enable Loopback** for
Retrobird in CMD Injector's app manager is the thing to try.

## Build

`..\build-uwp.ps1` (Visual Studio 2017 or later, or its Build Tools, with the Universal Windows
Platform build tools) and the Windows 10 SDK 10.0.17763.0. It builds the 8.1 engine first if it
isn't there, then writes the package, its public certificate and any framework packages to
`release\`.

Why SDK 17763: the app's minimum version is 15063 (Windows 10 Mobile), which ties it to .NET
Native 1.7, and that compiler crashes with newer SDKs.

Signing: Windows 10 Mobile refuses a package with no signature at all (0x800B0100), even with
signature checks skipped. The script signs with a self-signed `CN=Retrobird` certificate whose
private key stays outside the repository (`C:\rfx\keys\retrobird-signing.pfx` by default, made on
the first run). Never commit the .pfx.

Tile and icon art: `node ..\app\tools\render-art-uwp.js` renders `Retrobird10\Assets` from the
same drawings as the phone app (`..\app\art`).
