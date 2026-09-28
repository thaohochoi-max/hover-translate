; Script Inno Setup cho Hover Translate - cài đặt per-user (không cần quyền
; admin/UAC), có shortcut Desktop + Start Menu, tuỳ chọn tự chạy cùng Windows,
; và gỡ cài đặt đàng hoàng qua Windows Settings/Control Panel.
;
; Build trước khi compile file này:
;   1) dotnet publish ..\src\HoverTranslate.App\HoverTranslate.App.csproj -c Release -r win-x64 --self-contained true -o ..\publish
;   2) powershell -File build-python-embed.ps1   (chỉ cần làm lại khi đổi dependency Python - kết quả ~1GB, không commit git)
;
; Compile: "C:\Users\thao\AppData\Local\Programs\Inno Setup 6\ISCC.exe" HoverTranslate.iss

#define MyAppName "Hover Translate"
#define MyAppVersion "1.0.0"
#define MyAppExeName "HoverTranslate.App.exe"

[Setup]
AppId={{B7E1F6D2-5C4A-4E8B-9F3D-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={userpf}\HoverTranslate
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\installer-output
OutputBaseFilename=HoverTranslate-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
InfoAfterFile=SAU-KHI-CAI.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo shortcut ngoài Desktop"; GroupDescription: "Shortcut bổ sung:"
Name: "startupicon"; Description: "Tự động chạy cùng Windows (chạy nền, không hiện cửa sổ)"; GroupDescription: "Tuỳ chọn khởi động:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\tessdata\*"; DestDir: "{app}\tessdata"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\python-embed\*"; DestDir: "{app}\python-embed"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "SAU-KHI-CAI.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Gỡ cài đặt {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HoverTranslate"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startupicon; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Chạy {#MyAppName} ngay"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; App tự ghi cache dịch/crash log vào %LOCALAPPDATA%\HoverTranslate lúc chạy -
; không phải file cài đặt nên KHÔNG xoá lúc gỡ (giữ lại cache dịch của người
; dùng phòng khi họ cài lại).
