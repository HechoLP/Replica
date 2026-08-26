#ifndef ReplicaVersion
  #define ReplicaVersion "0.1.0-alpha.1"
#endif

#ifndef ReplicaFileVersion
  #define ReplicaFileVersion "0.1.0.0"
#endif

#ifndef PublishDirectory
  #define PublishDirectory "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDirectory
  #define OutputDirectory "..\artifacts\release"
#endif

#define ReplicaAppId "{{D47998AB-601B-45CA-A594-5289531AD742}"
#define ReplicaRepositoryUrl "https://github.com/HechoLP/Replica"
#define ReplicaReleasesUrl "https://github.com/HechoLP/Replica/releases"

[Setup]
AppId={#ReplicaAppId}
AppName=Replica
AppVersion={#ReplicaVersion}
#ifdef ReplicaSigningEnabled
AppVerName=Replica {#ReplicaVersion}
#else
AppVerName=Replica {#ReplicaVersion} (Unsigned)
#endif
AppPublisher=HechoLP
AppPublisherURL={#ReplicaRepositoryUrl}
AppSupportURL={#ReplicaRepositoryUrl}/issues
AppUpdatesURL={#ReplicaReleasesUrl}
VersionInfoVersion={#ReplicaFileVersion}
VersionInfoCompany=HechoLP
#ifdef ReplicaSigningEnabled
VersionInfoDescription=Replica Windows installer
#else
VersionInfoDescription=Replica Windows installer (Unsigned)
#endif
VersionInfoProductName=Replica
VersionInfoProductVersion={#ReplicaFileVersion}
DefaultDirName={localappdata}\Programs\Replica
DefaultGroupName=Replica
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDirectory}
OutputBaseFilename=ReplicaSetup-{#ReplicaVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no
ChangesAssociations=yes
UsePreviousAppDir=yes
#ifdef ReplicaSigningEnabled
UninstallDisplayName=Replica {#ReplicaVersion}
#else
UninstallDisplayName=Replica {#ReplicaVersion} (Unsigned)
#endif
UninstallDisplayIcon={app}\Replica.exe
#ifdef ReplicaSigningEnabled
SignTool=replica
SignedUninstaller=yes
#else
InfoBeforeFile=UNSIGNED.txt
SignedUninstaller=no
#endif

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 Replica 아이콘 만들기"; GroupDescription: "추가 아이콘:"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\Replica.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Replica"; Filename: "{app}\Replica.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Replica"; Filename: "{app}\Replica.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.replica"; ValueType: string; ValueName: ""; ValueData: "Replica.Snapshot"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Replica.Snapshot"; ValueType: string; ValueName: ""; ValueData: "Replica Snapshot"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Replica.Snapshot\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Replica.exe,0"
Root: HKA; Subkey: "Software\Classes\Replica.Snapshot\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Replica.exe"" --open-snapshot ""%1"""

[Run]
Filename: "{app}\Replica.exe"; Description: "Replica 실행"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteReplicaUserData: Boolean;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  DeleteReplicaUserData := False;

  if UninstallSilent then
    Exit;

  if MsgBox(
       'Snapshot, History, 설정과 복구 기록을 포함한 Replica 사용자 데이터를 완전히 삭제하시겠습니까?' + #13#10 + #13#10 +
       '기본값은 데이터 유지입니다.',
       mbConfirmation,
       MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    Exit;

  DeleteReplicaUserData :=
    MsgBox(
      '이 작업은 되돌릴 수 없습니다. %LOCALAPPDATA%\Replica의 모든 데이터를 삭제할까요?',
      mbError,
      MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and DeleteReplicaUserData then
    DelTree(ExpandConstant('{localappdata}\Replica'), True, True, True);
end;
