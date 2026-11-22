# wp-projects

Homebrew apps and tools for Windows Phone 8.1, made for an interop-unlocked Lumia 820 with
CMD Injector. Everything runs on the phone itself. The Gecko browser port lives in its own repo,
[retrofox-src](https://github.com/younes-mirzaie/retrofox-src).

| folder | what |
|---|---|
| [file-explorer](file-explorer) | **File Explorer**: root file manager (whole filesystem as SYSTEM, copy/move/delete, attributes, text/hex/image viewer, web access over Wi-Fi) |
| [design/metro-ui-design](design/metro-ui-design) | **Metro UI design**: the design skill all apps follow, plus the sourced research on the WP 7, 8 and 8.1 design docs |
| [design/windows10-uwp-design](design/windows10-uwp-design) | **Windows 10 UWP design**: the separate design skill for Windows 10 and Windows 10 Mobile apps (MDL2 and Fluent), plus its sourced research |

## Building

Apps build with Visual Studio 2015 and the Windows Phone 8.1 SDK. Native ARM helpers build with
the retrofox toolchain (clang-cl + the phone SDK under `C:\rfx\sdk`). Each folder has its own
README and build script.

## License

Everything in this repo is licensed under the [Mozilla Public License 2.0](LICENSE) unless a
folder says otherwise. Folders that come from other projects keep their own license and notices:
RetroTel is a fork of Amigram and stays under the MIT license (see `retrotel/LICENSE`).
Apps that ship Mozilla's NSS and NSPR libraries (also MPL-2.0) list them in their notices.

In short: you can use, change and redistribute this code, also inside other projects. If you
change one of these files and distribute the result, publish your changed version of that file
under MPL-2.0 too.
