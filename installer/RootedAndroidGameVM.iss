#define AppName "Rooted Android Game VM"
#define AppVersion "0.1.0"
#define Publisher "RootedAndroidGameVM contributors"

[Setup]
AppId={{2B456CBE-77EC-4F4B-911A-32D78A42F287}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={localappdata}\Programs\RootedAndroidGameVM
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release
OutputBaseFilename=RootedAndroidGameVM-Setup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayIcon={app}\RootedAndroidGameVM.exe
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#Publisher}
VersionInfoDescription={#AppName} installer

[Files]
Source: "..\artifacts\publish\Launcher\RootedAndroidGameVM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\Setup\RootedAndroidGameVM.Setup.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\配置 Rooted Android Game VM"; Filename: "{app}\RootedAndroidGameVM.Setup.exe"

[Code]
var
  UninstallMode: Integer;

function InitializeUninstall(): Boolean;
var
  Answer: Integer;
begin
  Answer := MsgBox(
    '请选择卸载范围：' + #13#10 + #13#10 +
    '“否”＝仅删除程序，保留安卓虚拟机和数据。' + #13#10 +
    '“是”＝继续选择是否删除安卓虚拟机。' + #13#10 +
    '“取消”＝退出卸载。',
    mbConfirmation, MB_YESNOCANCEL);
  if Answer = IDCANCEL then
  begin
    Result := False;
    exit;
  end;
  if Answer = IDNO then
  begin
    UninstallMode := 0;
    Result := True;
    exit;
  end;

  Answer := MsgBox(
    '是否完全删除 RootedAndroidGameVM 的 SDK、缓存、设置和安卓虚拟机？' + #13#10 + #13#10 +
    '“否”＝删除虚拟机与运行环境，但保留下载缓存和配置。' + #13#10 +
    '“是”＝完全删除约 10 GB 或更多本地数据。' + #13#10 +
    '你主动导出到其他文件夹的数据永远不会被删除。',
    mbConfirmation, MB_YESNO);
  if Answer = IDYES then
    UninstallMode := 2
  else
    UninstallMode := 1;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LegacyAvd: String;
  ResultCode: Integer;
begin
  if (CurUninstallStep = usUninstall) and (UninstallMode >= 1) then
  begin
    Exec(
      ExpandConstant('{localappdata}\RootedAndroidGameVM\runtime\android-sdk\platform-tools\adb.exe'),
      '-s emulator-5554 emu kill', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(3000);
  end;

  if CurUninstallStep <> usPostUninstall then exit;

  DeleteFile(ExpandConstant('{userdesktop}\Rooted Android Game VM.lnk'));
  DelTree(ExpandConstant('{userprograms}\Rooted Android Game VM'), True, True, True);

  if UninstallMode >= 1 then
  begin
    DelTree(ExpandConstant('{localappdata}\RootedAndroidGameVM\runtime'), True, True, True);
    LegacyAvd := ExpandConstant('{userprofile}\.android\avd\arcaea_root_api35.avd');
    DelTree(LegacyAvd, True, True, True);
    DeleteFile(ExpandConstant('{userprofile}\.android\avd\arcaea_root_api35.ini'));
  end;

  if UninstallMode = 2 then
    DelTree(ExpandConstant('{localappdata}\RootedAndroidGameVM'), True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) then
  begin
    if not Exec(
      ExpandConstant('{app}\RootedAndroidGameVM.Setup.exe'),
      '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
      RaiseException('无法启动 Rooted Android Game VM 图形配置程序。');
    if ResultCode <> 0 then
      RaiseException('安卓虚拟机配置未完成。未创建日常启动快捷方式，请重新运行安装包。');
  end;
end;
