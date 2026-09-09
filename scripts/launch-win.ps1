#!/usr/bin/env pwsh
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$AppDir = $PSScriptRoot

if (-not [System.Environment]::Is64BitOperatingSystem) {
    Write-Error 'Portalkeeper requires Windows x64.'
    exit 1
}

$Exe = Join-Path $AppDir 'Portalkeeper.exe'
$StormLib = Join-Path $AppDir 'StormLib.dll'
foreach ($required in @($Exe, $StormLib)) {
    if (-not (Test-Path -LiteralPath $required)) {
        Write-Error "Portalkeeper is missing a required file: $required"
        exit 1
    }
}

if ($args.Count -ge 1 -and $args[0] -eq '--check-dependencies') {
    Write-Host 'Portalkeeper native dependency check passed.'
    exit 0
}

& $Exe $args
exit $LASTEXITCODE