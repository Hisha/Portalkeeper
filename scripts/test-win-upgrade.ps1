#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OldInstaller,
    [Parameter(Mandatory = $true)][string]$NewInstaller
)
$ErrorActionPreference = 'Stop'
# Run as a disposable Windows x64 test user, never against your real installation.
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function InstallEntries {
    foreach ($RegistryRoot in @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        Get-ChildItem -LiteralPath $RegistryRoot -ErrorAction SilentlyContinue |
            Get-ItemProperty | Where-Object { $_.DisplayName -like 'Portalkeeper*' }
    }
}
function RunSetup([string]$Exe, [string[]]$SetupArguments) {
    $Process = Start-Process -FilePath $Exe -ArgumentList $SetupArguments -WindowStyle Hidden -PassThru -Wait
    Assert ($Process.ExitCode -eq 0) "Installer failed ($($Process.ExitCode)); inspect logs in $TestRoot"
}
function HashTree([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName |
        ForEach-Object { $_.FullName.Substring($Directory.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "\n"
}
function AssertInstalled([string]$Version) {
    $Entries = @(InstallEntries)
    Assert ($Entries.Count -eq 1) "Expected one Portalkeeper installation, found $($Entries.Count)"
    Assert ($Entries[0].PSChildName -eq '{64CF5E71-169A-422C-86D5-AC9A62E0AEEE}_is1') 'Unexpected installer identity'
    Assert ($Entries[0].DisplayVersion -eq $Version) 'Wrong installed version'
    Assert ($Entries[0].InstallLocation.TrimEnd('\') -eq $InstallDir) 'Previous install directory was not reused'
    foreach ($Shortcut in @($MenuShortcut, $DesktopShortcut)) {
        Assert (Test-Path -LiteralPath $Shortcut) "Missing shortcut: $Shortcut"
        $Link = $Shell.CreateShortcut($Shortcut)
        Assert ($Link.TargetPath -eq "$InstallDir\Portalkeeper.exe") 'Shortcut target changed'
        Assert ($Link.IconLocation -like "$InstallDir\Portalkeeper.exe*") 'Shortcut icon changed'
    }
}
Assert ([Environment]::Is64BitProcess) 'Use a 64-bit PowerShell process on Windows x64'
$OldInstaller = (Resolve-Path -LiteralPath $OldInstaller).Path
$NewInstaller = (Resolve-Path -LiteralPath $NewInstaller).Path
Assert (((Get-Item -LiteralPath $OldInstaller).VersionInfo.ProductVersion.Trim()) -eq '0.1.0.0') 'Supply the actual released 0.1.0 installer'
Assert (((Get-Item -LiteralPath $NewInstaller).VersionInfo.ProductVersion.Trim()) -eq '0.2.0.0') 'Supply the built 0.2.0 installer'
$DataDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Portalkeeper'
$MenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Portalkeeper.lnk'
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Portalkeeper.lnk'
Assert (@(InstallEntries).Count -eq 0) 'Use a clean test user: Portalkeeper is already installed'
Assert (-not (Test-Path -LiteralPath $DataDir)) 'Use a clean test user: Portalkeeper data already exists'
Assert (-not (Test-Path -LiteralPath $MenuShortcut)) 'Existing Start Menu shortcut; use a clean test user'
Assert (-not (Test-Path -LiteralPath $DesktopShortcut)) 'Existing desktop shortcut; use a clean test user'
$TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('Portalkeeper-upgrade-' + [guid]::NewGuid().ToString('N'))
$InstallDir = Join-Path $TestRoot 'custom install'
$ClientDir = Join-Path $TestRoot 'WoW client'
New-Item -ItemType Directory -Path $TestRoot -Force | Out-Null
$Shell = New-Object -ComObject WScript.Shell
Write-Host "Test artifacts and logs: $TestRoot"
RunSetup $OldInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=desktopicon', "/DIR=`"$InstallDir`"", "/LOG=`"$TestRoot\01-install.log`"")
AssertInstalled '0.1.0'
# Sentinel client content is deliberately not a real playable WoW installation.
foreach ($Relative in @('Wow.exe', 'Data\patch-Z.MPQ', 'Interface\AddOns\KeepMe\KeepMe.toc', 'WTF\Account\test\SavedVariables\KeepMe.lua', '.portalkeeper\backups\keep.bin')) {
    $Path = Join-Path $ClientDir $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, 'Do not modify: ' + $Relative)
}
New-Item -ItemType Directory -Path "$DataDir\realms" -Force | Out-Null
$Settings = Join-Path $DataDir 'settings.json'
@{ ClientPath = $ClientDir; HidePortalkeeperWhileGameRuns = $false } | ConvertTo-Json | Set-Content -LiteralPath $Settings -Encoding UTF8
$Sample = Join-Path (Split-Path -Parent $PSScriptRoot) 'config\example.realm.conf'
Assert (Test-Path -LiteralPath $Sample) 'Run this test from the repository scripts folder'
# Clear all remote URLs so startup cannot refresh the realm or fetch components.
$RealmText = ((Get-Content -LiteralPath $Sample -Raw) -split '(?m)^\[Addon\.', 2)[0]
$RealmText = $RealmText -replace '(?m)^(\s*\w*URL\s*=).*$', '$1'
$RealmText = $RealmText -replace '(?m)^Address=.*$', 'Address=127.0.0.1'
$Realm = Join-Path $DataDir 'realms\upgrade-test.realm.conf'
[IO.File]::WriteAllText($Realm, $RealmText)
$AdjacentRealm = Join-Path $InstallDir 'upgrade-test.realm.conf'
[IO.File]::WriteAllText($AdjacentRealm, $RealmText)
$LegacyNative = Join-Path $InstallDir 'runtimes\win-x64\native'
New-Item -ItemType Directory -Path $LegacyNative -Force | Out-Null
Copy-Item -LiteralPath "$InstallDir\StormLib.dll" -Destination "$LegacyNative\StormLib.dll"
[IO.File]::WriteAllText("$InstallDir\Portalkeeper.pdb", 'obsolete debug symbols')
$ClientBefore = HashTree $ClientDir
$RealmBefore = (Get-FileHash -LiteralPath $Realm).Hash
$AdjacentBefore = (Get-FileHash -LiteralPath $AdjacentRealm).Hash
$SettingsBefore = (Get-FileHash -LiteralPath $Settings).Hash
# Intentionally omit /DIR and /TASKS: the upgrade must reuse both previous choices.
RunSetup $NewInstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$TestRoot\02-upgrade.log`"")
AssertInstalled '0.2.0'
Assert ((Get-FileHash -LiteralPath $Settings).Hash -eq $SettingsBefore) 'Upgrade changed settings'
Assert ((Get-FileHash -LiteralPath $Realm).Hash -eq $RealmBefore) 'Upgrade changed persistent realm'
Assert ((Get-FileHash -LiteralPath $AdjacentRealm).Hash -eq $AdjacentBefore) 'Upgrade changed adjacent realm'
Assert ((HashTree $ClientDir) -eq $ClientBefore) 'Upgrade changed client/addons/patches/backups'
Assert (-not (Test-Path -LiteralPath "$LegacyNative\StormLib.dll")) 'Obsolete native copy survived'
Assert (-not (Test-Path -LiteralPath "$InstallDir\Portalkeeper.pdb")) 'Obsolete debug symbols survived'
Assert ((Get-Item -LiteralPath "$InstallDir\Portalkeeper.exe").VersionInfo.FileVersion -eq '0.2.0.0') 'Wrong application executable version'
Assert (Test-Path -LiteralPath "$InstallDir\StormLib.dll") 'Missing installed StormLib.dll'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PortalkeeperNativeSmoke {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
$Native = [PortalkeeperNativeSmoke]::LoadLibraryW("$InstallDir\StormLib.dll")
Assert ($Native -ne [IntPtr]::Zero) "StormLib load failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
try {
    Assert ([PortalkeeperNativeSmoke]::GetProcAddress($Native, 'SFileOpenArchive') -ne [IntPtr]::Zero) 'Missing StormLib archive API'
} finally { $null = [PortalkeeperNativeSmoke]::FreeLibrary($Native) }
$App = Start-Process -FilePath "$InstallDir\Portalkeeper.exe" -WorkingDirectory $InstallDir -WindowStyle Hidden -PassThru
try {
    $Deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $App.Refresh()
    } while (-not $App.HasExited -and $App.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $Deadline)
    Assert (-not $App.HasExited -and $App.MainWindowHandle -ne 0) 'Upgraded application did not create a window'
    Assert ((Get-Content -LiteralPath $Settings -Raw | ConvertFrom-Json).ClientPath -eq $ClientDir) 'Launch lost configured client path'
    Assert ((HashTree $ClientDir) -eq $ClientBefore) 'Application startup changed client data'
} finally {
    if (-not $App.HasExited) {
        $null = $App.CloseMainWindow()
        if (-not $App.WaitForExit(10000)) { $App.Kill(); $App.WaitForExit() }
    }
}
$Uninstaller = @(Get-ChildItem -LiteralPath $InstallDir -Filter 'unins*.exe')
Assert ($Uninstaller.Count -eq 1) 'Expected one uninstaller'
RunSetup $Uninstaller[0].FullName @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$TestRoot\03-uninstall.log`"")
Assert (@(InstallEntries).Count -eq 0) 'Uninstall entry remains'
Assert (-not (Test-Path -LiteralPath "$InstallDir\Portalkeeper.exe")) 'Uninstall left application executable'
Assert (-not (Test-Path -LiteralPath $MenuShortcut)) 'Uninstall left Start Menu shortcut'
Assert (-not (Test-Path -LiteralPath $DesktopShortcut)) 'Uninstall left desktop shortcut'
Assert ((Get-Content -LiteralPath $Settings -Raw | ConvertFrom-Json).ClientPath -eq $ClientDir) 'Uninstall lost client selection'
Assert ((Get-FileHash -LiteralPath $Realm).Hash -eq $RealmBefore) 'Uninstall changed persistent realm'
Assert ((Get-FileHash -LiteralPath $AdjacentRealm).Hash -eq $AdjacentBefore) 'Uninstall removed adjacent user realm'
Assert ((HashTree $ClientDir) -eq $ClientBefore) 'Uninstall changed client data'
Write-Host 'PASS: one installation, reused custom directory/shortcuts, launch, data preservation and uninstall.'
Write-Host 'Retained test data/logs for inspection. Visual GUI and real-client StormLib checks remain manual.'
