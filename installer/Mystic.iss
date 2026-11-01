; Mystic installer (Inno Setup 6). Compile after "Mystic > Build Windows App" in Unity has produced build\Mystic:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\Mystic.iss
; Output: build\installer\MysticSetup-<version>.exe

#define AppName "Mystic"
#define AppVersion "0.5.0"
#define BuildDir "..\build\Mystic"

[Setup]
AppId={{8F6C2A4B-3D1E-4B7A-9C55-6A1F2E0D4B11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Mystic
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\build\installer
OutputBaseFilename=MysticSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Admin: installs to Program Files and can add the firewall rule for iPhone tracking / VMC.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\Mystic.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "firewall"; Description: "Allow Mystic through Windows Firewall (needed for iPhone tracking and VMC)"; GroupDescription: "Network:"

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Mystic.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Mystic.exe"; Tasks: desktopicon

[Run]
; Only the two UDP ports Mystic listens on from the network: 39539 (VMC) and 39572 (iPhone / VTube Studio replies).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Mystic"" dir=in action=allow program=""{app}\Mystic.exe"" protocol=UDP localport=39539,39572 enable=yes profile=private,public"; Flags: runhidden; Tasks: firewall
Filename: "{app}\Mystic.exe"; Description: "Start Mystic"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Mystic"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"
