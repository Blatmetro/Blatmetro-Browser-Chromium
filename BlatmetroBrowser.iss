#define MyAppName "Blatmetro Browser"
#define MyAppVersion "0.4.7"
#define MyAppPublisher "Blatmetro"

[Setup]
AppId={{A7C79E71-4F53-4A92-8F1B-6C85E38D29A4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\Blatmetro Browser
DefaultGroupName=Blatmetro Browser
OutputDir=installer-output
OutputBaseFilename=BlatmetroBrowser-Setup-0.4.7-x64
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\BlatmetroBrowser.exe

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Blatmetro Browser"; Filename: "{app}\BlatmetroBrowser.exe"
Name: "{autodesktop}\Blatmetro Browser"; Filename: "{app}\BlatmetroBrowser.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BlatmetroBrowser.exe"; Description: "Запустить Blatmetro Browser"; Flags: postinstall nowait skipifsilent
