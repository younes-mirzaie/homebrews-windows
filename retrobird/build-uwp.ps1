# Builds Retrobird for Windows 10 (UWP, ARM, .NET Native) and puts the package and the framework
# packages it needs into app-uwp\release\, ready to install by hand.
#   .\build-uwp.ps1
# Needs Visual Studio 2017 or later (or its Build Tools) with the "Universal Windows Platform
# build tools" workload, and a Windows 10 SDK (10.0.17763.0 by default; pass another with -Sdk).
# The mail engine (rbmail.dll and the NSS/NSPR DLLs) is the 8.1 app's; when it isn't built yet,
# the 8.1 build (build.ps1) runs first to make it.
# Signing: Windows 10 Mobile refuses a package with no signature at all, even with signature
# checks skipped. So the package is signed with a self-signed "CN=Retrobird" certificate (it must
# match the manifest's Publisher). The certificate and its private key stay OUT of the repo, by
# default in C:\rfx\keys\retrobird-signing.pfx (made on first run, no password, current user only);
# only the public .cer goes into release\, for devices that want to trust it.
param([string]$Configuration = "Release", [string]$Sdk = "10.0.17763.0",
      [string]$CertPfx = "C:\rfx\keys\retrobird-signing.pfx")
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$proj = "$here\app-uwp\Retrobird10\Retrobird10.csproj"
$subject = "CN=Retrobird"

$helper = "$here\app\Retrobird\Helper"
if (-not (Test-Path "$helper\rbmail.dll") -or -not (Test-Path "$helper\nssckbi.dll")) {
    Write-Host "Mail engine missing, building it with build.ps1 first..." -ForegroundColor Cyan
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

$appx = Get-ChildItem "$here\app-uwp\Retrobird10\AppPackages" -Recurse -Filter "Retrobird10_*_ARM*.appx" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $appx) { throw "No .appx was produced." }
$out = "$here\app-uwp\release"
New-Item -ItemType Directory -Force "$out\Dependencies" | Out-Null
Copy-Item -Force $appx.FullName $out
$deps = Join-Path $appx.DirectoryName "Dependencies\ARM"
if (Test-Path $deps) { Copy-Item -Force "$deps\*.appx" "$out\Dependencies" }

# ---- sign ----
if (-not (Test-Path $CertPfx)) {
    Write-Host "Making the signing certificate ($subject) at $CertPfx ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Split-Path $CertPfx) | Out-Null
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName "Retrobird package signing" -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(20)
    Export-PfxCertificate -Cert $cert -FilePath $CertPfx -Password (New-Object System.Security.SecureString) | Out-Null
}
$pfx = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertPfx, "")
if ($pfx.Subject -ne $subject) { throw "$CertPfx is for '$($pfx.Subject)', the manifest needs $subject." }
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
            Where-Object { $_.FullName -match "\\x64\\" } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found (Windows 10 SDK)." }
& $signtool.FullName sign /fd SHA256 /f $CertPfx "$out\$($appx.Name)"
if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
[System.IO.File]::WriteAllBytes("$out\Retrobird10.cer", $pfx.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))

$hash = (Get-FileHash -Algorithm SHA256 "$out\$($appx.Name)").Hash.ToLowerInvariant()
Write-Host "Package: $out\$($appx.Name)" -ForegroundColor Green
Write-Host "SHA-256: $hash  size: $((Get-Item "$out\$($appx.Name)").Length)" -ForegroundColor Green
Get-ChildItem "$out\Dependencies" | ForEach-Object { Write-Host "Needs:   $($_.Name)" }
