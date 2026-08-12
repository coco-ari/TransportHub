#define AppName "TransportHub"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef RepositoryRoot
  #error RepositoryRoot must be supplied to ISCC.
#endif
#ifndef OutputDirectory
  #error OutputDirectory must be supplied to ISCC.
#endif

[Setup]
AppId={{8FE53DC7-0DDD-48C0-B8C1-E99EB4CE32B7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=TransportHub
AppPublisherURL=https://github.com/coco-ari/TransportHub
AppSupportURL=https://github.com/coco-ari/TransportHub/issues
AppUpdatesURL=https://github.com/coco-ari/TransportHub/releases
DefaultDirName={localappdata}\Programs\TransportHub
DefaultGroupName=TransportHub
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDirectory}
OutputBaseFilename=TransportHub-Setup-v{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayIcon={app}\TransportHub.exe
UninstallDisplayName=TransportHub
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany=TransportHub
VersionInfoDescription=TransportHub Windows installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#RepositoryRoot}\artifacts\TransportHub.Desktop\TransportHub.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepositoryRoot}\artifacts\TransportHub.Desktop\TransportHub.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepositoryRoot}\scripts\bootstrap.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#RepositoryRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepositoryRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{app}\TransportHub.pdb"
Type: files; Name: "{app}\install-manifest.json"

[Icons]
Name: "{autoprograms}\TransportHub"; Filename: "{app}\TransportHub.exe"; WorkingDir: "{app}"; Comment: "TransportHub desktop transfer window"
Name: "{autodesktop}\TransportHub"; Filename: "{app}\TransportHub.exe"; WorkingDir: "{app}"; Comment: "TransportHub desktop transfer window"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TransportHub"; ValueData: """{app}\TransportHub.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\TransportHub.exe"; Description: "启动 TransportHub"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  PowerShellPath: String;
  BootstrapPath: String;
  Parameters: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  WizardForm.StatusLabel.Caption := '正在安装并配置 Syncthing...';
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  BootstrapPath := ExpandConstant('{app}\scripts\bootstrap.ps1');
  Parameters := '-NoProfile -ExecutionPolicy Bypass -File "' + BootstrapPath + '"';

  if not Exec(PowerShellPath, Parameters, '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode) then
    RaiseException('无法启动 Syncthing 配置程序。');

  if ResultCode <> 0 then
    RaiseException('Syncthing 安装或配置失败（退出代码 ' +
      IntToStr(ResultCode) + '）。请检查网络和 winget 后重试。');
end;
