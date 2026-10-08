; XaultWallet Windows installer (Inno Setup 6). Built by .github/workflows/release.yml from the
; published single-file XaultWallet.exe; unsigned, like the zip next to it.
;
; Installs for the current user by default (no administrator rights, into
; %LOCALAPPDATA%\Programs\XaultWallet), or for all users if chosen in the first dialog.
; It installs the program only: the vault and settings live in %APPDATA%\XaultWallet and are
; never written, moved or deleted by Setup or by the uninstaller.
;
; Local build (from the repository root, after publishing win-x64 into .\out):
;   iscc /DAppVersion=0.6.0-beta /DAppNumericVersion=0.6.0 /DSourceDir=%CD%\out /DOutputDir=%CD%\dist installer\XaultWallet.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "XaultWallet-setup"
#endif

[Setup]
; Never change AppId: it is how a later installer finds (and upgrades) this one.
AppId={{6B0E7F25-3D41-4C8A-9E52-7A1F0C3B9D64}
AppName=XaultWallet
AppVersion={#AppVersion}
AppVerName=XaultWallet {#AppVersion}
AppPublisher=dboudreau
AppPublisherURL=https://github.com/dboudreau00/Xault-Wallet
AppSupportURL=https://github.com/dboudreau00/Xault-Wallet/issues
AppUpdatesURL=https://github.com/dboudreau00/Xault-Wallet/releases
AppCopyright=Copyright (c) 2026 dboudreau. MIT licensed.
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={autopf}\XaultWallet
DefaultGroupName=XaultWallet
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
SetupIconFile=..\src\XaultWallet.Desktop\Assets\xault.ico
UninstallDisplayIcon={app}\XaultWallet.exe
UninstallDisplayName=XaultWallet
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The app holds this mutex while it runs: Setup asks for it to be closed before replacing it.
AppMutex=XaultWallet.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
FinishedLabel=Setup has installed [name] on your computer.%n%nYour vault and settings stay in your AppData\Roaming\XaultWallet folder: Setup never touches them, and uninstalling leaves them where they are.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\XaultWallet.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SECURITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\XaultWallet"; Filename: "{app}\XaultWallet.exe"; Comment: "Privacy-first Monero wallet"
Name: "{autodesktop}\XaultWallet"; Filename: "{app}\XaultWallet.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\XaultWallet.exe"; Description: "{cm:LaunchProgram,XaultWallet}"; Flags: nowait postinstall skipifsilent
