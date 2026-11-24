; Overseer Windows installer (Inno Setup 6).
; Built by .github/workflows/release.yml:
;   iscc /DAppVersion=0.3.2 /DSourceDir=out\overseer-win-x64 /DOutputDir=dist installer\overseer.iss
; Installs per user (no administrator rights needed) into %LOCALAPPDATA%\Programs\Overseer.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\overseer-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6F2B9C41-8E3D-4F0A-9B57-0D4E1C7A2E19}
AppName=Overseer
AppVersion={#AppVersion}
AppVerName=Overseer {#AppVersion}
AppPublisher=Haidar Saifaldeen
AppPublisherURL=https://github.com/Haidarsf/overseer
AppSupportURL=https://github.com/Haidarsf/overseer/issues
AppUpdatesURL=https://github.com/Haidarsf/overseer/releases
DefaultDirName={localappdata}\Programs\Overseer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Overseer-Setup-{#AppVersion}-x64
SetupIconFile=..\src\Overseer.App\Assets\overseer.ico
UninstallDisplayIcon={app}\Overseer.exe
UninstallDisplayName=Overseer
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start Overseer when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Overseer.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Overseer"; Filename: "{app}\Overseer.exe"
Name: "{autoprograms}\Overseer (demo)"; Filename: "{app}\Overseer.exe"; Parameters: "--demo"; Comment: "Try Overseer on made-up sessions"
Name: "{autodesktop}\Overseer"; Filename: "{app}\Overseer.exe"; Tasks: desktopicon
Name: "{userstartup}\Overseer"; Filename: "{app}\Overseer.exe"; Tasks: startup

[Run]
Filename: "{app}\Overseer.exe"; Description: "Start Overseer"; Flags: nowait postinstall skipifsilent
; after a quiet update started from Overseer (Settings > Updates), start it again
Filename: "{app}\Overseer.exe"; Flags: nowait runasoriginaluser; Check: Relaunch

[UninstallRun]
; remove only the hooks and status line Overseer added to Claude Code's settings.json
Filename: "{app}\Overseer.exe"; Parameters: "--remove-integration"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveIntegration"

[Code]
// Overseer passes /RELAUNCH when it runs this installer to update itself.
function Relaunch: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if (not UninstallSilent) and
       (MsgBox('Also delete Overseer''s own data (~\.overseer: settings, presets, cache, recorded events)?' + #13#10 +
               'Your Claude Code sessions and settings are not affected either way.',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{%USERPROFILE}\.overseer'), True, True, True);
end;
