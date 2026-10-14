; RimForge X Manager — the installer (Inno Setup 6). Built by release.cmd, which passes the version and the files:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceExe=...\RimForge.exe /DOutputDir=...\artifacts\1.0.0 installer\RimForge.iss
; Installs for the current user only (no administrator rights), so RimForge can also update itself in place.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\RimForge.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define AppName "RimForge X Manager"
#define RepoUrl "https://github.com/SORLOK212/RimForge-X-Manager"

[Setup]
; Never change the AppId: Windows knows an installed RimForge by it (RimForge reads it too, see MainWindowViewModel.Updates).
AppId={{6F3C2A1E-8B4D-4E7A-9C5F-2D1B0A9E7F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SORLOK212
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (C) 2026 SORLOK212 · GPL-3.0
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
DefaultDirName={localappdata}\Programs\RimForge
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=RimForge-Setup-{#AppVersion}
SetupIconFile=..\src\RimForge.App\Assets\rimforge.ico
UninstallDisplayIcon={app}\RimForge.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto
UsePreviousLanguage=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.DeleteDataQuestion=Удалить также данные RimForge?%n%nЭто настройки, сборки модов, резервные копии и библиотека сохранённых версий игры (%1, %2).%n%nСама игра, её сохранения и моды из Steam не затрагиваются.%n%nНажми «Нет», чтобы оставить данные для следующей установки.
en.DeleteDataQuestion=Also delete RimForge's data?%n%nThat is its settings, modpacks, backups and the library of saved game versions (%1, %2).%n%nThe game itself, its saves and your Steam mods are not touched.%n%nChoose "No" to keep the data for a later install.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "RimForge.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\RimForge.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\RimForge.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RimForge.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[InstallDelete]
; The portable marker must never end up in an installed copy.
Type: files; Name: "{app}\portable.txt"

[UninstallDelete]
; Left by RimForge updating itself.
Type: files; Name: "{app}\RimForge.exe.old"
Type: dirifempty; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings, Library: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if UninstallSilent then Exit;
  Settings := ExpandConstant('{userappdata}\RimForge');
  Library := ExpandConstant('{localappdata}\RimForge');
  if not DirExists(Settings) and not DirExists(Library) then Exit;
  if MsgBox(FmtMessage(CustomMessage('DeleteDataQuestion'), [Settings, Library]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(Settings, True, True, True);
    DelTree(Library, True, True, True);
  end;
end;
