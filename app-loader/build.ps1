# Builds App Loader: helper\aldetach.c and fetch\rfxfetch.c (native ARM) and the WP 8.1 Silverlight app
#   .\build.ps1      ->  app\AppLoader\Bin\AnyCPU\Release\AppLoader_Release_AnyCPU.xap
# Install it on the phone with retrofox's rfx tool:  rfx install <xap> -Mode di   (first time)
#                                                    rfx install <xap> -Mode uda  (updates)
# Uses the retrofox phone toolchain (C:\rfx\sdk junctions and retrofox\build\lib) for the helper.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$Llvm = "C:\Program Files\LLVM\bin"
$Sdk = "C:\rfx\sdk"
$UcrtVer = "10.0.22621.0"
$Rfx = if ($env:RETROFOX_ROOT) { $env:RETROFOX_ROOT } else { "C:\rfx\retrofox" }
$PhoneLib = "$Rfx\build\lib"

# ---- aldetach.exe (native ARM): starts the install batch outside the telnet session ----
foreach ($need in @("$Llvm\clang-cl.exe", "$Sdk\vc143\include", "$PhoneLib\kernel32.lib")) {
    if (-not (Test-Path $need)) { throw "Missing $need (set up by the retrofox build; see retrofox\scripts\env.ps1)" }
}
$obj = "$here\helper\build"
New-Item -ItemType Directory -Force $obj | Out-Null
$cflags = @("--target=thumbv7-pc-windows-msvc", "/MT", "/O2", "/W3", "-X",
    "-I", "$Rfx\shim\include", "-FI", "retrofox-phone.h",
    "-D_ARM_WINAPI_PARTITION_DESKTOP_SDK_AVAILABLE=1", "-D_CRT_SECURE_NO_WARNINGS",
    "/imsvc", "$Sdk\vc143\include", "/imsvc", "$Sdk\ucrt\Include\$UcrtVer\ucrt",
    "/imsvc", "$Sdk\win81\Include\um", "/imsvc", "$Sdk\win81\Include\shared")
Write-Host "Compiling aldetach (ARM)..." -ForegroundColor Cyan
# Compiler warnings go to stderr: under "Stop", PowerShell 5.1 would turn them into errors
& { $ErrorActionPreference = "Continue"
    & "$Llvm\clang-cl.exe" @cflags /c "$here\helper\aldetach.c" "/Fo$obj\aldetach.obj" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "aldetach compile failed" }
$lflags = @("/machine:arm", "/subsystem:console",
    "/libpath:$Sdk\vc143\lib\arm", "/libpath:$Sdk\ucrt\Lib\$UcrtVer\ucrt\arm", "/libpath:$Sdk\phonekit\lib\ARM", "/libpath:$PhoneLib",
    "libcmt.lib", "libvcruntime.lib", "libucrt.lib", "WindowsPhoneCore.lib", "kernel32.lib", "retrofox-compat.lib")
& { $ErrorActionPreference = "Continue"
    & "$Llvm\lld-link.exe" @lflags "$obj\aldetach.obj" "/out:$obj\aldetach.exe" 2>&1 | ForEach-Object { "$_" } }
if ($LASTEXITCODE -ne 0) { throw "aldetach link failed" }
New-Item -ItemType Directory -Force "$here\app\AppLoader\Helper" | Out-Null
Copy-Item -Force "$obj\aldetach.exe" "$here\app\AppLoader\Helper"
Write-Host "aldetach.exe built" -ForegroundColor Green

# ---- rfxfetch.exe + NSS/NSPR DLLs (catalog downloads, stage 2) ----
& "$here\fetch\build.ps1"

# ---- app ----
$msbuild = "${env:ProgramFiles(x86)}\MSBuild\14.0\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild 14 not found. Install VS 2015 first." }
& $msbuild "$here\app\AppLoader.sln" "/p:Configuration=$Configuration" "/p:Platform=Any CPU" /p:VisualStudioVersion=14.0 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "App build failed." }
$xap = Get-ChildItem "$here\app\AppLoader\Bin" -Recurse -Filter *.xap | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Package: $($xap.FullName)" -ForegroundColor Green
