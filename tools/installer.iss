; ============================================================
;  AirPlayer installer script (for Inno Setup 6)
;
;  Steps:
;   1) From the repo root, run:
;      powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 1.0.0
;      This produces publish\AirPlayer-1.0.0-win-x64\ (self-contained, runnable).
;   2) Install Inno Setup (https://jrsoftware.org/isdl.php), open this file in it,
;      and click "Compile".
;   3) The output is publish\AirPlayer-1.0.0-setup.exe -- that's the installer.
;
;  To change version, edit MyAppVersion below (must match build-release.ps1 -Version).
;
;  NOTE: this file is kept ASCII-only to avoid code-page / BOM issues.
; ============================================================

#define MyAppName "AirPlayer"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "AirPlayer"
#define MyAppExeName "AirPlayer.App.exe"
; Release output folder (from build-release.ps1), relative to this tools\ folder
#define SourceDir "..\publish\AirPlayer-" + MyAppVersion + "-win-x64"

[Setup]
; AppId uniquely identifies the app. Keep it the SAME across versions (changing it
; makes Windows treat upgrades as a different product).
AppId={{B7E5B0A1-4C3D-4E2A-9F1B-2A6C3D4E5F60}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\publish
OutputBaseFilename=AirPlayer-{#MyAppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; 64-bit app: install under the 64-bit Program Files.
; x64compatible = x64 systems AND ARM64 systems that can run x64 via emulation
; (requires Inno Setup 6.3+). Clears the "x64 is deprecated" warning.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installing under Program Files requires admin
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Pack the whole release folder (exe, runtime, fdk-aac.dll, Assets) as-is
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[Code]
// Write an install marker so the app can tell an installed copy from a portable
// (zip-extracted) copy, and pick the matching update asset (setup.exe vs zip).
// Only the installer writes this; it is NOT shipped in the zip, so portable
// installs never carry the marker. Written post-install so upgrades preserve it.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveStringToFile(ExpandConstant('{app}\installed.marker'), '1', False);
end;
