# Builds rfxfetch.exe (native ARM, NSPR/NSS) for the phone and copies it with nssckbi.dll and the
# NSPR DLLs into ..\app\AppLoader\Helper. Same toolchain and NSS build as Retrobird's rbmaild
# (retrofox notes/step2-nss.md); it never rebuilds NSS, so it is safe next to a Gecko build.
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$Llvm = "C:\Program Files\LLVM\bin"
$Sdk = "C:\rfx\sdk"
$UcrtVer = "10.0.22621.0"
$Rfx = if ($env:RETROFOX_ROOT) { $env:RETROFOX_ROOT } else { "C:\rfx\retrofox" }
$Uxp = if ($env:UXP_ROOT) { $env:UXP_ROOT } else { "C:\rfx\uxp" }
$NsprInc = "C:\rfx\obj-js\dist\include\nspr"
$NsprLib = "C:\rfx\obj-js\config\external\nspr"
$NsprBin = "C:\rfx\obj-js\dist\bin"
$NssObj = "C:\rfx\obj-nss"
$PhoneLib = "$Rfx\build\lib"

foreach ($need in @("$Llvm\clang-cl.exe", "$Sdk\vc143\include", "$PhoneLib\kernel32.lib", "$NsprInc\nspr.h",
                    "$NssObj\targets.json", "$NssObj\lib\ssl.lib", "$NssObj\lib\nssckbi.dll", "$NsprBin\nspr4.dll")) {
    if (-not (Test-Path $need)) { throw "Missing $need (set up by the retrofox build; see retrofox notes/step2-nss.md)" }
}
$nss = "$Uxp\security\nss"
$targets = Get-Content "$NssObj\targets.json" -Raw | ConvertFrom-Json
$sslDefines = $targets.ssl.defines | Where-Object { $_ -ne "IN_LIBSSL" } | ForEach-Object { "-D$_" }
$obj = "$here\build-arm"
New-Item -ItemType Directory -Force $obj | Out-Null
$cflags = @("--target=thumbv7-pc-windows-msvc", "/MT", "/O2", "/W3", "-X", "/c", "/nologo",
    "-I", "$Rfx\shim\include", "-FI", "retrofox-phone.h",
    "-D_ARM_WINAPI_PARTITION_DESKTOP_SDK_AVAILABLE=1", "-DXP_PC", "-DWIN32",
    "-DXP_WIN", "-DXP_WIN32", "-DNSS_USE_STATIC_LIBS",
    "-D_CRT_SECURE_NO_WARNINGS", "-D_WINSOCK_DEPRECATED_NO_WARNINGS",
    "/imsvc", "$Sdk\vc143\include", "/imsvc", "$Sdk\ucrt\Include\$UcrtVer\ucrt",
    "/imsvc", "$Sdk\win81\Include\um", "/imsvc", "$Sdk\win81\Include\shared",
    "-I", $NsprInc) + $sslDefines
foreach ($d in Get-ChildItem "$nss\lib" -Directory) { $cflags += @("-I", $d.FullName) }
$cflags += @("-I", "$nss\lib\freebl\ecl")

Write-Host "Compiling rfxfetch (ARM)..." -ForegroundColor Cyan
& { $ErrorActionPreference = "Continue"
    & "$Llvm\clang-cl.exe" @cflags "$here\rfxfetch.c" "/Fo$obj\rfxfetch.obj" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "rfxfetch.c compile failed" }
$static = "nssutil", "nssb", "nssdev", "nsspki", "certdb", "certhi", "cryptohi", "pk11wrap_static",
          "nss_static", "softokn_static", "freebl_static", "sqlite", "ssl", "nssckfw" | ForEach-Object { "$_.lib" }
$lflags = @("/nologo", "/machine:arm", "/subsystem:console",
    "/libpath:$Sdk\vc143\lib\arm", "/libpath:$Sdk\ucrt\Lib\$UcrtVer\ucrt\arm", "/libpath:$Sdk\phonekit\lib\ARM",
    "/libpath:$PhoneLib", "/libpath:$NssObj\lib",
    "/libpath:$NsprLib\pr", "/libpath:$NsprLib\libc", "/libpath:$NsprLib\ds") + $static +
    @("nspr4.lib", "plc4.lib", "plds4.lib", "ws2_32.lib", "libcmt.lib", "libvcruntime.lib", "libucrt.lib",
      "WindowsPhoneCore.lib", "kernel32.lib", "retrofox-compat.lib")
& { $ErrorActionPreference = "Continue"
    & "$Llvm\lld-link.exe" @lflags "$obj\rfxfetch.obj" "/out:$obj\rfxfetch.exe" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "rfxfetch link failed" }

$helperDir = "$here\..\app\AppLoader\Helper"
New-Item -ItemType Directory -Force $helperDir | Out-Null
Copy-Item -Force "$obj\rfxfetch.exe" $helperDir
Copy-Item -Force "$NssObj\lib\nssckbi.dll" $helperDir
foreach ($dll in "nspr4.dll", "plc4.dll", "plds4.dll") { Copy-Item -Force "$NsprBin\$dll" $helperDir }
Write-Host "rfxfetch.exe built -> $helperDir" -ForegroundColor Green
