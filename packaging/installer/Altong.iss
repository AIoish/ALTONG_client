; 알통 설치 파일. 직접 컴파일하지 말고 tools/build-installer.ps1로 만든다.
; 빌드 스크립트가 아래 값을 /D로 넘긴다.
;   AppVersion      예: 1.0.0
;   PublishDir      dotnet publish 결과 폴더 (Altong.msix 포함)
;   CertFile        패키지 서명 인증서(.cer)
;   CertThumbprint  위 인증서의 지문 (제거할 때 씀)
;   OutputDir       설치 파일을 만들 폴더
;   RepoRoot        저장소 루트

#ifndef AppVersion
  #error tools/build-installer.ps1로 빌드하세요.
#endif

[Setup]
AppId={{6E0B7C3A-5D1F-4C8E-9A42-1F3B8D7E2A90}
AppName=Altong
AppVersion={#AppVersion}
AppVerName=Altong {#AppVersion}
AppPublisher=AIoish
AppPublisherURL=https://github.com/AIoish/ALTONG_client
DefaultDirName={autopf}\Altong
DisableProgramGroupPage=yes
; 패키지 서명 인증서를 컴퓨터에 신뢰시키려면 관리자 권한이 필요하다.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; sparse 패키지 매니페스트의 최소 버전(Windows 10 2004)과 같다.
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=AltongSetup-{#AppVersion}
SetupIconFile={#RepoRoot}\src\Altong.Client\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Altong.Client.exe
UninstallDisplayName=Altong
WizardStyle=modern
; 모델 파일(약 1GB)은 이미 압축된 형태라 더 줄지 않는다. 빠른 압축만 쓴다.
Compression=lzma2/fast
SolidCompression=no
CloseApplications=force
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked
Name: "kakao"; Description: "카카오톡 알림 연동 켜기 (실험 기능: 카카오톡 팝업을 화면에서 읽어 옵니다)"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\packaging\installer\register-package.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#CertFile}"; DestDir: "{tmp}"; DestName: "Altong.cer"; Flags: deleteafterinstall

[Icons]
; 시작 메뉴 항목은 패키지 등록으로 생긴다. 여기서 또 만들면 두 개가 된다.
Name: "{autodesktop}\Altong"; Filename: "{app}\Altong.Client.exe"; Tasks: desktopicon

[Run]
; 설치 프로그램은 관리자 권한이라, 앱은 원래 사용자 권한으로 띄운다.
Filename: "{app}\Altong.Client.exe"; Description: "Altong 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser; Tasks: not kakao
; 방금 켠 환경 변수는 이미 떠 있는 설치 프로그램에 반영되지 않아, 처음 실행할 때만 직접 넘긴다.
Filename: "{cmd}"; Parameters: "/c set ALTONG_ENABLE_KAKAO=1&& start """" ""{app}\Altong.Client.exe"""; Description: "Altong 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser runhidden; Tasks: kakao

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/im Altong.Client.exe /f"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name Altong.Client | Remove-AppxPackage"""; Flags: runhidden; RunOnceId: "RemovePackage"
Filename: "{sys}\certutil.exe"; Parameters: "-delstore TrustedPeople {#CertThumbprint}"; Flags: runhidden; RunOnceId: "RemoveCert"
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Altong /f"; Flags: runhidden; RunOnceId: "RemoveStartup"
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Environment /v ALTONG_ENABLE_KAKAO /f"; Flags: runhidden; RunOnceId: "RemoveKakaoFlag"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  { 1. 패키지 서명 인증서를 이 컴퓨터에서 신뢰하게 한다 (관리자 권한). }
  if not Exec(ExpandConstant('{sys}\certutil.exe'),
      '-f -addstore TrustedPeople "' + ExpandConstant('{tmp}\Altong.cer') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    MsgBox('패키지 인증서를 등록하지 못했습니다(' + IntToStr(ResultCode) + ').' + #13#10 +
      '앱은 실행되지만 Windows 알림을 읽지 못합니다.', mbError, MB_OK);

  { 2. sparse 패키지를 원래 사용자에게 등록한다. }
  if not ExecAsOriginalUser('powershell.exe',
      '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\register-package.ps1') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    MsgBox('알림 권한용 패키지를 등록하지 못했습니다(' + IntToStr(ResultCode) + ').' + #13#10 +
      '앱은 실행되지만 Windows 알림을 읽지 못합니다.' + #13#10 +
      '자세한 내용: %TEMP%\altong-register-package.log', mbError, MB_OK);

  { 3. 카카오톡 연동 (Release 빌드는 기본으로 꺼져 있다). }
  if WizardIsTaskSelected('kakao') then
    ExecAsOriginalUser(ExpandConstant('{sys}\setx.exe'), 'ALTONG_ENABLE_KAKAO 1', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  else
    ExecAsOriginalUser(ExpandConstant('{sys}\reg.exe'), 'delete HKCU\Environment /v ALTONG_ENABLE_KAKAO /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
