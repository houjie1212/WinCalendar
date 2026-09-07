# WinCalendar 安装包

在 PowerShell 7 中运行：

```powershell
./Installer/build-installer.ps1 -IsccPath 'C:\路径\ISCC.exe'
```

需要 .NET 8 SDK 和已安装的 Inno Setup，可通过 `-NuGetSource` 指定本地 NuGet 源。脚本先运行现有检查并从干净目录生成独立运行包，然后生成 `publish/installer/WinCalendar-Setup-{版本}-x64.exe` 及 `SHA256SUMS.txt`。版本号取自工程配置。

安装包仅安装到当前用户，默认目录为 `%LOCALAPPDATA%\Programs\WinCalendar`，包含运行时及 `release-files.json`。支持 Windows 11 25H2 x64，提供简中、繁中、英文、日文；开始菜单快捷方式默认创建，桌面快捷方式可选。安装完成可启动程序，自启动由应用设置控制。

升级沿用固定安装标识及目录。安装、卸载时要求先从应用设置退出，不强制终止程序。交互卸载提供“保留订阅和设置”复选框，默认不勾选；继续卸载会删除 `%LOCALAPPDATA%\WinCalendar` 下的设置、订阅、缓存和更新备份，勾选后保留。取消卸载不删除数据，静默卸载默认保留。目录链接及无法删除的文件会保留并提示。仅清除指向安装目录的自启动记录。

安装包未签名，Windows 可能显示未知发布者或信誉提示。脚本不上传 Release，也不生成安装包的在线更新资产；在线更新使用发行脚本同时生成的 ZIP 和校验文件。
