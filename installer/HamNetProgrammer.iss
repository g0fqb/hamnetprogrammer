; Inno Setup script for HamNetProgrammer. Not run directly - see installer\build.ps1, which
; publishes a fresh self-contained build first (using the exact flags this WinUI3 app shape needs -
; see HamNetProgrammer.Desktop.csproj's comments) and passes the version through via /DMyAppVersion,
; so this script never has its own copy of the version number to drift out of sync.
;
; Per-user install (PrivilegesRequired=lowest, installs under %LocalAppData%) deliberately avoids
; requiring admin/UAC - the audience here isn't all technical, and "download, double-click, Next
; a few times" with no elevation prompt is the point of this whole installer, replacing a
; download-zip/unzip/find-the-right-exe flow.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppName "HamNetProgrammer"
#define MyAppPublisher "HamNetProgrammer"
#define MyAppURL "https://github.com/g0fqb/hamnetprogrammer"
#define MyAppExeName "HamNetProgrammer.Desktop.exe"
#define PublishDir "..\src\HamNetProgrammer.Desktop\bin\Publish"

[Setup]
AppId={{B2C3D4E5-F6A7-8901-BCDE-F12345678901}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=HamNetProgrammer-Setup-{#MyAppVersion}
SetupIconFile=..\src\HamNetProgrammer.Desktop\Assets\Logo\hnp_logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
