#!/usr/bin/env pwsh
#Requires -Version 5.1
param(
    [switch]$Installer
)
$ErrorActionPreference = 'Stop'

function Find-Iscc {
    $Cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($Cmd) { return $Cmd.Source }

    $CandidateDirs = @()
    foreach ($Lookup in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not $Lookup) { continue }
        $CandidateDirs += Join-Path $Lookup 'Inno Setup 6'
        $CandidateDirs += Join-Path $Lookup 'Inno Setup 5'
        $CandidateDirs += Join-Path $Lookup 'Programs\Inno Setup 6'
    }
    foreach ($Dir in $CandidateDirs) {
        $Candidate = Join-Path $Dir 'ISCC.exe'
        if (Test-Path -LiteralPath $Candidate) { return $Candidate }
    }

    $UninstallRoots = @(
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($RootKey in $UninstallRoots) {
        Get-ItemProperty $RootKey -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
            ForEach-Object {
                $Candidate = Join-Path $_.InstallLocation 'ISCC.exe'
                if (Test-Path -LiteralPath $Candidate) { return $Candidate }
            }
    }

    return $null
}

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
if ($Installer) {
    $SetupExe = Join-Path $Dist "Portalkeeper-Setup-$Version.exe"
    if (Test-Path -LiteralPath $SetupExe) { Remove-Item -LiteralPath $SetupExe -Force }
}
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

& dotnet publish $Project -c Release -r $Rid --self-contained true -o $PackageDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# The project publishes StormLib.dll and its license beside Portalkeeper.exe.
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

Copy-Item (Join-Path $Root 'assets\branding\portalkeeper-icon.png') (Join-Path $PackageDir 'portalkeeper-icon.png')

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
Write-Host 'Extract and run Portalkeeper.exe directly. The .NET runtime and StormLib.dll are bundled.'

if ($Installer) {
    $Iscc = Find-Iscc
    if (-not $Iscc) {
        Write-Host
        Write-Host 'ERROR: Inno Setup compiler (ISCC.exe) was not found.' -ForegroundColor Red
        Write-Host 'The portable ZIP was still created above.' -ForegroundColor Yellow
        Write-Host 'Install Inno Setup 6 (6.2 or newer) from https://jrsoftware.org/isdl.php and re-run with -Installer,' -ForegroundColor Yellow
        Write-Host "or add the Inno Setup bin directory to PATH so ISCC.exe is discoverable." -ForegroundColor Yellow
        exit 1
    }

    $IssFile = Join-Path $Root 'installer\windows\Portalkeeper.iss'
    Write-Host
    Write-Host "Compiling the Windows installer with $Iscc ..."
    & $Iscc `
        "/DMyAppVersion=$Version" `
        "/DMyAppSourceDir=$PackageDir" `
        "/DMyAppOutputDir=$Dist" `
        $IssFile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $SetupExe = Join-Path $Dist "Portalkeeper-Setup-$Version.exe"
    if (-not (Test-Path -LiteralPath $SetupExe)) {
        Write-Error "Inno Setup finished but did not produce $SetupExe"
        exit 1
    }
    Write-Host
    Write-Host 'Windows installer created:'
    Write-Host "  $SetupExe"
    Write-Host
    Write-Host 'Run the installer for a per-user install with Start Menu shortcut and uninstall entry.'
}