
#define MyAppName "OPPO 解锁工具箱"
#define MyAppNameEn "OPPOUnlockToolbox"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "diyiqiuye"
#define MyAppExeName "OPPO解锁工具箱.exe"
#define MyAppUrl "https://github.com/diyiqiuye/fx5p-resources"

[Setup]
AppId={{EB482A61-2A52-499F-BCE8-AF0F6F2B1643}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
DefaultDirName={localappdata}\Programs\{#MyAppNameEn}
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\..\dist-cs\installer
OutputBaseFilename=OPPO解锁工具箱-{#MyAppVersion}-setup
SetupIconFile=..\OppoUnlockToolbox\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=no
MinVersion=6.3

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Files]
Source: "..\..\dist-cs\OPPO解锁工具箱\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; net48 是依赖框架发布，这几个依赖 dll 和 exe.config 必须跟着 exe 一起装
Source: "..\..\dist-cs\OPPO解锁工具箱\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\dist-cs\OPPO解锁工具箱\*.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\..\dist-cs\OPPO解锁工具箱\resources.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\dist-cs\OPPO解锁工具箱\resources\*"; DestDir: "{app}\resources"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\dist-cs\OPPO解锁工具箱\tools\platform-tools\*"; DestDir: "{app}\tools\platform-tools"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM ""{#MyAppExeName}"" /IM adb.exe"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]

[Code]
// .NET Framework 4.8 是 Win10 1903+ / Win11 的自带组件；老系统上先提示去装，别让用户
// 双击之后收到一个看不懂的运行时错误。Release >= 528040 即 4.8 及以上。
function IsDotNet48OrLater(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
                               'Release', Release) and (Release >= 528040);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  if not IsDotNet48OrLater() then
  begin
    if MsgBox('这个工具箱需要 .NET Framework 4.8，Windows 10 1903 以上和 Windows 11 都自带。'
              + #13#10 + #13#10 + '要现在打开微软的下载页吗？', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48',
                '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
    Exit;
  end;
  Result := True;
end;

// 安装前先停掉正在运行的实例，避免文件被占用
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM "{#MyAppExeName}" >nul 2>&1', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM adb.exe >nul 2>&1', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
end;
