#ifndef AppVersion
  #error AppVersion is required. Use Build-Installer.ps1.
#endif
#ifndef PayloadDir
  #error PayloadDir is required. Use Build-Installer.ps1.
#endif
#ifndef OutputDir
  #error OutputDir is required. Use Build-Installer.ps1.
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required. Use Build-Installer.ps1.
#endif

[Setup]
AppId={{1B6D4754-0971-4A21-971B-69EB363A2A27}
AppName=ZARA
AppVersion={#AppVersion}
AppPublisher=ZARA
DefaultDirName={autopf}\ZARA
DisableDirPage=auto
UsePreviousAppDir=yes
AllowNetworkDrive=no
AllowUNCPath=no
AllowRootDirectory=no
DefaultGroupName=ZARA
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\src\Zara.Desktop\Assets\ZaraShellIcon.ico
UninstallDisplayIcon={app}\Zara.Desktop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupMutex=ZARA.Installation
Uninstallable=yes
DisableReadyPage=no
SetupLogging=yes
LicenseFile=Terms.ko.txt

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Messages]
WizardLicense=이용약관
LicenseLabel=설치하기 전에 다음 이용약관과 주의사항을 읽어 주십시오.
LicenseLabel3=본 이용약관과 주의사항을 읽고 이해했으며, 이에 동의하는 경우 '동의합니다'를 선택하십시오. 동의하지 않으면 설치를 진행할 수 없습니다.
LicenseAccepted=동의합니다.(&A)
LicenseNotAccepted=동의하지 않습니다.(&D)

[Dirs]
; Prepare creates the protected application directory before Inno copies files.
Name: "{app}"; Flags: uninsalwaysuninstall

[Files]
Source: "Manage-Installation.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The final file callback runs inside Inno's install transaction, so startup failure fails installation.
Source: "Manage-Installation.ps1"; DestDir: "{app}"; Flags: ignoreversion; AfterInstall: StartInstalledService

#ifndef LocalBuild
[InstallDelete]
; A Release update must not retain the identity of an earlier local build.
Type: files; Name: "{app}\local-build.json"
#endif

[Icons]
Name: "{autoprograms}\ZARA"; Filename: "{app}\Zara.Desktop.exe"; WorkingDir: "{app}"

[Code]
var
  Prepared: Boolean;
  Completed: Boolean;
  ServiceStarted: Boolean;
  TransactionId: String;

function InitializeSetup: Boolean;
begin
  Result := not WizardSilent;
  if not Result then begin
    Log('ZARA requires interactive acceptance of the terms. Silent installation is not supported.');
    SuppressibleMsgBox('이용약관에 직접 동의해야 설치할 수 있습니다. 설치파일을 일반 실행하여 진행하십시오.',
      mbError, MB_OK, IDOK);
  end;
end;

function RunManagement(const ScriptPath, Action: String): Boolean;
var
  ResultCode: Integer;
begin
  if TransactionId = '' then
    TransactionId := GetSHA256OfString(ExpandConstant('{tmp}'));
  Result := ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + AddQuotes(ScriptPath) +
    ' -Action ' + Action + ' -InstallDirectory ' + AddQuotes(ExpandConstant('{app}')) +
    ' -TransactionId ' + TransactionId,
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode, nil);
  Result := Result and (ResultCode = 0);
  Log('ZARA installation action ' + Action + ': ' + IntToStr(ResultCode));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousDirectory: String;
begin
  Result := '';
  if Prepared then exit;
  { Also reject /DIR overrides when the directory page is hidden for an update. }
  if RegQueryStringValue(HKLM64,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{1B6D4754-0971-4A21-971B-69EB363A2A27}_is1',
      'Inno Setup: App Path', PreviousDirectory) then
    if CompareText(RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}'))),
        RemoveBackslashUnlessRoot(ExpandFileName(PreviousDirectory))) <> 0 then begin
      Result := '업데이트는 기존 설치 폴더에서 진행해야 합니다. 설치 위치를 바꾸려면 ZARA를 제거한 뒤 다시 설치하십시오.';
      exit;
    end;
  ExtractTemporaryFile('Manage-Installation.ps1');
  if not RunManagement(ExpandConstant('{tmp}\Manage-Installation.ps1'), 'Prepare') then begin
    RunManagement(ExpandConstant('{tmp}\Manage-Installation.ps1'), 'Rollback');
    Result := '설치 폴더를 사용할 수 없거나 기존 ZARA를 안전하게 종료하지 못했습니다. 설치 로그를 확인하십시오.';
    exit;
  end;
  Prepared := True;
end;

procedure StartInstalledService;
begin
  if not RunManagement(ExpandConstant('{tmp}\Manage-Installation.ps1'), 'Start') then
    RaiseException('ZARA를 시작하지 못했습니다. 설치를 취소하고 이전 파일을 복구합니다.');
  ServiceStarted := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then begin
    Completed := ServiceStarted;
    if Completed then
      if not RunManagement(ExpandConstant('{tmp}\Manage-Installation.ps1'), 'Complete') then
        MsgBox('ZARA 설치는 완료되었으나 이전 파일의 백업을 정리하지 못했습니다.', mbError, MB_OK);
  end;
end;

procedure DeinitializeSetup;
begin
  if Prepared and not Completed then
    if not RunManagement(ExpandConstant('{tmp}\Manage-Installation.ps1'), 'Rollback') then
      MsgBox('이전 ZARA를 완전히 복구하지 못했습니다. 백업 파일을 보존했습니다. 다시 설치하기 전에 복구가 필요합니다.', mbError, MB_OK);
end;

function GetCustomSetupExitCode: Integer;
begin
  if Completed then Result := 0 else Result := 1;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    if not RunManagement(ExpandConstant('{app}\Manage-Installation.ps1'), 'Uninstall') then begin
      MsgBox('ZARA를 안전하게 종료하지 못해 제거를 중단했습니다.', mbError, MB_OK);
      Abort;
    end;
end;
