; Lagoon installer (Inno Setup 6). Build: ISCC.exe installer\Lagoon.iss
; Installs for the current user only (no admin prompt) to %LOCALAPPDATA%\Programs\Lagoon.

#define AppVersion "1.0.0"
#define Src GetEnv("LOCALAPPDATA") + "\LagoonBuild\dist-build\Lagoon.exe"

[Setup]
AppId={{7C3E2A0B-5F1D-4E8A-9B6C-4A2D1E0F7B31}
AppName=Lagoon
AppVersion={#AppVersion}
AppVerName=Lagoon {#AppVersion}
AppPublisher=Lagoon
DefaultDirName={localappdata}\Programs\Lagoon
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=LagoonSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayName=Lagoon
UninstallDisplayIcon={app}\Lagoon.exe
CloseApplications=no

[Messages]
WelcomeLabel2=This will install Lagoon {#AppVersion}: a notch and floating dock for Windows 11.%n%nIf Lagoon is running, it will be closed first.

[Tasks]
Name: "startup"; Description: "Start Lagoon when I sign in"
Name: "desktopicon"; Description: "Desktop shortcut"; Flags: unchecked

[Files]
Source: "{#Src}"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
; same file Lagoon itself keeps in the Start menu, so there is only ever one
Name: "{userprograms}\Lagoon"; Filename: "{app}\Lagoon.exe"; Comment: "Notch and dock"
Name: "{userdesktop}\Lagoon"; Filename: "{app}\Lagoon.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Lagoon.exe"; Parameters: "--enable-startup"; Tasks: startup; Flags: runhidden waituntilterminated
Filename: "{app}\Lagoon.exe"; Description: "Start Lagoon now"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Lagoon"" /F"; Flags: runhidden; RunOnceId: "RemoveStartup"

[UninstallDelete]
Type: files; Name: "{userprograms}\Lagoon.lnk"

[Code]
// Before updating or uninstalling, Lagoon must quit properly (it puts the Windows taskbar and
// notification pop-ups back). "Lagoon.exe --quit" signals it and waits until
// its single-instance mutex is gone. An old build that ignores the signal is simply ended.
function OpenMutexW(Access: Cardinal; Inherit: Boolean; Name: String): THandle; external 'OpenMutexW@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean; external 'CloseHandle@kernel32.dll stdcall';

const
  SYNCHRONIZE = $00100000;

function LagoonRunning(): Boolean;
var
  H: THandle;
begin
  H := OpenMutexW(SYNCHRONIZE, False, 'Lagoon.SingleInstance');
  Result := H <> 0;
  if Result then CloseHandle(H);
end;

// Exe: a Lagoon.exe of this version; "--quit" sends the signal and waits (up to 8 s) for it to go.
procedure QuitLagoon(Exe: String);
var
  I, Code: Integer;
begin
  if not LagoonRunning() then Exit;
  Log('Lagoon is running: asking it to quit');
  Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for I := 1 to 10 do
  begin
    if not LagoonRunning() then Break;
    Sleep(200);
  end;
  if LagoonRunning() then Log('Lagoon still running: ending it') else Log('Lagoon quit');
  if LagoonRunning() then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Lagoon.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if LagoonRunning() then
  begin
    ExtractTemporaryFile('Lagoon.exe'); // the new build knows --quit, whatever version is running
    QuitLagoon(ExpandConstant('{tmp}\Lagoon.exe'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then QuitLagoon(ExpandConstant('{app}\Lagoon.exe'));
end;
