; Inno Setup script for Diktat for Windows. Built by .github/workflows/release.yml:
;   iscc /DAppVersion=0.1.0 /DSourceDir=..\publish\win-x64 installer\Diktat.iss
; Installs per user (no admin rights needed) into %LOCALAPPDATA%\Programs\Diktat.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

[Setup]
AppId={{8C1B7A52-3E0F-4C8E-9D4B-6F2A1D7E5B90}
AppName=Diktat
AppVersion={#AppVersion}
AppPublisher=MProductions GmbH
AppPublisherURL=https://github.com/MProductionsGmbH/diktat-app-win
DefaultDirName={localappdata}\Programs\Diktat
DefaultGroupName=Diktat
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Diktat-Setup
SetupIconFile=..\src\Diktat.Windows\Assets\Diktat.ico
UninstallDisplayIcon={app}\Diktat.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Diktat beim Anmelden starten"; GroupDescription: "Start:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Diktat"; Filename: "{app}\Diktat.exe"
Name: "{autodesktop}\Diktat"; Filename: "{app}\Diktat.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Diktat"; ValueData: """{app}\Diktat.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Diktat.exe"; Description: "{cm:LaunchProgram,Diktat}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/IM Diktat.exe /F"; Flags: runhidden; RunOnceId: "KillDiktat"
