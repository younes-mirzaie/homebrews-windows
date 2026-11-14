# wp-projects

Homebrew apps and tools for Windows Phone 8.1, made for an interop-unlocked Lumia 820 with
CMD Injector. Everything runs on the phone itself. The Gecko browser port lives in its own repo,
[retrofox](https://github.com/younes-mirzaie/retrofox).

| folder | what |
|---|---|
| [file-explorer](file-explorer) | **File Explorer**: root file manager (whole filesystem as SYSTEM, copy/move/delete, attributes, text/hex/image viewer, web access over Wi-Fi) |
| [design/metro-ui-design](design/metro-ui-design) | **Metro UI design**: the design skill all apps follow, plus the sourced research on the WP 7, 8 and 8.1 design docs |

## Building

Apps build with Visual Studio 2015 and the Windows Phone 8.1 SDK. Native ARM helpers build with
the retrofox toolchain (clang-cl + the phone SDK under `C:\rfx\sdk`). Each folder has its own
README and build script.
