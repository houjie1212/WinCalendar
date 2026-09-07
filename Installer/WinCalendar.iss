#ifndef AppVersion
  #error AppVersion is required; run build-installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
; 固定标识用于覆盖升级，不随版本改变。
AppId={{0D94FB44-DB53-44F3-B04E-F005DDBDA764}
AppName=WinCalendar
AppVersion={#AppVersion}
AppVerName=WinCalendar {#AppVersion}
DefaultDirName={localappdata}\Programs\WinCalendar
DefaultGroupName=WinCalendar
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.26200
OutputDir={#OutputDir}
OutputBaseFilename=WinCalendar-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\WinCalendar.exe
AppMutex=Local\WinCalendar
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
DisableProgramGroupPage=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh_Hans"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "zh_Hant"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[Messages]
en.SetupAppRunningError=WinCalendar is running.%nOpen its Settings and choose Exit, then click OK to continue.
en.UninstallAppRunningError=WinCalendar is running.%nOpen its Settings and choose Exit, then click OK to uninstall.
zh_Hans.SetupAppRunningError=WinCalendar 正在运行。%n请打开应用设置并选择“退出”，然后点击“确定”继续安装。
zh_Hans.UninstallAppRunningError=WinCalendar 正在运行。%n请打开应用设置并选择“退出”，然后点击“确定”继续卸载。
zh_Hant.SetupAppRunningError=WinCalendar 正在執行。%n請開啟應用程式設定並選擇「結束」，再按「確定」繼續安裝。
zh_Hant.UninstallAppRunningError=WinCalendar 正在執行。%n請開啟應用程式設定並選擇「結束」，再按「確定」繼續解除安裝。
ja.SetupAppRunningError=WinCalendar は実行中です。%n設定から終了して、OK を押すとインストールを続行します。
ja.UninstallAppRunningError=WinCalendar は実行中です。%n設定から終了して、OK を押すとアンインストールを続行します。

[CustomMessages]
en.UninstallTitle=Uninstall WinCalendar
en.DeleteDataWarning=Continue to uninstall WinCalendar. Unless you select the option below, subscriptions, settings, caches and update backups will be deleted. Canceling does not delete data.
en.KeepData=Keep subscriptions and settings
en.ContinueUninstall=Continue
en.CleanupFailed=Some data could not be removed. Remaining folder: %1
zh_Hans.UninstallTitle=卸载 WinCalendar
zh_Hans.DeleteDataWarning=继续卸载 WinCalendar。若不勾选下方选项，将删除订阅、设置、缓存和更新备份。取消卸载不会删除数据。
zh_Hans.KeepData=保留订阅和设置
zh_Hans.ContinueUninstall=继续卸载
zh_Hans.CleanupFailed=部分数据未能删除，残留目录：%1
zh_Hant.UninstallTitle=解除安裝 WinCalendar
zh_Hant.DeleteDataWarning=繼續解除安裝 WinCalendar。若不勾選下方選項，將刪除訂閱、設定、快取及更新備份。取消解除安裝不會刪除資料。
zh_Hant.KeepData=保留訂閱及設定
zh_Hant.ContinueUninstall=繼續解除安裝
zh_Hant.CleanupFailed=部分資料無法刪除，殘留目錄：%1
ja.UninstallTitle=WinCalendar のアンインストール
ja.DeleteDataWarning=WinCalendar をアンインストールします。下の項目を選択しない場合、購読、設定、キャッシュ、更新バックアップを削除します。キャンセルした場合は削除しません。
ja.KeepData=購読と設定を保持する
ja.ContinueUninstall=続行
ja.CleanupFailed=一部のデータを削除できませんでした。残っているフォルダー: %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\WinCalendar"; Filename: "{app}\WinCalendar.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\WinCalendar"; Filename: "{app}\WinCalendar.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WinCalendar.exe"; Description: "{cm:LaunchProgram,WinCalendar}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  KeepUserData: Boolean;

#include "UninstallData.iss"

function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Explanation: TNewStaticText;
  KeepBox: TNewCheckBox;
  ContinueButton, CancelButton: TNewButton;
begin
  // 静默模式没有交互机会，始终保留数据。
  KeepUserData := True;
  Result := True;
  if UninstallSilent then Exit;
  Form := CreateCustomForm(ScaleX(460), ScaleY(200), False, True);
  try
    Form.Caption := CustomMessage('UninstallTitle');
    Form.ClientWidth := ScaleX(460);
    Form.ClientHeight := ScaleY(200);
    Explanation := TNewStaticText.Create(Form);
    Explanation.Parent := Form;
    Explanation.SetBounds(ScaleX(20), ScaleY(16), ScaleX(420), ScaleY(88));
    Explanation.AutoSize := False;
    Explanation.WordWrap := True;
    Explanation.Caption := CustomMessage('DeleteDataWarning');
    KeepBox := TNewCheckBox.Create(Form);
    KeepBox.Parent := Form;
    KeepBox.SetBounds(ScaleX(20), ScaleY(108), ScaleX(420), ScaleY(24));
    KeepBox.Caption := CustomMessage('KeepData');
    KeepBox.Checked := False;
    ContinueButton := TNewButton.Create(Form);
    ContinueButton.Parent := Form;
    ContinueButton.SetBounds(ScaleX(196), ScaleY(156), ScaleX(120), ScaleY(28));
    ContinueButton.Caption := CustomMessage('ContinueUninstall');
    ContinueButton.ModalResult := mrOK;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.SetBounds(ScaleX(326), ScaleY(156), ScaleX(114), ScaleY(28));
    CancelButton.Caption := SetupMessage(msgButtonCancel);
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    CancelButton.Default := True;
    Result := Form.ShowModal = mrOK;
    if Result then KeepUserData := KeepBox.Checked;
  finally
    Form.Free;
  end;
end;

// 只识别本应用的启动命令，不更改其他程序的自启动记录。
function StartupExecutable(Command: String): String;
var
  P: Integer;
begin
  Command := Trim(Command);
  Result := '';
  if Command = '' then Exit;
  if Command[1] = '"' then begin
    Delete(Command, 1, 1);
    P := Pos('"', Command);
    if P > 0 then Result := Copy(Command, 1, P - 1);
  end else begin
    P := Pos(' ', Command);
    if P = 0 then Result := Command else Result := Copy(Command, 1, P - 1);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Command, Executable, Version: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if not RegQueryStringValue(HKCU, RunKey, 'WinCalendar', Command) then Exit;
  Executable := StartupExecutable(Command);
  if CompareText(ExtractFileName(Executable), 'WinCalendar.exe') <> 0 then Exit;
  if not GetVersionNumbersString(Executable, Version) then Exit;

  RegWriteStringValue(HKCU, RunKey, 'WinCalendar', '"' + ExpandConstant('{app}\WinCalendar.exe') + '" --background');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then begin
    if RegQueryStringValue(HKCU, RunKey, 'WinCalendar', Command) then
      if CompareText(StartupExecutable(Command), ExpandConstant('{app}\WinCalendar.exe')) = 0 then
        RegDeleteValue(HKCU, RunKey, 'WinCalendar');
  end;
  // 只有确认且已执行卸载后才清理固定数据目录；取消不会进入此阶段。
  if (CurUninstallStep = usPostUninstall) and (not KeepUserData) then begin
    Command := ExpandConstant('{localappdata}\WinCalendar');
    if not DeleteUserTree(Command) then
      MsgBox(FmtMessage(CustomMessage('CleanupFailed'), [Command]), mbInformation, MB_OK);
  end;
end;
