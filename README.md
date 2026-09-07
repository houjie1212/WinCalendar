# WinCalendar

Windows 11 25H2 x64 日历：点击系统时间打开，默认显示公历，支持自定义 ICS 日历；启用中国节假日订阅后显示节日名称和“休／班”标识。

## 使用

1. 解压独立运行包到固定目录，运行 `WinCalendar.exe`。整个目录必须一起保留。
2. 使用前退出 WinCal 或其它接管系统时钟的程序，避免争抢点击。
3. 点击任务栏原来的时间区域打开；再次点击、点击外部或按 Esc 收起。
4. 点击齿轮进入设置，添加 HTTPS 或 Webcal 订阅链接。可添加多个源、选择颜色、启停、编辑或删除。
5. 默认每 30 分钟刷新；“立即刷新”先保存当前设置并更新数据。自启动默认关闭，可在设置中启用。
6. 退出程序：设置 → 退出。退出后，系统时间点击恢复原生行为。

订阅为单向读取，不修改源日历，不支持账号登录或日程回写。带令牌的私人链接保存在本机配置，不输出到日志。订阅标题、地点和描述保持原文。

## 日期与语言

- 默认仅显示公历、普通周末颜色及用户订阅日程，不显示农历或自动推算传统节日。
- 在订阅编辑中选择“普通日历”或“中国节假日”；旧订阅保持普通日历，需要手动修改类型。
- 中国节假日源启用后，日期格显示名称与休班角标；停用或删除并保存后立即移除。
- 首先兼容 ShuYZ 完整日历，复制以下链接并选择“中国节假日”类型：

```text
https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/holidayCal.ics
```

- 仅识别完整标题“节日名 假期 第N天/共N天”和“节日名 补班 第N天/共N天”；未知格式仍显示日程，不推断休班。其他来源不保证支持角标。
- 日期格按源日历日期标记；定时详情按系统本地时区显示，可能落在不同日期。全天结束日期不包含在事件中。
- 多源同日休班冲突时隐藏角标，日期提示说明冲突；多个名称按订阅顺序展示首个，提示和带来源名称的日程详情保留全部信息。
- 界面跟随 Windows 显示语言，内置简中、繁中、英文、日文；其他语言回退英文。日期、时间与默认周起始日跟随系统区域格式。
- 已知节日名称和休班角标本地化；ICS 原始标题、地点、描述不翻译。没有订阅数据的日期不补充节假日。

## 本地数据

配置和缓存位于 `%LOCALAPPDATA%\WinCalendar`：

- `settings.json`：订阅和显示设置。
- `订阅标识.ics`：最后一次成功下载的订阅内容。

不自动添加订阅，不读取或更新旧 `holiday-年份.json` 缓存，也不主动删除这些旧文件。订阅更新失败保留最后有效缓存；损坏配置不会被静默覆盖。

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
- 日历浏览范围为 1901—2100。
- 本项目未对可执行文件进行商业代码签名。

第三方许可见 `THIRD-PARTY-NOTICES.md` 和 `Licenses`。
