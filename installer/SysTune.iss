; SysTune installer (Inno Setup 6). Per-user install: no administrator rights needed.
; Build: ISCC /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=..\artifacts\publish\win-x64 /O..\artifacts\dist installer\SysTune.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-" + Arch
#endif

[Setup]
AppId={{8C1F4E52-6B0A-4F3E-9C51-2D6E7A9B3F10}
AppName=SysTune
AppVersion={#AppVersion}
AppVerName=SysTune {#AppVersion}
AppPublisher=SysTune contributors
DefaultDirName={localappdata}\Programs\SysTune
DefaultGroupName=SysTune
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=SysTune-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\src\SysTune.App\Assets\SysTune.ico
UninstallDisplayIcon={app}\SysTune.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
; Close a running instance before upgrading/uninstalling.
AppMutex=Local\SysTune.SingleInstance.6F1C2B7E
CloseApplications=yes

[Languages]
#ifexist "ChineseTraditional.isl"
Name: "zh_TW"; MessagesFile: "ChineseTraditional.isl"
#endif
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\SysTune"; Filename: "{app}\SysTune.exe"
Name: "{group}\{cm:UninstallProgram,SysTune}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\SysTune"; Filename: "{app}\SysTune.exe"; Tasks: desktopicon

[Registry]
; The app manages the autostart value itself; make sure uninstall removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "SysTune"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\SysTune.exe"; Description: "{cm:LaunchProgram,SysTune}"; Flags: nowait postinstall skipifsilent
