; TreeAlarm VMS for Windows. A thin wrapper: it copies the directory scripts/Build-Stage.ps1 laid out
; and runs the PowerShell scripts in it, which do the actual work and can be run by hand as well.
;
;   iscc /DStageDir=<stage> /DAppVersion=<version> /DOutputDir=<dir> ta_vms.iss
;
; Unattended: setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/STORAGE=<dir>] [/LOG=<file>]

#ifndef StageDir
  #error Pass /DStageDir=<directory laid out by Build-Stage.ps1>
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
; Never change: it is how an upgrade finds the installation it replaces.
AppId={{6C2E9B0E-4B7A-4F51-9E0B-7A1C3D5F2E84}
AppName=TreeAlarm VMS
AppVersion={#AppVersion}
AppPublisher=TreeAlarm
AppPublisherURL=https://treealarm.dev
DefaultDirName={autopf}\TreeAlarm VMS
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 / Server 2019: the oldest the bundled runtimes support.
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ta_vms-setup-x64
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=TreeAlarm VMS
CloseApplications=no

[InstallDelete]
; Each component is replaced whole, so that files an older version had and this one does not --
; a previous Erlang runtime, say -- do not linger. Services are stopped by then (PrepareToInstall).
Type: filesandordirs; Name: "{app}\host"
Type: filesandordirs; Name: "{app}\vms"
Type: filesandordirs; Name: "{app}\dotnet"
Type: filesandordirs; Name: "{app}\dapr"
Type: filesandordirs; Name: "{app}\mediamtx"
Type: filesandordirs; Name: "{app}\pgsql"
Type: filesandordirs; Name: "{app}\erlang"
Type: filesandordirs; Name: "{app}\rabbitmq"
Type: filesandordirs; Name: "{app}\redist"
Type: filesandordirs; Name: "{app}\config"
Type: filesandordirs; Name: "{app}\setup"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Also needed before the files are copied, to stop a running installation.
Source: "{#StageDir}\setup\Stop-TaVms.ps1"; Flags: dontcopy

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\setup\Uninstall-TaVms.ps1"" -InstallDir ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveServices"

[Code]
var
  StoragePage: TInputDirWizardPage;
  InstallFailed: Boolean;

function PowerShell: String;
begin
  // The sys constant is the 64-bit System32 in 64-bit install mode; a 32-bit PowerShell would
  // write the RabbitMQ cookie into SysWOW64's profile, where the service never looks.
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function DataDir: String;
begin
  Result := ExpandConstant('{commonappdata}\TreeAlarm\VMS');
end;

function IsUpgrade: Boolean;
begin
  Result := FileExists(DataDir + '\config\site.env');
end;

procedure InitializeWizard;
begin
  StoragePage := CreateInputDirPage(wpSelectDir,
    'Video archive', 'Where should recordings be kept?',
    'Recordings and exported clips go into this folder. Choose a large disk: an archive grows ' +
    'until the retention configured for each camera removes old clips.' + #13#10#13#10 +
    'It can be changed later in site.env in ' + DataDir + '\config.',
    False, '');
  StoragePage.Add('');
  StoragePage.Values[0] := ExpandConstant('{param:STORAGE|' + DataDir + '}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An upgrade keeps the storage path site.env already names. }
  Result := (PageID = StoragePage.ID) and IsUpgrade;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if DirExists(ExpandConstant('{app}')) then
  begin
    ExtractTemporaryFile('Stop-TaVms.ps1');
    if not Exec(PowerShell, '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\Stop-TaVms.ps1') +
        '" -InstallDir "' + ExpandConstant('{app}') + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Result := 'Could not stop the running TreeAlarm VMS services (exit code ' + IntToStr(Code) + ').';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Params: String;
begin
  if CurStep <> ssPostInstall then Exit;
  WizardForm.StatusLabel.Caption := 'Setting up PostgreSQL, RabbitMQ and the TreeAlarm VMS service...';
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\setup\Install-TaVms.ps1') +
    '" -InstallDir "' + ExpandConstant('{app}') + '" -DataDir "' + DataDir + '"';
  if not IsUpgrade then
    Params := Params + ' -StoragePath "' + StoragePage.Values[0] + '"';
  if not Exec(PowerShell, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
  begin
    InstallFailed := True;
    SuppressibleMsgBox('Setting up the services failed (exit code ' + IntToStr(Code) + '). ' +
      'The log is in ' + DataDir + '\install-logs.', mbCriticalError, MB_OK, IDOK);
  end;
end;

{ Lets an unattended install (and CI) see that the post-install step failed. }
function GetCustomSetupExitCode: Integer;
begin
  if InstallFailed then Result := 10 else Result := 0;
end;
