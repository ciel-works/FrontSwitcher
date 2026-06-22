; ============================================================
;  FrontSwitcher インストーラ定義（Inno Setup 6）
;  コンパイル: Inno Setup をインストール後、このファイルを
;  ISCC.exe にかける（または Inno Setup Compiler で開いてビルド）。
;  事前に publish.ps1 で自己完結 exe を作成しておくこと。
; ============================================================

#define AppName        "FrontSwitcher"
#define AppVersion      "1.0.0"
#define AppPublisher    "（あなたの名前 / ハンドル名）"
#define AppURL          "https://github.com/（ユーザー名）/FrontSwitcher"
#define AppExeName      "FrontSwitcher.exe"

; 自己完結・単一exe の出力先（publish.ps1 の成果物）。.iss から見た相対パス。
#define PublishExe      "..\bin\Release\net8.0-windows\win-x64\publish\FrontSwitcher.exe"

[Setup]
; AppId はアプリ固有の GUID。一度決めたら変更しないこと（更新インストールの識別に使う）。
AppId={{8F3C2A91-5E47-4B6D-9C2E-FR0NTSW1TCHR}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
; 64bit 専用
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; インストール先が Program Files のため管理者権限を要求
PrivilegesRequired=admin
; 起動中でもインストール/アンインストールできるよう、実行中の本体を閉じる
CloseApplications=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputDir=.
OutputBaseFilename=FrontSwitcher_Setup_{#AppVersion}
; インストーラ自身とアンインストーラのアイコン（☑）
SetupIconFile=..\checkmark.ico

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english";  MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishExe}";                 DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\はじめにお読みください.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
; LICENSE を置く場合は次行のコメントを外す
; Source: "..\LICENSE.txt";              DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";                 Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} をアンインストール"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";           Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; 注: 「Windows 起動時に常駐」はアプリ本体の設定（設定画面のチェック）で行ってください。
;     インストーラ側でも登録すると二重起動になるため、ここでは登録しません。

[UninstallDelete]
; 設定ファイル（%AppData%\FrontSwitcher）はユーザーデータのため既定では消しません。
; アンインストール時に消したい場合は下記コメントを外す:
; Type: filesandordirs; Name: "{userappdata}\FrontSwitcher"
