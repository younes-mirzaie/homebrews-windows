# Builds Retrobird: the native mail helper (helper\*.c on NSPR/NSS, ARM; rbmail.dll runs inside
# the app, rbmaild.exe is the fallback started through CMD Injector) and the WP 8.1
# Silverlight app, then installs the app on the USB-connected Lumia.
#   .\build.ps1               helper + app + install on the phone (phone must be unlocked)
#   .\build.ps1 -NoDeploy     build only (Retrobird_Release_AnyCPU.xap)
#   .\build.ps1 -Emulator     install on the WP 8.1 emulator instead (helper won't run there)
# Needs the retrofox phone toolchain and its NSS build (retrofox notes/step2-nss.md):
#   C:\rfx\sdk, C:\rfx\uxp (NSS headers), C:\rfx\obj-js (NSPR), C:\rfx\obj-nss (static NSS libs,
#   nssckbi.dll, targets.json). It never rebuilds those, so it is safe next to a Gecko build.
# The rfxdev portal (http://<phone>:8082) can install the .xap over Wi-Fi instead of USB.
param([switch]$NoDeploy, [switch]$Emulator, [string]$Configuration = "Release")
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
$pf86 = ${env:ProgramFiles(x86)}

# ---- helper (native ARM) ----
foreach ($need in @("$Llvm\clang-cl.exe", "$Sdk\vc143\include", "$PhoneLib\kernel32.lib", "$NsprInc\nspr.h",
                    "$NssObj\targets.json", "$NssObj\lib\ssl.lib", "$NssObj\lib\nssckbi.dll", "$NsprBin\nspr4.dll")) {
    if (-not (Test-Path $need)) { throw "Missing $need (set up by the retrofox build; see retrofox notes/step2-nss.md)" }
}
$nss = "$Uxp\security\nss"
$targets = Get-Content "$NssObj\targets.json" -Raw | ConvertFrom-Json
# Same platform defines as libssl (NSPR's headers need WIN95/_WINDOWS)
$sslDefines = $targets.ssl.defines | Where-Object { $_ -ne "IN_LIBSSL" } | ForEach-Object { "-D$_" }
$obj = "$here\helper\build"
New-Item -ItemType Directory -Force $obj | Out-Null
$cflags = @("--target=thumbv7-pc-windows-msvc", "/MT", "/O2", "/W3", "-X", "/c", "/nologo",
    "-I", "$Rfx\shim\include", "-FI", "retrofox-phone.h",
    "-D_ARM_WINAPI_PARTITION_DESKTOP_SDK_AVAILABLE=1", "-DXP_PC", "-DWIN32",
    # XP_WIN changes NSPR's PRNetAddr layout; it must match how NSPR/NSS were built
    "-DXP_WIN", "-DXP_WIN32", "-DNSS_USE_STATIC_LIBS",
    "-D_CRT_SECURE_NO_WARNINGS", "-D_WINSOCK_DEPRECATED_NO_WARNINGS",
    "/imsvc", "$Sdk\vc143\include", "/imsvc", "$Sdk\ucrt\Include\$UcrtVer\ucrt",
    "/imsvc", "$Sdk\win81\Include\um", "/imsvc", "$Sdk\win81\Include\shared",
    "-I", $NsprInc) + $sslDefines
foreach ($d in Get-ChildItem "$nss\lib" -Directory) { $cflags += @("-I", $d.FullName) }
# blapit.h (freebl) includes ecl-exp.h, one level further down
$cflags += @("-I", "$nss\lib\freebl\ecl")

$sources = "buf", "codec", "mime", "net", "store", "imap", "smtp", "server"
Write-Host "Compiling rbmaild (ARM)..." -ForegroundColor Cyan
foreach ($s in $sources) {
    # Compiler warnings go to stderr: under "Stop", PowerShell 5.1 would turn them into errors.
    # Only the exit code decides.
    & { $ErrorActionPreference = "Continue"
        & "$Llvm\clang-cl.exe" @cflags "$here\helper\$s.c" "/Fo$obj\$s.obj" 2>&1 | ForEach-Object { "$_" } }
    if ($LASTEXITCODE -ne 0) { throw "$s.c compile failed" }
}
$static = "nssutil", "nssb", "nssdev", "nsspki", "certdb", "certhi", "cryptohi", "pk11wrap_static",
          "nss_static", "softokn_static", "freebl_static", "sqlite", "ssl", "nssckfw" | ForEach-Object { "$_.lib" }
$lflags = @("/nologo", "/machine:arm", "/subsystem:console",
    "/libpath:$Sdk\vc143\lib\arm", "/libpath:$Sdk\ucrt\Lib\$UcrtVer\ucrt\arm", "/libpath:$Sdk\phonekit\lib\ARM",
    "/libpath:$PhoneLib", "/libpath:$NssObj\lib",
    "/libpath:$NsprLib\pr", "/libpath:$NsprLib\libc", "/libpath:$NsprLib\ds") + $static +
    @("nspr4.lib", "plc4.lib", "plds4.lib", "ws2_32.lib", "libcmt.lib", "libvcruntime.lib", "libucrt.lib",
      "WindowsPhoneCore.lib", "kernel32.lib", "retrofox-compat.lib")
$objs = $sources | ForEach-Object { "$obj\$_.obj" }
& { $ErrorActionPreference = "Continue"
    & "$Llvm\lld-link.exe" @lflags @objs "/out:$obj\rbmaild.exe" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "rbmaild link failed" }
# The same helper as rbmail.dll, which the app loads and runs inside its own process
& { $ErrorActionPreference = "Continue"
    & "$Llvm\clang-cl.exe" @cflags -DRB_DLL "$here\helper\server.c" "/Fo$obj\server_dll.obj" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "server.c (dll) compile failed" }
$dllObjs = ($sources | Where-Object { $_ -ne "server" } | ForEach-Object { "$obj\$_.obj" }) + "$obj\server_dll.obj"
$dllFlags = $lflags | Where-Object { $_ -ne "/subsystem:console" }
& { $ErrorActionPreference = "Continue"
    & "$Llvm\lld-link.exe" /dll @dllFlags @dllObjs "/out:$obj\rbmail.dll" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "rbmail.dll link failed" }

$helperDir = "$here\app\Retrobird\Helper"
New-Item -ItemType Directory -Force $helperDir | Out-Null
Copy-Item -Force "$obj\rbmaild.exe" $helperDir
Copy-Item -Force "$obj\rbmail.dll" $helperDir
Copy-Item -Force "$NssObj\lib\nssckbi.dll" $helperDir
foreach ($dll in "nspr4.dll", "plc4.dll", "plds4.dll") { Copy-Item -Force "$NsprBin\$dll" $helperDir }
Write-Host "rbmail.dll and rbmaild.exe built" -ForegroundColor Green

# ---- app ----
$msbuild = "$pf86\MSBuild\14.0\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild 14 not found. Install VS 2015 first." }
Write-Host "Building the app..." -ForegroundColor Cyan
& $msbuild "$here\app\Retrobird.sln" "/p:Configuration=$Configuration" "/p:Platform=Any CPU" /p:VisualStudioVersion=14.0 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "App build failed." }
$xap = Get-ChildItem "$here\app\Retrobird\Bin" -Recurse -Filter *.xap | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $xap) { throw "No .xap produced." }
Write-Host "Package: $($xap.FullName)" -ForegroundColor Green
if ($NoDeploy) { return }

$deploy = "$pf86\Microsoft SDKs\Windows Phone\v8.1\Tools\AppDeploy\AppDeployCmd.exe"
if (-not (Test-Path $deploy)) {
    $found = Get-ChildItem "$pf86\Microsoft SDKs\Windows Phone" -Recurse -Filter AppDeployCmd.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $deploy = $found.FullName } else { throw "AppDeployCmd.exe not found." }
}
$target = if ($Emulator) { "xd" } else { "de" }
& $deploy /installlaunch $xap.FullName /targetdevice:$target
