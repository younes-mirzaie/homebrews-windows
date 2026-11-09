# Builds App Loader (WP 8.1 Silverlight app, no native parts): app\AppLoader\Bin\...\AppLoader_Release_AnyCPU.xap
#   .\build.ps1
# Install it on the phone with retrofox's rfx tool:  rfx install <xap> -Mode di   (first time)
#                                                    rfx install <xap> -Mode uda  (updates)
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$msbuild = "${env:ProgramFiles(x86)}\MSBuild\14.0\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild 14 not found. Install VS 2015 first." }
& $msbuild "$here\app\AppLoader.sln" "/p:Configuration=$Configuration" "/p:Platform=Any CPU" /p:VisualStudioVersion=14.0 /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "App build failed." }
$xap = Get-ChildItem "$here\app\AppLoader\Bin" -Recurse -Filter *.xap | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Package: $($xap.FullName)" -ForegroundColor Green
