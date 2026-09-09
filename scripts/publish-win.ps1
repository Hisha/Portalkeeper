#!/usr/bin/env pwsh
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Rid = 'win-x64'
$Dist = Join-Path $Root 'dist'
$Project = Join-Path $Root 'src\Portalkeeper\Portalkeeper.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet was not found in PATH.'
    exit 1
}

if (-not (Get-Command Compress-Archive -ErrorAction SilentlyContinue)) {
    Write-Error 'Compress-Archive (Microsoft.PowerShell.Archive) was not found. Install it before creating the release archive.'
    exit 1
}

$Version = & dotnet msbuild $Project -getProperty:Version
if ($LASTEXITCODE -ne 0 -or -not $Version) {
    Write-Error 'Failed to read the project version.'
    exit 1
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.+-][A-Za-z0-9.-]+)?$') {
    Write-Error "Invalid project version: $Version"
    exit 1
}
$PackageDir = Join-Path $Dist "Portalkeeper-$Version-$Rid"
$Archive = Join-Path $Dist "Portalkeeper-$Version-$Rid.zip"

Write-Host "Publishing Portalkeeper $Version for $Rid..."
if (Test-Path -LiteralPath $PackageDir) { Remove-Item -LiteralPath $PackageDir -Recurse -Force }
if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

& dotnet publish $Project -c Release -r $Rid --self-contained true -o $PackageDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The project publishes StormLib.dll and its license beside Portalkeeper.dll.
$StormLib = Join-Path $PackageDir 'StormLib.dll'
$StormLibLicense = Join-Path $PackageDir 'StormLib.LICENSE.txt'
if (-not (Test-Path -LiteralPath $StormLib)) {
    Write-Error "StormLib.dll missing from publish output: $StormLib"
    exit 1
}
if (-not (Test-Path -LiteralPath $StormLibLicense)) {
    Write-Error "StormLib.LICENSE.txt missing from publish output: $StormLibLicense"
    exit 1
}

Copy-Item (Join-Path $Root 'scripts\launch-win.ps1') (Join-Path $PackageDir 'launch.ps1')
Copy-Item (Join-Path $Root 'scripts\install-win.ps1') (Join-Path $PackageDir 'install.ps1')
Copy-Item (Join-Path $Root 'assets\branding\portalkeeper-icon.png') (Join-Path $PackageDir 'portalkeeper-icon.png')

if ($env:OS -eq 'Windows_NT') {
    & (Join-Path $PackageDir 'launch.ps1') --check-dependencies
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Copy-Item (Join-Path $Root 'README.md') (Join-Path $PackageDir 'README.md')
Copy-Item (Join-Path $Root 'LICENSE') (Join-Path $PackageDir 'LICENSE')

# The public example is the only realm config that may ship in a package.
$PrivateRealmFiles = Get-ChildItem -Path $PackageDir -Recurse -File -Filter '*.realm.conf' |
    Where-Object { $_.Name -ne 'example.realm.conf' }
if ($PrivateRealmFiles) {
    Write-Host 'ERROR: private realm configuration detected in release output:' -ForegroundColor Red
    $PrivateRealmFiles | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    exit 1
}

# Development-only files should never be handed to users.
Get-ChildItem -Path $PackageDir -Recurse -File |
    Where-Object { $_.Name -like '*.pdb' -or $_.Name -like '*.Development.json' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Compress-Archive -Path $PackageDir -DestinationPath $Archive -Force

Write-Host
Write-Host 'Release package created:'
Write-Host "  $Archive"
Write-Host
Write-Host 'Extract and run Portalkeeper.exe (or launch.ps1), or install.ps1 for a user-level Start menu installation.'
Write-Host 'The .NET runtime and StormLib.dll are bundled.'