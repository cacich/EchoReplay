#ifndef AppVersion
  #error AppVersion must be passed by build-release.ps1
#endif
#ifndef PayloadDir
  #error PayloadDir must be passed by build-release.ps1
#endif
#ifndef ReleaseDir
  #error ReleaseDir must be passed by build-release.ps1
#endif

[Setup]
AppId={{36E87E05-473D-4A07-AF16-44E831245437}
AppName=EchoReplay
AppVersion={#AppVersion}
AppVerName=EchoReplay {#AppVersion}
AppPublisher=cacich
AppPublisherURL=https://github.com/cacich/EchoReplay
AppSupportURL=https://github.com/cacich/EchoReplay/issues
AppUpdatesURL=https://github.com/cacich/EchoReplay/releases/latest
DefaultDirName={localappdata}\Programs\EchoReplay
DefaultGroupName=EchoReplay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir={#ReleaseDir}
OutputBaseFilename=EchoReplay-Setup-x64
SetupIconFile=..\src\EchoReplay\app.ico
UninstallDisplayIcon={app}\EchoReplay.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
AppMutex=Local\EchoReplay.Setup
UninstallDisplayName=EchoReplay
VersionInfoVersion={#AppVersion}
VersionInfoDescription=EchoReplay Windows installer

[Languages]
Name: "chinesetraditional"; MessagesFile: "Languages\ChineseTraditional.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
chinesetraditional.SetupAppRunningError=EchoReplay 仍在背景執行。%n%n請先儲存需要的音訊，再從系統匣右鍵選單選擇「結束 EchoReplay」，然後繼續安裝。
chinesetraditional.UninstallAppRunningError=EchoReplay 仍在背景執行。%n%n請先儲存需要的音訊，再從系統匣右鍵選單選擇「結束 EchoReplay」，然後繼續解除安裝。
english.SetupAppRunningError=EchoReplay is still running in the background.%n%nSave any audio you need, then choose Exit from the system tray menu before continuing.
english.UninstallAppRunningError=EchoReplay is still running in the background.%n%nSave any audio you need, then choose Exit from the system tray menu before continuing.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\EchoReplay"; Filename: "{app}\EchoReplay.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\EchoReplay"; Filename: "{app}\EchoReplay.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\EchoReplay.exe"; Description: "{cm:LaunchProgram,EchoReplay}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  OwnCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Only remove startup when it points to this installation. Another
    // portable copy may own the same application startup preference.
    OwnCommand := '"' + ExpandConstant('{app}\EchoReplay.exe') + '" --background';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'EchoReplay', Command) then
      if CompareText(Command, OwnCommand) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'EchoReplay');
  end;
end;
