; DeepSeek 划词翻译 — Inno Setup 安装脚本
; 由 scripts\build-installer.ps1 调用，版本号/源目录/输出目录通过 /D 参数传入。

#define MyAppName "DeepSeek 划词翻译"
#define MyAppPublisher "DeepSeek Translator"
#define MyAppExeName "DeepSeekTranslator.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\releases\v0.0.0\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\releases\v0.0.0"
#endif
#ifndef IconFile
  #define IconFile "..\src\DeepSeekTranslator\Assets\app.ico"
#endif

[Setup]
; AppId 决定升级/卸载的识别，必须保持不变
AppId={{8F30CBC1-0A78-4C4D-8A56-FBE1ED673715}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
; 按用户安装：不需要管理员权限，也不会弹 UAC
DefaultDirName={localappdata}\Programs\DeepSeekTranslator
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=DeepSeekTranslator-{#AppVersion}-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 2004 (19041) 及以上
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 程序运行时会把“开机自启”写进 HKCU\...\Run；卸载时清掉它，避免留下失效项
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DeepSeekTranslator"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前结束正在运行的实例，否则文件会被占用
Filename: "{cmd}"; Parameters: "/c taskkill /f /im {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
{ 自动更新：更新程序传入 /RESTARTAPP=1，安装完成后把主程序重新拉起来 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if ExpandConstant('{param:RESTARTAPP|0}') = '1' then
    begin
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--autostart',
        ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;
