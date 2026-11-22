# Builds Unesbrews for Windows 10 Mobile (UWP, ARM, .NET Native) and puts the package and the
# framework packages it needs into app-uwp\release\, ready to install by hand or to publish.
#   .\build-uwp.ps1
# Needs Visual Studio 2017 or later (or its Build Tools) with the "Universal Windows Platform
# build tools" workload, and a Windows 10 SDK (10.0.22621.0 is the default; another installed one
# can be passed with -Sdk).
# The native helpers (aldetach, rfxfetch) are the 8.1 app's; when they aren't built yet, the 8.1
# build (build.ps1) runs first to make them.
# Signing: the phone refuses a package with no signature at all (0x800B0100), even with signature
# checks skipped. So the package is signed with a self-signed "CN=Unesbrews" certificate (it must
# match the manifest's Publisher). The certificate and its private key stay OUT of the repo, by
# default in C:\rfx\keys\unesbrews-signing.pfx (made on first run, no password, current user only);
# only the public .cer goes into release\, for phones that want to trust it. See app-uwp\README.md.
param([string]$Configuration = "Release", [string]$Sdk = "10.0.17763.0",
      [string]$CertPfx = "C:\rfx\keys\unesbrews-signing.pfx")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$proj = "$here\app-uwp\Unesbrews\Unesbrews.csproj"

$helper = "$here\app\AppLoader\Helper"
if (-not (Test-Path "$helper\aldetach.exe") -or -not (Test-Path "$helper\rfxfetch.exe")) {
    Write-Host "Native helpers missing, building them with build.ps1 first..." -ForegroundColor Cyan
    & "$here\build.ps1"
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found: install Visual Studio 2017 or later (or the Build Tools)." }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found through vswhere." }
Write-Host "MSBuild: $msbuild" -ForegroundColor Cyan

& $msbuild $proj /restore "/p:Configuration=$Configuration" /p:Platform=ARM "/p:TargetPlatformVersion=$Sdk" `
    /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$appx = Get-ChildItem "$here\app-uwp\Unesbrews\AppPackages" -Recurse -Filter "Unesbrews_*_ARM*.appx" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appx) { throw "No .appx was produced." }
$out = "$here\app-uwp\release"
New-Item -ItemType Directory -Force "$out\Dependencies" | Out-Null
Copy-Item -Force $appx.FullName $out
$deps = Join-Path $appx.DirectoryName "Dependencies\ARM"
if (Test-Path $deps) { Copy-Item -Force "$deps\*.appx" "$out\Dependencies" }
# ---- sign ----
if (-not (Test-Path $CertPfx)) {
    Write-Host "Making the signing certificate (CN=Unesbrews) at $CertPfx ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Split-Path $CertPfx) | Out-Null
    $cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Unesbrews" -KeyUsage DigitalSignature `
        -FriendlyName "Unesbrews package signing" -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(20)
    Export-PfxCertificate -Cert $cert -FilePath $CertPfx -Password (New-Object System.Security.SecureString) | Out-Null
}
$pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPfx, "")
if ($pfx.Subject -ne "CN=Unesbrews") { throw "$CertPfx is for '$($pfx.Subject)', the manifest needs CN=Unesbrews." }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
            Where-Object { $_.FullName -match "\\x64\\" } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found (Windows 10 SDK)." }
& $signtool.FullName sign /fd SHA256 /f $CertPfx "$out\$($appx.Name)"
if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
[System.IO.File]::WriteAllBytes("$out\Unesbrews.cer", $pfx.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

$hash = (Get-FileHash -Algorithm SHA256 "$out\$($appx.Name)").Hash.ToLowerInvariant()
Write-Host "Package: $out\$($appx.Name)" -ForegroundColor Green
Write-Host "SHA-256: $hash  size: $((Get-Item "$out\$($appx.Name)").Length)" -ForegroundColor Green
Get-ChildItem "$out\Dependencies" | ForEach-Object { Write-Host "Needs:   $($_.Name)" }
