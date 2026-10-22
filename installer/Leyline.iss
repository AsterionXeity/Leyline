; Leyline installer (Inno Setup 6).
;
; Packs the published app (including its Driver\ folder with the Leyline Virtual Audio driver package) into a
; single Leyline-Setup.exe. The driver itself is installed by Leyline: on first launch, and after an update that
; ships a newer driver, Leyline shows its driver prompt and installs it with one administrator approval.
;
; Build (CI does this):
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=<publish folder> /DOutputDir=<output folder> installer\Leyline.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Leyline\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Keep AppId fixed: it makes new versions install over (upgrade) the existing one.
AppId={{6F7A0C3E-5B1D-4E2A-9C8F-1D4B7E9A2C51}
AppName=Leyline
AppVersion={#AppVersion}
AppVerName=Leyline {#AppVersion}
AppPublisher=Leyline
AppPublisherURL=https://github.com/AsterionXeity/Leyline
AppSupportURL=https://github.com/AsterionXeity/Leyline/issues
DefaultDirName={autopf}\Leyline
DefaultGroupName=Leyline
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Leyline-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Leyline.exe
SetupIconFile=..\src\Leyline\Assets\Leyline.ico
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Leyline"; Filename: "{app}\Leyline.exe"
Name: "{group}\Uninstall Leyline"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Leyline"; Filename: "{app}\Leyline.exe"; Tasks: desktopicon

[Run]
; Leyline checks its driver on launch and prompts to install or update it.
Filename: "{app}\Leyline.exe"; Description: "{cm:LaunchProgram,Leyline}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the Leyline Virtual Audio device and driver package (the uninstaller already runs elevated).
Filename: "{app}\Leyline.exe"; Parameters: "--uninstall-driver"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLeylineDriver"
