; Mystic web installer (Inno Setup 6): a small setup.exe that downloads the app during installation.
; Built by installer\release.ps1, which passes the download URL and the zip's SHA-256:
;   ISCC.exe /DPayloadUrl=https://... /DPayloadSha256=... installer\MysticWeb.iss
; The download is refused unless its SHA-256 matches, so a corrupted or swapped file is never installed.

#define AppName "Mystic"
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif
#ifndef PayloadUrl
  #error Pass /DPayloadUrl=<https URL of Mystic-<version>.zip> (see installer\release.ps1)
#endif
#ifndef PayloadSha256
  #error Pass /DPayloadSha256=<sha256 of the zip> (see installer\release.ps1)
#endif
#define PayloadName "Mystic-" + AppVersion + ".zip"

[Setup]
AppId={{8F6C2A4B-3D1E-4B7A-9C55-6A1F2E0D4B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Mystic
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\build\installer
OutputBaseFilename=MysticWebSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\Mystic.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "firewall"; Description: "Allow Mystic through Windows Firewall (needed for iPhone tracking and VMC)"; GroupDescription: "Network:"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Mystic.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Mystic.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Mystic"" dir=in action=allow program=""{app}\Mystic.exe"" protocol=UDP localport=39539,39572 enable=yes profile=private,public"; Flags: runhidden; Tasks: firewall
Filename: "{app}\Mystic.exe"; Description: "Start Mystic"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Mystic"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
; The app is unpacked from the download rather than listed in [Files], so remove its folder explicitly.
; User models and settings (Documents\Mystic, AppData) are kept.
Type: filesandordirs; Name: "{app}"

[Code]
var
  DownloadPage: TDownloadWizardPage;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading Mystic...', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    DownloadPage.Clear;
    // Inno verifies the SHA-256 itself and fails the download if it doesn't match.
    DownloadPage.Add('{#PayloadUrl}', '{#PayloadName}', '{#PayloadSha256}');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Log('Download aborted by user.')
        else
          SuppressibleMsgBox('Could not download Mystic:' + #13#10 + GetExceptionMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    ForceDirectories(ExpandConstant('{app}'));
    // Windows 10+ ships tar.exe, which unpacks .zip files quickly.
    if not Exec(ExpandConstant('{sys}\tar.exe'),
                '-xf "' + ExpandConstant('{tmp}\{#PayloadName}') + '" -C "' + ExpandConstant('{app}') + '"',
                '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      RaiseException('Could not unpack Mystic (tar exit code ' + IntToStr(ResultCode) + ').');
  end;
end;
