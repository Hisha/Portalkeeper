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
  #error MyAppVersion is required; use scripts/publish-win.ps1 -Installer
#endif
#ifndef MyAppSourceDir
  #error MyAppSourceDir must identify the published win-x64 directory
#endif
#ifndef MyAppOutputDir
  #define MyAppOutputDir "..\..\dist"
#endif

#if !FileExists(MyAppSourceDir + "\Portalkeeper.exe") || !FileExists(MyAppSourceDir + "\StormLib.dll")
  #error Publish output must contain Portalkeeper.exe and StormLib.dll
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
; Preserve the 0.1.0 identity, scope, directory and optional shortcuts.
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableDirPage=auto
UninstallLogMode=append
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
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Install the entire published win-x64 payload (Portalkeeper.exe, the .NET
; runtime, StormLib.dll, StormLib.LICENSE.txt, README, LICENSE, config, ...).
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Excludes: "*.realm.conf,settings.json"; Flags: recursesubdirs createallsubdirs ignoreversion
; Only the public sample belongs to the payload; private realm/settings files
; must never become installer-owned files, even when ISCC is invoked directly.
Source: "{#MyAppSourceDir}\config\example.realm.conf"; DestDir: "{app}\config"; Flags: ignoreversion

[InstallDelete]
; Exact obsolete application artifacts only. Never prune {app}, *.dll, config,
; realms, settings, or a WoW directory. Current binaries are replaced by [Files].
Type: files; Name: "{app}\Portalkeeper.pdb"; Check: SafeCleanupPath('Portalkeeper.pdb')
Type: files; Name: "{app}\runtimes\win-x64\native\StormLib.dll"; Check: SafeCleanupPath('runtimes\win-x64\native\StormLib.dll')
Type: files; Name: "{app}\runtimes\win-x64\native\StormLib.LICENSE.txt"; Check: SafeCleanupPath('runtimes\win-x64\native\StormLib.LICENSE.txt')

[Icons]
Name: "{autoprograms}\Portalkeeper"; Filename: "{app}\Portalkeeper.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Portalkeeper.exe"; Comment: "Portalkeeper {#MyAppVersion}"
Name: "{autodesktop}\Portalkeeper"; Filename: "{app}\Portalkeeper.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Portalkeeper.exe"; Comment: "Portalkeeper {#MyAppVersion}"; Tasks: desktopicon

[Run]
Filename: "{app}\Portalkeeper.exe"; Description: "{cm:LaunchProgram,Portalkeeper}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
[Code]
function CleanupFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function SafeCleanupPath(RelativeName: String): Boolean;
var
  Path, Parent: String;
  Attributes: LongWord;
begin
  Result := False;
  { Only fixed application-relative names above call this function. Refuse
    links/junctions anywhere in the path; never follow them into client data. }
  Path := ExpandConstant('{app}') + '\' + RelativeName;
  if not FileExists(Path) then exit;
  while Path <> '' do
  begin
    Attributes := CleanupFileAttributes(Path);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then exit;
    Parent := ExtractFileDir(Path);
    if Parent = Path then break;
    Path := Parent;
  end;
  Result := True;
end;
