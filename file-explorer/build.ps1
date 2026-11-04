# Builds RetroFiles: the native helper (daemon\rfxfsd.c, ARM) and the WP 8.1 Silverlight app,
# then installs the app on the USB-connected Lumia.
#   .\build.ps1               helper + app + install on the phone (phone must be unlocked)
#   .\build.ps1 -NoDeploy     build only (RetroFiles_Release_AnyCPU.xap)
#   .\build.ps1 -Emulator     install on the WP 8.1 emulator instead (helper won't run there)
# Uses the retrofox phone toolchain (C:\rfx\sdk junctions and retrofox\build\lib) but never
# regenerates those libs, so it is safe to run while a Gecko build is going.
param([switch]$NoDeploy, [switch]$Emulator, [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$Llvm = "C:\Program Files\LLVM\bin"
$Sdk = "C:\rfx\sdk"
$UcrtVer = "10.0.22621.0"
$Rfx = if ($env:RETROFOX_ROOT) { $env:RETROFOX_ROOT } else { "C:\rfx\retrofox" }
$PhoneLib = "$Rfx\build\lib"
$pf86 = ${env:ProgramFiles(x86)}

# ---- helper (native ARM) ----
foreach ($need in @("$Llvm\clang-cl.exe", "$Sdk\vc143\include", "$PhoneLib\kernel32.lib")) {
    if (-not (Test-Path $need)) { throw "Missing $need (set up by the retrofox build; see retrofox\scripts\env.ps1)" }
}
$obj = "$here\daemon\build"
New-Item -ItemType Directory -Force $obj | Out-Null
$cflags = @("--target=thumbv7-pc-windows-msvc", "/MT", "/O2", "/W3", "-X",
    "-I", "$Rfx\shim\include", "-FI", "retrofox-phone.h",
    "-D_ARM_WINAPI_PARTITION_DESKTOP_SDK_AVAILABLE=1", "-D_CRT_SECURE_NO_WARNINGS", "-D_WINSOCK_DEPRECATED_NO_WARNINGS",
    "/imsvc", "$Sdk\vc143\include", "/imsvc", "$Sdk\ucrt\Include\$UcrtVer\ucrt",
    "/imsvc", "$Sdk\win81\Include\um", "/imsvc", "$Sdk\win81\Include\shared")
Write-Host "Compiling rfxfsd (ARM)..." -ForegroundColor Cyan
& "$Llvm\clang-cl.exe" @cflags /c "$here\daemon\rfxfsd.c" "/Fo$obj\rfxfsd.obj" 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw "rfxfsd compile failed" }
$lflags = @("/machine:arm", "/subsystem:console",
    "/libpath:$Sdk\vc143\lib\arm", "/libpath:$Sdk\ucrt\Lib\$UcrtVer\ucrt\arm", "/libpath:$Sdk\phonekit\lib\ARM", "/libpath:$PhoneLib",
    "libcmt.lib", "libvcruntime.lib", "libucrt.lib", "WindowsPhoneCore.lib", "kernel32.lib", "retrofox-compat.lib", "ws2_32.lib")
& "$Llvm\lld-link.exe" @lflags "$obj\rfxfsd.obj" "/out:$obj\rfxfsd.exe" 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw "rfxfsd link failed" }
New-Item -ItemType Directory -Force "$here\app\RetroFiles\Helper" | Out-Null
Copy-Item -Force "$obj\rfxfsd.exe" "$here\app\RetroFiles\Helper\rfxfsd.exe"
Write-Host "rfxfsd.exe built" -ForegroundColor Green

# ---- app ----
$msbuild = "$pf86\MSBuild\14.0\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild 14 not found. Install VS 2015 first." }
Write-Host "Building the app..." -ForegroundColor Cyan
& $msbuild "$here\app\RetroFiles.sln" "/p:Configuration=$Configuration" "/p:Platform=Any CPU" /p:VisualStudioVersion=14.0 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "App build failed." }
$xap = Get-ChildItem "$here\app\RetroFiles\Bin" -Recurse -Filter *.xap | Sort-Object LastWriteTime -Descending | Select-Object -First 1
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
