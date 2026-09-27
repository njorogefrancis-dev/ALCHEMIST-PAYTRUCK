
; ============================================================================
; Alchemist PayTrack - Inno Setup Installer Script
; ============================================================================
; HOW TO USE:
;   1. Install Inno Setup (free): https://jrsoftware.org/isdl.php
;   2. Run build.bat first, so publish\AlchemistPayTrack.exe exists.
;   3. Open this file (installer.iss) in Inno Setup and click Compile
;      (or run from the command line: ISCC.exe installer.iss)
;   4. The finished installer is written to installer_output\
;      AlchemistPayTrackSetup.exe
; ============================================================================

#define MyAppName "Alchemist PayTrack"
#define MyAppVersion "1.5.0"
#define MyAppPublisher "Alchemist PayTrack"
#define MyAppExeName "AlchemistPayTrack.exe"
#define MyAppIcon "AlchemistPayTrack\Assets\AppIcon.ico"

[Setup]
AppId={{B7E4C9A1-3F2D-4E5A-9C8B-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#MyAppIcon}
OutputDir=installer_output
OutputBaseFilename=AlchemistPayTrackSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Standalone single-file publish: only the exe is required.
Source: "publish\AlchemistPayTrack.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
