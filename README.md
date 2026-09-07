# WinCalendar

Windows 11 25H2 x64 日历：点击系统时间打开，显示中国节假日、传统节日和“休／班”标识，并订阅 ICS 日历。

## 使用

1. 解压独立运行包到固定目录，运行 `WinCalendar.exe`。整个目录必须一起保留。
2. 使用前退出 WinCal 或其它接管系统时钟的程序，避免争抢点击。
3. 点击任务栏原来的时间区域打开；再次点击、点击外部或按 Esc 收起。
4. 点击齿轮进入设置，添加 HTTPS 或 Webcal 订阅链接。可添加多个源、选择颜色、启停、编辑或删除。
5. 默认每 30 分钟刷新；“立即刷新”先保存当前设置并更新数据。自启动默认关闭，可在设置中启用。
6. 退出程序：设置 → 退出。退出后，系统时间点击恢复原生行为。

订阅为单向读取，不修改源日历，不支持账号登录或日程回写。带令牌的私人链接保存在本机配置，不输出到日志。订阅标题、地点和描述保持原文。

## 日期与语言

- 法定放假标“休”，官方补班标“班”；普通周末只着色，传统节日不会自动变成休息日。
- 中文节日包括春节、元宵、端午、七夕、中元、中秋、重阳、腊八、除夕；法定假日名称由年度安排补充。
- 界面跟随 Windows 显示语言，内置简中、繁中、英文、日文；其它语言回退英文。
- 日期、时间和默认周起始日跟随系统区域格式，可单独设置周起始日。
- 系统要求注销才能应用的语言变化，在下次登录时生效。切换语言不会改变中国放假安排。
- 英文角标为 Off／Work，日文为休／出；悬停可看完整含义。

## 本地数据

配置和缓存位于 `%LOCALAPPDATA%\WinCalendar`：

- `settings.json`：订阅和显示设置。
- `holiday-年份.json`：中国法定放假及补班数据。
- `订阅标识.ics`：最后一次成功下载的订阅内容。

内置已确认的 2026 年安排，后台每天尝试获取可视年份及相邻年份数据。未公布或未获取的年份显示提示，不推算调休。更新失败保留缓存；损坏配置不会被静默覆盖。

数据源：[holiday-cn](https://github.com/NateScarlet/holiday-cn)，年度数据附带国务院公告来源。节假日与普通 ICS 订阅独立，订阅不会改变休班角标。

## 构建与检查

需要 .NET 8 SDK。在工程目录运行：

```powershell
dotnet build WinCalendar.csproj --disable-build-servers -p:UseSharedCompilation=false
dotnet run --project Checks/Checks.csproj
dotnet publish WinCalendar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish/win-x64
```

独立发布包使用 .NET 8.0.30，不需要安装运行时。基础检查使用本机 .NET 8。

开发诊断（不会写入用户订阅）：

```powershell
dotnet bin/Debug/net8.0-windows/WinCalendar.dll --probe-clock clock-probe.txt
dotnet bin/Debug/net8.0-windows/WinCalendar.dll --integration-check integration-results.txt
```

`--preview` 为界面调试模式，暂停失焦关闭并显示任务栏图标；日常使用请不带该参数。`--background` 为自启动使用的后台模式。

## 边界与验收

- 时间入口采用 UI Automation 识别和鼠标钩子，不注入 Explorer，不先打开原生日历再隐藏。25H2 的时间按钮与通知按钮分开识别；Windows 后续更新仍可能改变控件结构。
- 原生接管必须实测：直接打开、无明显原生面板闪现、重复点击和外部点击关闭、Esc、通知铃铛、Win+N、Explorer 重启、多屏及不同 DPI。未测项目不能视为通过。
- ICS 请求限制为 5 MiB、25 秒、最多 5 次 HTTPS 重定向；可视六周之外不展开日程。
- 解析独立进程限制为 10 秒、256 MiB 和 10,000 条展开事件。超限视为刷新失败，保留上次缓存。
- 日历浏览范围为 1901—2100；农历转换范围以 .NET 中国农历实现为准。
- 本项目未对可执行文件进行商业代码签名。

第三方许可见 `THIRD-PARTY-NOTICES.md` 和 `Licenses`。
