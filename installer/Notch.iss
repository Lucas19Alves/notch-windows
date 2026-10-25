; Instalador do Notch (Inno Setup 6). Gere com build.ps1 na raiz do projeto.
#define AppVersion "1.0.0"

[Setup]
AppId={{8C3F2A51-6E1B-4C7D-9A2E-5B4F7D8E1C20}
AppName=Notch
AppVersion={#AppVersion}
AppVerName=Notch {#AppVersion}
DefaultDirName={localappdata}\Programs\Notch
DisableProgramGroupPage=yes
DisableDirPage=yes
; instala só para o usuário atual: não pede permissão de administrador
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=NotchSetup-{#AppVersion}
SetupIconFile=..\Assets\notch.ico
UninstallDisplayIcon={app}\NotchApp.exe
UninstallDisplayName=Notch
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\artifacts\publish\NotchApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Notch"; Filename: "{app}\NotchApp.exe"

[Run]
Filename: "{app}\NotchApp.exe"; Description: "Abrir o Notch"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; fecha o Notch e devolve a barra de tarefas antes de apagar os arquivos
Filename: "{sys}\taskkill.exe"; Parameters: "/im NotchApp.exe /f"; Flags: runhidden; RunOnceId: "StopNotch"
Filename: "{app}\NotchApp.exe"; Parameters: "--restore-taskbar"; Flags: runhidden waituntilterminated; RunOnceId: "RestoreTaskbar"

[Registry]
; o próprio Notch cria a entrada de iniciar com o Windows; a desinstalação a remove
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "NotchApp"; ValueType: none; Flags: uninsdeletevalue
