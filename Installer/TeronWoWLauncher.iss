; Inno Setup script for Teron WoW Launcher.
;
; Normally you don't need to run this directly: publishing the win-x86 profile (via
; "dotnet publish -p:PublishProfile=win-x86" or Visual Studio's Publish dialog) builds this
; automatically as a post-publish MSBuild step - see the BuildInnoSetupInstaller target in
; TeronWoWLauncher.csproj. The lines below are only needed to run it manually:
;   dotnet publish -p:PublishProfile=win-x86 -c Release
;   ISCC TeronWoWLauncher.iss
;
; Output goes to bin\InstallerPackage\TeronWoWLauncherSetup-x86.exe
;
; x86 only, deliberately: WoW.exe (1.12.1) is a 32-bit binary and this launcher's in-process DLL
; injection is only valid when both processes share the same bitness - see the PlatformTarget
; comment in TeronWoWLauncher.csproj. There is no x64 build of this app to package.

#ifndef MyAppVersion
  #define MyAppVersion "2.0.0-beta.3"
#endif

#define MyAppName "Teron WoW Launcher"
#define MyAppPublisher "Teronverse"
#define MyAppExeName "TeronWoWLauncher.exe"
#define MyPublishDir "..\Build\Publish\TeronWoWLauncher\win-x86"

[Setup]
AppId={{F8EDD851-8B53-4896-AF09-BDBE21CA8191}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; No default suggested folder: this launcher is designed to live inside an existing World of
; Warcraft install (next to WoW.exe), not in Program Files - the wizard's directory page (left
; enabled below) is where the user picks that folder, with wording that says so explicitly.
DefaultDirName={autopf}\TeronWoWLauncher
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
SetupIconFile=..\game.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\bin\InstallerPackage
OutputBaseFilename=TeronWoWLauncherSetup-x86
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x86compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SelectDirDesc=Where is your World of Warcraft (1.12.1) folder?
SelectDirLabel3=Setup will install {#MyAppName} into the folder that contains your WoW.exe - browse to it below (not a Program Files location). This is required for the launcher to find and patch the game.

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Additional shortcuts:"
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
