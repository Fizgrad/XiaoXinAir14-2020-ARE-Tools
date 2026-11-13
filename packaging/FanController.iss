#ifndef AppVersion
  #define AppVersion "1.3.1"
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef ArtifactDir
  #error ArtifactDir is required
#endif
#ifndef ChineseMessages
  #error ChineseMessages is required
#endif

[Setup]
AppId={{AD2273CC-8077-4C44-A805-446B62D28DBE}
AppName=FanController
AppVersion={#AppVersion}
AppVerName=FanController {#AppVersion}
AppPublisher=Fizgrad
AppPublisherURL=https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools
AppSupportURL=https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools/issues
AppUpdatesURL=https://github.com/Fizgrad/XiaoXinAir14-2020-ARE-Tools/releases/latest
DefaultDirName={autopf}\FanController
DefaultGroupName=FanController
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ArtifactDir}
OutputBaseFilename=FanController-v{#AppVersion}-win-x64-Setup
SetupIconFile={#SourcePath}..\app.ico
UninstallDisplayIcon={app}\FanController.exe
AppMutex=FanController.SingleInstance.v1
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "chinesesimp"; MessagesFile: "{#ChineseMessages}"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FanController"; Filename: "{app}\FanController.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\FanController"; Filename: "{app}\FanController.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FanController.exe"; Description: "{cm:LaunchProgram,FanController}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\FanController.exe"; Parameters: "--remove-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartupTasks"
