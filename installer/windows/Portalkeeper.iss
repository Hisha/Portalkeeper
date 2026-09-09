; Portalkeeper Windows installer (Inno Setup).
;
; Builds from the already-published win-x64 output, so users never need a
; PowerShell launch/install script. The installer is per-user by default and
; does not require administrator rights.
;
; Compile with the project version and paths passed as defines so the
; installer version always comes from Portalkeeper.csproj:
;
;   ISCC /DMyAppVersion=<version> /DMyAppSourceDir=<publish dir> /DMyAppOutputDir=<dist> Portalkeeper.iss
;
; scripts/publish-win.ps1 -Installer (or scripts/build-win-installer.ps1)
; supplies these automatically.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyAppSourceDir
  #define MyAppSourceDir "..\..\dist"
#endif
#ifndef MyAppOutputDir
  #define MyAppOutputDir "..\..\dist"
#endif

[Setup]
; Stable per-app GUID; keep it unchanged across releases for clean upgrades.
AppId={{64CF5E71-169A-422C-86D5-AC9A62E0AEEE}
AppName=Portalkeeper
AppVersion={#MyAppVersion}
AppVerName=Portalkeeper {#MyAppVersion}
AppPublisher=Portalkeeper
DefaultDirName={localappdata}\Programs\Portalkeeper
DefaultGroupName=Portalkeeper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#MyAppOutputDir}
OutputBaseFilename=Portalkeeper-Setup-{#MyAppVersion}
SetupIconFile=..\..\assets\branding\portalkeeper-icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Portalkeeper.exe
UninstallDisplayName=Portalkeeper
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=Portalkeeper {#MyAppVersion}
VersionInfoProductName=Portalkeeper
VersionInfoProductVersion={#MyAppVersion}.0
CloseApplications=yes
CloseApplicationsFilter=Portalkeeper.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Install the entire published win-x64 payload (Portalkeeper.exe, the .NET
; runtime, StormLib.dll, StormLib.LICENSE.txt, README, LICENSE, config, ...).
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Portalkeeper"; Filename: "{app}\Portalkeeper.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Portalkeeper.exe"; Comment: "Portalkeeper {#MyAppVersion}"
Name: "{autodesktop}\Portalkeeper"; Filename: "{app}\Portalkeeper.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Portalkeeper.exe"; Comment: "Portalkeeper {#MyAppVersion}"; Tasks: desktopicon

[Run]
Filename: "{app}\Portalkeeper.exe"; Description: "{cm:LaunchProgram,Portalkeeper}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent