; Inno Setup 6 script. Compiled by the BuildInstaller target in PresetSwitcherForSonar.csproj, which
; passes the three defines below; the defaults only exist so the script also compiles by hand after
; a publish with the FolderProfile.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\PresetSwitcherForSonar\bin\publish\win-x86"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "Preset Switcher for Sonar"
#define AppExe "PresetSwitcherForSonar.exe"
#define AppUrl "https://github.com/Shadow-Hussein/PresetSwitcherForSonar"

[Setup]
; Identifies the product to Windows across versions. Never change it: a new id installs side by side
; instead of upgrading.
AppId={{6C1E6B0B-6C0B-4B63-9E0F-3D8B1B7C52A4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Hussein Mohammed Kazim
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) 2026 Hussein Mohammed Kazim
VersionInfoVersion={#AppVersion}

; Per-user by default, so no UAC prompt: the app itself is asInvoker and writes only to %APPDATA%.
; The dialog still offers an all-users install into Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; The Run value below is deliberately per-user even in an all-users install — the app writes it there.
UsedUserAreasWarning=no
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
MinVersion=10.0

LicenseFile=..\LICENSE
InfoBeforeFile=NOTICE.txt

; The app's single-instance mutex (Local\ is the session namespace, which is where a bare name
; lands too). Setup and uninstall ask for the tray app to be exited rather than replacing a running exe.
; The second name is the one builds before the rename hold, so an upgrade over one still notices it.
AppMutex=PresetSwitcherForSonar.SingleInstance,SonarPresetSwitcher.SingleInstance

SetupIconFile=..\src\PresetSwitcherForSonar\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=PresetSwitcherForSonar-v{#AppVersion}-win-x86-setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The publish folder already carries LICENSE.txt, THIRD-PARTY-NOTICES.md and licenses\ — the
; CopyLicenseNotices target puts them there so every way of shipping the app includes them.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Shortcuts from before the rename (up to 1.4.0), which an upgrade would otherwise leave beside the new ones.
Type: files; Name: "{autoprograms}\Sonar Preset Switcher.lnk"
Type: files; Name: "{autodesktop}\Sonar Preset Switcher.lnk"
; The executable under its old name; the shortcuts and the Run value now point at the new one.
Type: files; Name: "{app}\SonarPresetSwitcher.exe"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; "Start with Windows" is the app's own toggle (StartupRegistration.cs). Setup never creates the
; value; this entry only makes uninstall take it away, so Windows is not left launching a missing exe.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PresetSwitcherForSonar"; Flags: dontcreatekey uninsdeletevalue
; The same value under its name from before the rename, in case the new build never ran to adopt it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SonarPresetSwitcher"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The mic gain feature leaves a Preamp in Equalizer APO's config folder (MicGainService.FileName).
// Without the app nothing would ever take it back out, so uninstall empties the file — emptied rather
// than deleted, because config.txt still includes it. Settings in %APPDATA% are left alone.
procedure EmptyGainFile(const Path: String);
begin
  if FileExists(Path) then
    SaveStringToFile(Path, '', False);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigPath: String;
begin
  if (CurUninstallStep <> usUninstall) or not IsWin64 then
    exit;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\EqualizerAPO', 'ConfigPath', ConfigPath) then
    exit;
  EmptyGainFile(AddBackslash(ConfigPath) + 'PresetSwitcherForSonar.txt');
  // The name from before the rename, in case the new build never ran to adopt it.
  EmptyGainFile(AddBackslash(ConfigPath) + 'SonarPresetSwitcher.txt');
end;
