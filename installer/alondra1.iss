; Alondra1 setup. Compile with Inno Setup 6 (ISCC.exe).

; Two builds from one script (completion spec v1.1 §8.2): prod (default) and dev (ISCC /DDev). The dev setup has its own
; AppId, name, folder and output file, so it installs next to prod and never upgrades or removes it.
#define MyAppVersion "1.3.0"
#define MyAppExeName "Alondra1.exe"
#ifdef Dev
  #define MyAppName "Alondra1 Dev"
  #define MyAppId "{{3C7D9B52-0E61-4A1F-B8C4-6D2E9F1A7B30}"
  #define MyOutput "Alondra1-dev-setup"
  #define MySource "..\artifacts\win-x64-dev"
#else
  #define MyAppName "Alondra1"
  ; Keep this id: it is what lets a newer setup upgrade an existing Alondra1 installation in place.
  #define MyAppId "{{8F4C2A91-6B1E-4D77-9C3A-1E5B7A0D4F22}"
  #define MyOutput "Alondra1-setup"
  #define MySource "..\artifacts\win-x64"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppName}
VersionInfoVersion=1.3.0.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion=1.3.0.0
VersionInfoDescription=Alondra1 setup
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir=..\artifacts
OutputBaseFilename={#MyOutput}
SetupLogging=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Alondra1 artwork: side panel on Welcome/Finish, bird in the page header. One file per DPI scale (100-250%); regenerate with branding\make-branding.ps1.
WizardImageFile=branding\wizard-164.bmp,branding\wizard-205.bmp,branding\wizard-246.bmp,branding\wizard-287.bmp,branding\wizard-328.bmp,branding\wizard-369.bmp,branding\wizard-410.bmp
WizardSmallImageFile=branding\small-55.bmp,branding\small-69.bmp,branding\small-82.bmp,branding\small-96.bmp,branding\small-110.bmp,branding\small-124.bmp,branding\small-138.bmp
WizardImageAlphaFormat=none
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\Alondra1.App\Assets\alondra1.ico
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MySource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Alondra1"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, Personal mail, tasks, and credentials live in {localappdata}\alondra1 and are kept on uninstall.

[Messages]
WelcomeLabel1=Welcome to Alondra1
WelcomeLabel2=Alondra1 brings your mail, calendar, tasks, notes, and contacts together in one calm, local-first app.%n%nThis will install [name/ver] on your computer. Your settings and data stay in their own folder and are kept across updates.
FinishedHeadingLabel=Alondra1 is ready
FinishedLabelNoIcons=Setup has installed [name] on your computer.
FinishedLabel=Setup has installed [name] on your computer. Open it from the Start menu or the shortcut.

[Code]
// Alondra1 palette (TColor is $00BBGGRR): text #2A2940, muted #5F5E7A, accent #55569E,
// chrome #F6F4FA, and #FBF9FE to match the top of the side image.
const
  Alondra1Text   = $0040292A;
  Alondra1Muted  = $007A5E5F;
  Alondra1Accent = $009E5655;
  Alondra1Chrome = $00FAF4F6;
  Alondra1Page   = $00FEF9FB;

procedure InitializeWizard;
begin
  WizardForm.Color := Alondra1Chrome;
  WizardForm.WelcomePage.Color := Alondra1Page;
  WizardForm.FinishedPage.Color := Alondra1Page;
  WizardForm.MainPanel.Color := clWhite;

  WizardForm.WelcomeLabel1.Font.Color := Alondra1Text;
  WizardForm.WelcomeLabel2.Font.Color := Alondra1Muted;
  WizardForm.FinishedHeadingLabel.Font.Color := Alondra1Text;
  WizardForm.FinishedLabel.Font.Color := Alondra1Muted;

  WizardForm.PageNameLabel.Font.Color := Alondra1Text;
  WizardForm.PageDescriptionLabel.Font.Color := Alondra1Muted;
  WizardForm.SelectDirLabel.Font.Color := Alondra1Text;
  WizardForm.ReadyLabel.Font.Color := Alondra1Text;
  WizardForm.StatusLabel.Font.Color := Alondra1Accent;
  WizardForm.FilenameLabel.Font.Color := Alondra1Muted;
end;
