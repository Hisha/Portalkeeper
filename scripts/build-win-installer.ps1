#!/usr/bin/env pwsh
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

# Builds the portable win-x64 ZIP and then compiles the Inno Setup installer.
# Requires Inno Setup 6 (ISCC.exe) on PATH or in a standard install location.
Write-Host 'Building the portable ZIP and the Windows installer...'
$Publish = Join-Path $PSScriptRoot 'publish-win.ps1'
& $Publish -Installer
exit $LASTEXITCODE