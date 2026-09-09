#!/usr/bin/env pwsh
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$SourcePath = $PSScriptRoot

$LocalAppData = $env:LOCALAPPDATA
if (-not $LocalAppData) {
    Write-Error 'LOCALAPPDATA must be set.'
    exit 1
}
$StartMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$Dest = Join-Path $LocalAppData 'Programs\Portalkeeper'

$SourceFull = [System.IO.Path]::GetFullPath($SourcePath)
$DestFull = [System.IO.Path]::GetFullPath($Dest)
if ($SourceFull -eq $DestFull) {
    Write-Error 'Run the installer from the extracted ZIP.'
    exit 1
}

$LaunchScript = Join-Path $SourcePath 'launch.ps1'
if (-not (Test-Path -LiteralPath $LaunchScript)) {
    Write-Error "launch.ps1 not found next to the installer: $LaunchScript"
    exit 1
}
& $LaunchScript --check-dependencies
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Drop any Zone.Identifier blocks inherited from the downloaded ZIP.
if (Get-Command Unblock-File -ErrorAction SilentlyContinue) {
    Get-ChildItem -Path $SourcePath -File -Recurse | Unblock-File
}

$Marker = Join-Path $Dest '.portalkeeper-install'
if ((Test-Path -LiteralPath $Dest -PathType Container) -and -not (Test-Path -LiteralPath $Marker)) {
    Write-Error "Refusing to replace an unmanaged folder: $Dest"
    exit 1
}

New-Item -ItemType Directory -Path (Split-Path -Parent $Dest) -Force | Out-Null

$Stage = Join-Path $LocalAppData ".portalkeeper-install.$([System.Guid]::NewGuid().ToString('N'))"
$Backup = $null

try {
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $Stage -Recurse -Force
    New-Item -ItemType File -Path (Join-Path $Stage '.portalkeeper-install') -Force | Out-Null

    # Keep the previous application available until the replacement is staged.
    if (Test-Path -LiteralPath $Dest -PathType Container) {
        $Backup = Join-Path $LocalAppData ".portalkeeper-old.$([System.Guid]::NewGuid().ToString('N'))"
        Rename-Item -LiteralPath $Dest -NewName (Split-Path -Leaf $Backup)
    }

    try {
        Move-Item -LiteralPath $Stage -Destination $Dest
    } catch {
        if ($Backup -and (Test-Path -LiteralPath $Backup -PathType Container)) {
            Move-Item -LiteralPath $Backup -Destination $Dest
        }
        throw
    }

    $ShortcutPath = Join-Path $StartMenuDir 'Portalkeeper.lnk'
    $WshShell = New-Object -ComObject WScript.Shell
    $Shortcut = $WshShell.CreateShortcut($ShortcutPath)
    $Shortcut.TargetPath = Join-Path $Dest 'Portalkeeper.exe'
    $Shortcut.WorkingDirectory = $Dest
    $Shortcut.IconLocation = Join-Path $Dest 'portalkeeper-icon.png'
    $Shortcut.Description = 'Realm launcher and Armory'
    $Shortcut.Save()

    Write-Host "Installed Portalkeeper to $Dest"
    Write-Host 'Open Portalkeeper from your Start menu.'
} finally {
    if (Test-Path -LiteralPath $Stage -PathType Container) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    if ($Backup -and (Test-Path -LiteralPath $Backup -PathType Container)) { Remove-Item -LiteralPath $Backup -Recurse -Force }
}