using System.Collections;
using System.Globalization;
using WinCalendar;

// 可选真实公网检查：只使用公开源，输出不含地址令牌。
if (args.Length == 1 && args[0] == "--check-public-feeds")
{
    L.Reload();
    foreach (var source in SubscriptionPreset.All)
    {
        try
        {
            var text = Download.Text(Download.Validate(source.Url)).GetAwaiter().GetResult();
            var events = IcsParser.Parse(text, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), "public", source.Color, source.Kind);
            Console.WriteLine(source.NameKey + " PASS events=" + events.Count);
        }
        catch (Exception e) { Console.WriteLine(source.NameKey + " UNVERIFIED " + e.GetType().Name); Environment.ExitCode = 1; }
    }
    return;
}

// 隔离发行流程通过环境变量共享检查目录，不访问用户配置。
if (Environment.GetEnvironmentVariable("WINCALENDAR_CHECK_ROOT") is string checkRoot) Store.Root = checkRoot;
// 独立进程只返回摘要，验证重启可解密且不在命令行输出地址。
if (args.Length == 1 && args[0] == "--check-encrypted-settings")
{
    var restored = Store.Load();
    Console.WriteLine(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(restored.Sources[0].Url))));
    return;
}
if (args.Length == 2 && args[0] is "--apply-update" or "--recover-update")
{
    Environment.ExitCode = UpdateInstaller.Worker(args[1], args[0] == "--recover-update"); return;
}
if (args.Length == 2 && args[0] == "--test-recover-parent")
{
    Environment.ExitCode = UpdateInstaller.ResumeIfNeeded() ? 0 : 1; return;
}
if (args.Length == 2 && args[0] == "--test-update-parent")
{
    UpdateInstaller.Launch(args[1]).GetAwaiter().GetResult(); return;
}
// 子进程模拟新版启动确认，不加载真实设置或接管时钟。
if (args.Length == 2 && args[0] == "--updated")
{
    Store.Root = Path.GetDirectoryName(Path.GetDirectoryName(args[1]))!;
    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "skip-ack")))
    {
        UpdateInstaller.Acknowledge(args[1]);
        Thread.Sleep(1500);
    }
    return;
}
if (args.Contains("--update-failed")) return;
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception(name); }
L.Reload();
Store.Root = Path.Combine(Path.GetTempPath(), "WinCalendarChecks-" + Guid.NewGuid().ToString("N"));
try
{
    // 隐私检查使用独立目录，所有地址均为合成数据。
    string checkHome = Store.Root;
    Store.Root = Path.Combine(checkHome, "privacy");
    try
    {
        string secret = "https://calendar.example/private/Token-AbC/中文?Key=XyZ%2B&other=你好#fragment";
        var privateSettings = new Settings { Sources = new() { new Subscription { Url = secret, Name = "测试", Enabled = false, Color = "#123456", Kind = SubscriptionKind.ChinaHolidays } }, ShowChineseLunar = true };
        string configPath = Store.PathFor("settings.json");
        string Snapshot(Settings value) => System.Text.Json.JsonSerializer.Serialize(value, Store.Json);
        Store.Save(privateSettings);
        string encryptedJson = File.ReadAllText(configPath);
        var stored = System.Text.Json.Nodes.JsonNode.Parse(encryptedJson)!;
        Check(stored["Format"]!.GetValue<int>() == 1 && stored["Sources"]![0]!["Url"] == null && stored["Sources"]![0]!["ProtectedUrl"]!.GetValue<string>().Length > 0, "配置只保存带版本的地址密文");
        Check(!encryptedJson.Contains("Token-AbC") && !encryptedJson.Contains("calendar.example") && !File.Exists(configPath + ".tmp"), "配置及临时文件不残留明文令牌");
        Check(Snapshot(Store.Load()) == Snapshot(privateSettings), "DPAPI 完整地址及订阅属性往返");
        string beforeDraft = Snapshot(privateSettings);
        Store.Save(privateSettings);
        Check(Snapshot(privateSettings) == beforeDraft, "随机密文不改变草稿比较");
        var childInfo = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) childInfo.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        childInfo.ArgumentList.Add("--check-encrypted-settings"); childInfo.Environment["WINCALENDAR_CHECK_ROOT"] = Store.Root;
        using (var restarted = System.Diagnostics.Process.Start(childInfo)!)
        {
            Check(restarted.WaitForExit(15000) && restarted.ExitCode == 0 && restarted.StandardOutput.ReadToEnd().Trim() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret))), "独立进程重启恢复加密地址");
        }
        string oldJson = System.Text.Json.JsonSerializer.Serialize(new { Sources = new[] { new { privateSettings.Sources[0].Id, Url = secret, Name = "旧配置", Enabled = false, Color = "#123456", Kind = 1 } }, ShowChineseLunar = true });
        Store.Write(configPath, oldJson);
        Store.Load(migrate: false);
        Check(File.ReadAllText(configPath) == oldJson, "更新确认前只读配置不迁移");
        var migrated = Store.Load();
        Check(migrated.Sources[0].Url == secret && migrated.Sources[0].Id == privateSettings.Sources[0].Id && !migrated.Sources[0].Enabled && migrated.ShowChineseLunar && !File.ReadAllText(configPath).Contains("Token-AbC"), "旧配置自动迁移且保留属性");
        Store.Write(configPath, oldJson);
        using (var lockedConfig = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => Store.Load(), "迁移写入失败拒绝覆盖");
        Check(File.ReadAllText(configPath) == oldJson && !File.Exists(configPath + ".tmp"), "迁移失败保留原配置并清除临时文件");
        Store.Load();
        var originalEncrypted = File.ReadAllText(configPath);
        int encryptions = 0;
        var pair = new Settings { Sources = new() { new Subscription { Url = secret }, new Subscription { Url = "https://calendar.example/other" } } };
        Reject(() => Store.Save(pair, address => { if (++encryptions == 2) throw new System.Security.Cryptography.CryptographicException(); return "test"; }), "部分地址加密失败不写入");
        Check(File.ReadAllText(configPath) == originalEncrypted && !File.Exists(configPath + ".tmp"), "加密失败保留完整原文件");
        Store.Save(pair);
        var healthyPair = File.ReadAllText(configPath);
        foreach (var badCipher in new[] { "not-base64!", Convert.ToBase64String(new byte[32]) })
        {
            var broken = System.Text.Json.Nodes.JsonNode.Parse(healthyPair)!;
            broken["Sources"]![0]!["ProtectedUrl"] = badCipher;
            Store.Write(configPath, broken.ToJsonString());
            var loaded = Store.Load();
            Check(loaded.Sources[0].UrlUnreadable && loaded.Sources[0].Url == "" && loaded.Sources[0].ProtectedUrl == badCipher && loaded.Sources[1].Url == pair.Sources[1].Url, "单条密文错误不影响其他订阅");
            var copy = System.Text.Json.JsonSerializer.Deserialize<Settings>(Snapshot(loaded), Store.Json)!;
            copy.ShowChineseLunar = true; Store.Save(copy);
            Check(Store.Load().Sources[0].ProtectedUrl == badCipher && loaded.ShowChineseLunar == false, "保存及取消草稿保留失败密文");
            string cacheText = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:private-cache\r\nDTSTART;VALUE=DATE:20260908\r\nDTEND;VALUE=DATE:20260909\r\nSUMMARY:Cached\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            Store.Write(Store.PathFor(copy.Sources[0].Id + ".ics"), cacheText);
            copy.Sources[1].Enabled = false;
            var cache = new Subscriptions(copy);
            cache.Refresh(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), true).GetAwaiter().GetResult();
            Check(cache.Events.Count == 1 && !copy.Sources[0].Failed && copy.Sources[0].UrlUnreadable && Store.Load().Sources[0].ProtectedUrl == badCipher, "解密失败跳过网络并保留缓存和密文");
        }
        Store.Write(configPath, "{\"Format\":99,\"Sources\":[]}");
        string future = File.ReadAllText(configPath);
        Reject(() => Store.Load(), "拒绝加载未知配置格式");
        Reject(() => Store.Save(privateSettings), "拒绝覆盖未知配置格式");
        Check(File.ReadAllText(configPath) == future, "未知格式原文件保留");
        Console.WriteLine("SKIP 跨 Windows 用户解密：当前未创建隔离用户，待实机验收");
    }
    finally { Store.Root = checkHome; }

    // 连接替身只接收已校验的 IP，不建立真实网络连接。
    System.Net.IPAddress IP(string value) => System.Net.IPAddress.Parse(value);
    foreach (var address in new[] { "0.0.0.0", "10.1.2.3", "100.64.0.1", "127.0.0.1", "169.254.169.254", "172.16.0.1", "192.168.1.1", "198.18.0.1", "192.0.2.1", "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "fc00::1", "ff02::1", "::ffff:127.0.0.1", "64:ff9b::7f00:1", "2002:7f00:1::1", "2001:db8::1", "3fff::1" })
        Check(!PublicNetwork.IsPublic(IP(address), Array.Empty<System.Net.IPAddress>()), "订阅拒绝非公网 " + address);
    foreach (var address in new[] { "8.8.8.8", "1.1.1.1", "2606:4700:4700::1111", "::ffff:8.8.8.8" })
        Check(PublicNetwork.IsPublic(IP(address), Array.Empty<System.Net.IPAddress>()), "订阅允许公网 " + address);
    Check(!PublicNetwork.IsPublic(IP("8.8.8.8"), new[] { IP("::ffff:8.8.8.8") }), "拒绝本机公网网卡地址");
    foreach (var host in new[] { "127.1", "2130706433", "0x7f000001", "[::1]", "localhost", "localhost.", "x.localhost" })
        Reject(() => Download.Validate("https://" + host + "/a.ics"), "拒绝特殊主机写法 " + host);
    int resolves = 0, connections = 0;
    Task<System.Net.IPAddress[]> ResolvePublic(string host, CancellationToken ct) { resolves++; return Task.FromResult(resolves == 1 ? new[] { IP("8.8.8.8"), IP("1.1.1.1") } : new[] { IP("127.0.0.1") }); }
    ValueTask<Stream> Dial(System.Net.IPEndPoint endpoint, CancellationToken ct)
    {
        connections++;
        Check(endpoint.Address.Equals(IP(connections == 1 ? "8.8.8.8" : "1.1.1.1")), "连接只使用批准 IP " + connections);
        if (connections == 1) throw new System.Net.Sockets.SocketException();
        return ValueTask.FromResult<Stream>(new MemoryStream());
    }
    using (PublicNetwork.Connect("calendar.example", 443, CancellationToken.None, ResolvePublic, Dial, () => Array.Empty<System.Net.IPAddress>()).AsTask().GetAwaiter().GetResult()) { }
    Check(resolves == 1 && connections == 2, "备用连接不重新解析 DNS");
    connections = 0;
    foreach (var answers in new[] { Array.Empty<System.Net.IPAddress>(), new[] { IP("8.8.8.8"), IP("10.0.0.1") } })
        Reject(() => PublicNetwork.Connect("calendar.example", 443, CancellationToken.None, (h,c) => Task.FromResult(answers), Dial, () => Array.Empty<System.Net.IPAddress>()).AsTask().GetAwaiter().GetResult(), "空或混合 DNS 整体拒绝");
    Check(connections == 0, "DNS 拒绝发生在连接之前");
    using (var cancelledNetwork = new CancellationTokenSource())
    {
        cancelledNetwork.Cancel();
        Reject(() => PublicNetwork.Connect("calendar.example", 443, cancelledNetwork.Token, ResolvePublic, Dial, () => Array.Empty<System.Net.IPAddress>()).AsTask().GetAwaiter().GetResult(), "取消后不连接");
        Check(connections == 0, "取消不绕过检查");
    }
    int failedDials = 0;
    Reject(() => PublicNetwork.Connect("calendar.example", 443, CancellationToken.None,
        (h,c) => Task.FromResult(new[] { IP("8.8.8.8"), IP("1.1.1.1") }),
        (e,c) => { failedDials++; throw new System.Net.Sockets.SocketException(); }, () => Array.Empty<System.Net.IPAddress>()).AsTask().GetAwaiter().GetResult(), "全部 IP 连接失败不回退域名");
    Check(failedDials == 2, "失败仅尝试本次批准的地址");
    using (var duringDns = new CancellationTokenSource())
    {
        Reject(() => PublicNetwork.Connect("calendar.example", 443, duringDns.Token,
            (h,c) => { duringDns.Cancel(); return Task.FromResult(new[] { IP("8.8.8.8") }); },
            Dial, () => Array.Empty<System.Net.IPAddress>()).AsTask().GetAwaiter().GetResult(), "DNS 完成时取消不建立连接");
        Check(connections == 0, "DNS 期间取消后无连接");
    }
    using (var policy = PublicNetwork.CreateHandler())
        Check(!policy.UseProxy && !policy.UseCookies && !policy.AllowAutoRedirect && policy.SslOptions.RemoteCertificateValidationCallback == null, "禁用代理 Cookie 并保留系统证书验证");
    foreach (bool privateRedirect in new[] { false, true })
    {
        var transport = new SubscriptionRedirectStub(privateRedirect);
        using var client = new System.Net.Http.HttpClient(transport);
        if (privateRedirect)
        {
            Reject(() => Download.Text(new Uri("https://calendar.example/a"), default, client).GetAwaiter().GetResult(), "重定向内网拒绝");
            Check(transport.Calls == 1, "内网重定向不发送请求");
        }
        else Check(Download.Text(new Uri("https://calendar.example/a"), default, client).GetAwaiter().GetResult() == "calendar", "公网重定向正常");
    }
    Check(PublicNetwork.WasBlocked(new System.Net.Http.HttpRequestException("failed", new SubscriptionBlockedException())), "安全拦截穿透网络异常包装");

    // 内网旧配置仍可保留；安全拦截和离线刷新都使用已有有效缓存。
    var cachedSource = new Subscription { Url = "https://127.0.0.1/private?token=test", Name = "缓存检查" };
    var cachedSettings = new Settings(); cachedSettings.Sources.Add(cachedSource);
    string cachedIcs = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:cached\r\nDTSTART;VALUE=DATE:20260908\r\nDTEND;VALUE=DATE:20260909\r\nSUMMARY:Cached\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
    Store.Write(Store.PathFor(cachedSource.Id + ".ics"), cachedIcs);
    var cachedSubscriptions = new Subscriptions(cachedSettings);
    cachedSubscriptions.Refresh(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), true).GetAwaiter().GetResult();
    Check(cachedSource.Blocked && cachedSource.Failed && cachedSubscriptions.Events.Count == 1, "地址拦截保留有效缓存及独立状态");
    cachedSubscriptions.Refresh(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), false).GetAwaiter().GetResult();
    Check(cachedSubscriptions.Events.Count == 1 && File.ReadAllText(Store.PathFor(cachedSource.Id + ".ics")) == cachedIcs, "离线缓存不被删除或覆盖");

    File.Delete(Store.PathFor("settings.json")); // 清除本项检查的配置，不影响后续默认配置断言。
    // 识别与命中使用纯数据检查，不改变当前任务栏或用户设置。
    var bar = new System.Windows.Rect(-1920, 1000, 1920, 80);
    var rect = new System.Windows.Rect(-150, 1010, 100, 50);
    string Identify(string cls, string id, string text, bool button = true, bool hidden = false)
        => ClockHook.CandidateReason(cls, id, text, button, rect, bar, hidden);
    Check(Identify("SystemTray.DateTimeIcon", "", "") == "accepted", "明确时间控件标识");
    Check(Identify("", "ClockButton", "") == "accepted", "时钟自动化标识");
    Check(Identify("TrayClockWClass", "", "", false) == "accepted", "原生时钟回退");
    foreach (var cls in new[] { "SystemTray.OmniButton", "SystemTray.OmniButtonLeft" })
        Check(Identify(cls, "SystemTrayIcon", "13:25") == "accepted", "共享时间按钮 " + cls);
    Check(Identify("SystemTray.OmniButtonRight", "SystemTrayIcon", "13:25") != "accepted", "通知按钮不接管");
    Check(Identify("SystemTray.OmniButton", "SystemTrayIcon", "通知") != "accepted", "共享按钮必须包含时间");
    Check(Identify("SystemTray.OmniButton", "SystemTrayIcon", "13:25", false) != "accepted", "共享容器不接管");
    Check(Identify("SystemTray.DateTimeIcon", "", "", true, true) != "accepted", "隐藏时间控件不接管");
    Check(ClockHook.CandidateReason("TrayClockWClass", "", "", false, System.Windows.Rect.Empty, bar, false) != "accepted", "空时钟范围拒绝");
    var point = new System.Windows.Point(-100, 1030);
    Check(ClockHook.HitTest(rect, bar, point, true, true), "负坐标显示器命中");
    Check(!ClockHook.HitTest(rect, bar, point, true, false), "遮挡窗口点击不接管");
    Check(!ClockHook.HitTest(rect, bar, point, false, true), "隐藏任务栏不接管");
    Check(!ClockHook.HitTest(rect, new System.Windows.Rect(0, 0, 100, 50), point, true, true), "任务栏移动后旧范围失效");

    // 不访问真实卸载记录，使用临时目录验证卸载命令匹配。
    string uninstallDirectory = Path.Combine(Store.Root, "installed with spaces");
    Directory.CreateDirectory(uninstallDirectory);
    string uninstaller = Path.Combine(uninstallDirectory, "unins000.exe"); File.WriteAllText(uninstaller, "test");
    Check(UninstallService.Resolve(uninstallDirectory, uninstallDirectory, "\"" + uninstaller + "\"") == uninstaller, "卸载命令支持含空格目录");
    Check(UninstallService.Resolve(uninstallDirectory, null, null) == null, "便携版禁用卸载");
    Check(UninstallService.Resolve(uninstallDirectory, uninstallDirectory + "-other", uninstaller) == null, "卸载拒绝安装目录不匹配");
    Check(UninstallService.Resolve(uninstallDirectory, uninstallDirectory, uninstaller + " /SILENT") == null, "卸载不执行注册命令中的额外参数");
    Check(UninstallService.Resolve(uninstallDirectory, uninstallDirectory, Path.Combine(uninstallDirectory, "cmd.exe")) == null, "卸载拒绝其他可执行文件");
    File.Delete(uninstaller);
    Check(UninstallService.Resolve(uninstallDirectory, uninstallDirectory, uninstaller) == null, "卸载程序缺失时禁用");

    // 旧记录的兼容只允许确认；这里不启动或终止任何进程。
    string legacyJob = Path.Combine(UpdateService.Root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(legacyJob);
    var legacyRecord = new UpdateJournal { Target = AppContext.BaseDirectory, Phase = "starting", Files = new[] { "WinCalendar.exe" }, Existing = new[] { "WinCalendar.exe" } };
    UpdateInstaller.ValidateConfirmation(legacyRecord, AppContext.BaseDirectory);
    Check(true, "旧记录可进入受限确认校验");
    Reject(() => UpdateInstaller.ValidateConfirmation(legacyRecord, Path.Combine(Store.Root, "unrelated")), "旧记录拒绝错误目标目录");
    legacyRecord.Files = new[] { "../outside" };
    Reject(() => UpdateInstaller.ValidateConfirmation(legacyRecord, AppContext.BaseDirectory), "旧确认拒绝越界文件名");
    legacyRecord.Files = legacyRecord.Existing;
    legacyRecord.Phase = "unknown";
    Reject(() => UpdateInstaller.ValidateConfirmation(legacyRecord, AppContext.BaseDirectory), "旧确认拒绝非法阶段");
    legacyRecord.Phase = "starting";
    var rawRecord = System.Text.Json.JsonSerializer.SerializeToNode(legacyRecord)!.AsObject();
    rawRecord.Remove("Format");
    File.WriteAllText(UpdateInstaller.JournalPath(legacyJob), rawRecord.ToJsonString());
    Reject(() => UpdateInstaller.Acknowledge(legacyJob), "旧确认拒绝缺失工作进程");
    using (var current = System.Diagnostics.Process.GetCurrentProcess())
    {
        rawRecord["WorkerId"] = current.Id; rawRecord["WorkerStarted"] = current.StartTime.ToUniversalTime().Ticks;
        rawRecord["ChildId"] = current.Id; rawRecord["ChildStarted"] = current.StartTime.ToUniversalTime().Ticks;
        File.WriteAllText(UpdateInstaller.JournalPath(legacyJob), rawRecord.ToJsonString());
        Reject(() => UpdateInstaller.Acknowledge(legacyJob), "旧确认拒绝无关工作进程路径");
        Check(!current.HasExited, "确认拒绝不终止无关进程");
    }
    rawRecord["Format"] = 0;
    File.WriteAllText(UpdateInstaller.JournalPath(legacyJob), rawRecord.ToJsonString());
    Reject(() => UpdateInstaller.Acknowledge(legacyJob), "显式未知格式不当作旧格式接受");
    rawRecord.Remove("Format"); rawRecord["Phase"] = "complete";
    File.WriteAllText(UpdateInstaller.JournalPath(legacyJob), rawRecord.ToJsonString());
    Check(!UpdateInstaller.ResumeIfNeeded(), "旧格式完成记录忽略且不执行恢复");
    rawRecord["Phase"] = "rolledback";
    File.WriteAllText(UpdateInstaller.JournalPath(legacyJob), rawRecord.ToJsonString());
    Check(!UpdateInstaller.ResumeIfNeeded(), "旧格式回滚记录忽略且不执行恢复");

    // 更新检查使用模拟 HTTP 响应，不依赖线上 Release 或用户订阅。
    Check(UpdateService.ParseVersion("v1.10.0") > UpdateService.ParseVersion("1.9.9"), "更新版本按数字比较");
    Reject(() => UpdateService.ParseVersion("v1.2.3-beta"), "更新拒绝预发布版本格式");
    Reject(() => UpdateService.ParseVersion("1.2"), "更新拒绝不完整版本");
    Check(UpdateService.AllowedUri(new Uri("https://github.com/houjie1212/WinCalendar/releases/download/v1.0.1/a.zip")) &&
        !UpdateService.AllowedUri(new Uri("https://github.com/other/repo/releases/download/v1/a.zip")) &&
        !UpdateService.AllowedUri(new Uri("http://github.com/houjie1212/WinCalendar/releases/download/v1/a.zip")), "更新仅接受固定仓库HTTPS资产");
    Check(UpdateService.AllowedUri(new Uri("https://release-assets.githubusercontent.com/file?token=abc"), true) &&
        !UpdateService.AllowedUri(new Uri("https://evil.example/file"), true), "更新重定向限制");
    Check(UpdateService.ReadRelease("{\"draft\":false,\"prerelease\":true}") == null, "更新忽略预发布");
    var latestJson = System.Text.Json.JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v" + UpdateService.Current });
    Check(UpdateService.ReadRelease(latestJson)!.Version == UpdateService.Current, "更新同版本无需更新包");
    var nextVersion = new Version(UpdateService.Current.Major, UpdateService.Current.Minor, UpdateService.Current.Build + 1);
    Reject(() => UpdateService.ReadRelease(System.Text.Json.JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v" + nextVersion, assets = Array.Empty<object>() })), "更新缺失资产被拒绝");
    foreach (var code in new[] { 404, 403, 429, 500, 200 })
    {
        var handler = new UpdateHttpStub(code, latestJson);
        if (code == 404) Check(UpdateService.Check(CancellationToken.None, handler).GetAwaiter().GetResult() == null, "更新无正式发布");
        else if (code == 200) Check(UpdateService.Check(CancellationToken.None, handler).GetAwaiter().GetResult()!.Version == UpdateService.Current, "更新HTTP成功解析");
        else Reject(() => UpdateService.Check(CancellationToken.None, handler).GetAwaiter().GetResult(), "更新HTTP失败或限流 " + code);
    }
    using (var cancelledUpdate = new CancellationTokenSource())
    {
        cancelledUpdate.Cancel();
        Reject(() => UpdateService.Check(cancelledUpdate.Token, new UpdateHttpStub(200, latestJson)).GetAwaiter().GetResult(), "更新检查可取消");
    }
    string updateTemp = Path.Combine(Store.Root, "update-tests"); Directory.CreateDirectory(updateTemp);
    foreach (string path in new[] { "../outside", "C:/outside", "a\\b", "file:stream", "a/CON.txt", "a/../b", "a. " })
        Reject(() => UpdateService.SafePath(updateTemp, path), "更新拒绝不安全路径 " + path);
    var assetUri = new Uri("https://github.com/houjie1212/WinCalendar/releases/download/v1.0.1/package.zip");
    string downloaded = Path.Combine(updateTemp, "downloaded");
    UpdateService.DownloadAsset(assetUri, downloaded, 100, null, CancellationToken.None, new UpdateHttpStub(200, "download-body")).GetAwaiter().GetResult();
    Check(File.ReadAllText(downloaded) == "download-body", "更新资产下载内容");
    Reject(() => UpdateService.DownloadAsset(assetUri, downloaded + "-large", 2, null, CancellationToken.None, new UpdateHttpStub(200, "too-large")).GetAwaiter().GetResult(), "更新资产大小上限");
    using (var cancelledDownload = new CancellationTokenSource())
    {
        cancelledDownload.Cancel();
        Reject(() => UpdateService.DownloadAsset(assetUri, downloaded + "-cancel", 100, null, cancelledDownload.Token, new UpdateHttpStub(200, "body")).GetAwaiter().GetResult(), "更新下载取消");
    }
    string hashFile = Path.Combine(updateTemp, "package.zip"), sumFile = Path.Combine(updateTemp, "SHA256SUMS.txt");
    File.WriteAllText(hashFile, "test");
    File.WriteAllText(sumFile, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(hashFile))) + "  package.zip");
    UpdateService.VerifyHash(hashFile, sumFile, "package.zip"); Check(true, "更新校验正确哈希");
    File.AppendAllText(hashFile, "changed"); Reject(() => UpdateService.VerifyHash(hashFile, sumFile, "package.zip"), "更新校验拒绝损坏文件");
    string malformedZip = Path.Combine(updateTemp, "unsafe.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(malformedZip, System.IO.Compression.ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
    Reject(() => UpdateService.Extract(malformedZip, Path.Combine(updateTemp, "unsafe"), UpdateService.Current), "更新解压拒绝路径越界");
    string linkZip = Path.Combine(updateTemp, "link.zip");
    using (var zip = System.IO.Compression.ZipFile.Open(linkZip, System.IO.Compression.ZipArchiveMode.Create)) zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xa1ff0000);
    Reject(() => UpdateService.Extract(linkZip, Path.Combine(updateTemp, "link"), UpdateService.Current), "更新解压拒绝符号链接");
    string target = Path.Combine(updateTemp, "installed"), job = Path.Combine(updateTemp, "job"), payload = Path.Combine(job, "payload");
    Directory.CreateDirectory(target); Directory.CreateDirectory(payload);
    var managed = new[] { "WinCalendar.exe", "WinCalendar.dll", "WinCalendar.deps.json", "WinCalendar.runtimeconfig.json" };
    foreach (string file in managed) { File.WriteAllText(Path.Combine(target, file), "old"); File.WriteAllText(Path.Combine(payload, file), "new"); }
    File.WriteAllText(Path.Combine(target, UpdateService.ManifestName), System.Text.Json.JsonSerializer.Serialize(managed));
    File.WriteAllText(Path.Combine(payload, UpdateService.ManifestName), System.Text.Json.JsonSerializer.Serialize(managed));
    File.WriteAllText(Path.Combine(target, "personal.txt"), "keep");
    var journal = UpdateInstaller.PrepareFiles(job, target);
    // 篡改检查只作用于隔离目录；失败不得改变私人附加文件。
    string originalJournal = File.ReadAllText(UpdateInstaller.JournalPath(job));
    var wrongTarget = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(originalJournal)!;
    wrongTarget.Target = target + "-other";
    Reject(() => UpdateSecurity.Validate(wrongTarget, target), "更新拒绝篡改目标目录");
    var wrongFiles = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(originalJournal)!;
    wrongFiles.Files = wrongFiles.Files.Append("personal.txt").ToArray();
    Reject(() => UpdateSecurity.Validate(wrongFiles, target), "更新拒绝清单外文件");
    wrongFiles = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(originalJournal)!;
    wrongFiles.OldFiles["../escape"] = new UpdateFile(0, new string('0', 64));
    Reject(() => UpdateSecurity.Validate(wrongFiles, target), "恢复拒绝路径跳转");
    var wrongPhase = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(originalJournal)!;
    wrongPhase.Phase = "unknown";
    Reject(() => UpdateSecurity.Validate(wrongPhase, target), "更新拒绝非法阶段");
    File.WriteAllText(UpdateInstaller.JournalPath(job), "{}");
    Reject(() => UpdateInstaller.ReadJournal(job, target), "拒绝旧恢复格式");
    File.WriteAllText(UpdateInstaller.JournalPath(job), new string(' ', 4 * 1024 * 1024 + 1));
    Reject(() => UpdateInstaller.ReadJournal(job, target), "拒绝超大恢复记录");
    File.WriteAllText(UpdateInstaller.JournalPath(job), originalJournal);
    using (var self = System.Diagnostics.Process.GetCurrentProcess())
        Reject(() => UpdateSecurity.Find(self.Id, self.StartTime.ToUniversalTime().Ticks, Path.Combine(target, "WinCalendar.exe")), "拒绝无关进程路径");
    File.WriteAllText(Path.Combine(payload, "WinCalendar.dll"), "bad");
    Reject(() => UpdateInstaller.InstallFiles(job, journal), "更新拒绝载荷摘要不符");
    Check(File.ReadAllText(Path.Combine(target, "WinCalendar.exe")) == "old", "校验失败不替换文件");
    File.WriteAllText(Path.Combine(payload, "WinCalendar.dll"), "new");
    UpdateInstaller.InstallFiles(job, journal);
    Check(File.ReadAllText(Path.Combine(target, "WinCalendar.exe")) == "new" && File.ReadAllText(Path.Combine(target, "personal.txt")) == "keep", "更新替换且保留额外文件");
    var backupDll = Path.Combine(job, "backup", "WinCalendar.dll");
    File.Delete(backupDll);
    Reject(() => UpdateInstaller.Rollback(job, journal), "恢复拒绝缺失备份");
    File.WriteAllText(backupDll, "bad");
    Reject(() => UpdateInstaller.Rollback(job, journal), "恢复拒绝备份摘要不符");
    Check(File.ReadAllText(Path.Combine(target, "WinCalendar.exe")) == "new", "备份校验失败不部分回滚");
    File.WriteAllText(backupDll, "old");
    UpdateInstaller.Rollback(job, journal);
    UpdateInstaller.Rollback(job, journal);
    Check(File.ReadAllText(Path.Combine(target, "WinCalendar.exe")) == "old" && journal.Phase == "rolledback", "更新中断恢复可重复执行");
    // 文件被占用时安装失败，备份及恢复记录仍可用于还原。
    using (var locked = new FileStream(Path.Combine(target, "WinCalendar.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        Reject(() => UpdateInstaller.InstallFiles(job, journal), "更新文件占用触发失败");
    UpdateInstaller.Rollback(job, journal);
    Check(File.ReadAllText(Path.Combine(target, "WinCalendar.exe")) == "old", "更新部分替换失败后回滚");

    var link = Path.Combine(updateTemp, "directory-link");
    try
    {
        Directory.CreateSymbolicLink(link, target);
        Reject(() => UpdateSecurity.VerifyManifest(link, journal.OldFiles), "恢复拒绝目录链接");
    }
    catch (Exception e) when (e is UnauthorizedAccessException || (e is IOException && (e.HResult & 0xffff) == 1314)) { Console.WriteLine("SKIP 目录链接实测：当前环境不允许创建符号链接"); }
    finally { if (Directory.Exists(link)) Directory.Delete(link); }

    // 使用检查程序自身的 apphost 模拟新旧版本，运行真实更新工作流程。
    foreach (int updateMode in new[] { 0, 1, 2 })
    {
        bool failStartup = updateMode == 1;
        bool recovering = updateMode == 2;
        string transaction = Path.Combine(UpdateService.Root, Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(updateTemp, "process-" + updateMode);
        string incoming = Path.Combine(transaction, "payload");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(incoming);
        foreach (string sourceFile in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(AppContext.BaseDirectory, sourceFile);
            foreach (string destination in new[] { installed, incoming })
            {
                var file = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(sourceFile, file);
            }
        }
        foreach (string destination in new[] { installed, incoming })
        {
            File.Copy(Path.Combine(destination, "Checks.exe"), Path.Combine(destination, "WinCalendar.exe"), true);
            var listing = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(destination, p).Replace('\\', '/')).ToArray();
            File.WriteAllText(Path.Combine(destination, UpdateService.ManifestName), System.Text.Json.JsonSerializer.Serialize(listing));
        }
        if (updateMode == 0)
        {
            string validZip = Path.Combine(updateTemp, "valid.zip");
            System.IO.Compression.ZipFile.CreateFromDirectory(incoming, validZip);
            UpdateService.Extract(validZip, Path.Combine(updateTemp, "valid-extract"), UpdateService.Current);
            Check(true, "更新完整包清单及程序集版本验证");
            Reject(() => UpdateService.Extract(validZip, Path.Combine(updateTemp, "wrong-version"), new Version(99, 0, 0)), "更新包内版本不符");
        }
        if (failStartup) File.WriteAllText(Path.Combine(installed, "skip-ack"), "test");
        if (recovering)
        {
            var interrupted = UpdateInstaller.PrepareFiles(transaction, installed);
            UpdateInstaller.InstallFiles(transaction, interrupted);
        }
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(installed, "WinCalendar.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        info.Environment["WINCALENDAR_CHECK_ROOT"] = Store.Root;
        info.ArgumentList.Add(recovering ? "--test-recover-parent" : "--test-update-parent"); info.ArgumentList.Add(transaction);
        using var parent = System.Diagnostics.Process.Start(info)!;
        Check(parent.WaitForExit(30000) && parent.ExitCode == 0, "更新父进程验证及正常退出 " + failStartup);
        UpdateJournal? finished = null;
        var deadline = DateTime.UtcNow.AddSeconds(55);
        while (DateTime.UtcNow < deadline)
        {
            // 观察进度不占用写入权限，避免检查本身干扰工作进程原子替换记录。
            try { using var statusFile = new FileStream(UpdateInstaller.JournalPath(transaction), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); finished = System.Text.Json.JsonSerializer.Deserialize<UpdateJournal>(statusFile); } catch (IOException) { }
            if (finished?.Phase is "complete" or "rolledback") break;
            Thread.Sleep(100);
        }
        Check(finished?.Phase == (failStartup || recovering ? "rolledback" : "complete"), "更新工作进程启动确认、回滚及中断恢复 " + updateMode);
        // 等待测试子进程退出，之后才能清理临时程序集。
        Thread.Sleep(1800);
    }

    Check(L.Match("zh-CN") == "zh-Hans" && L.Match("zh-SG") == "zh-Hans", "简中匹配");
    Check(L.Match("zh-TW") == "zh-Hant" && L.Match("zh-HK") == "zh-Hant", "繁中匹配");
    Check(L.Match("ja-JP") == "ja" && L.Match("fr-FR") == "en", "日文和英文回退");
    var keys = L.Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<DictionaryEntry>().Select(x => (string)x.Key).Order().ToArray();
    foreach (var language in new[] { "zh-Hans", "zh-Hant", "ja" })
    {
        var set = L.Resources.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!;
        Check(keys.SequenceEqual(set.Cast<DictionaryEntry>().Select(x => (string)x.Key).Order()), "资源完整 " + language);
        Check(set.Cast<DictionaryEntry>().All(x => !string.IsNullOrWhiteSpace(x.Value?.ToString())), "无空翻译 " + language);
    }
    Check(new DateTime(2026, 9, 7).ToString("d", CultureInfo.GetCultureInfo("ja-JP")) == "2026/09/07", "日本区域日期格式");
    Check(MainWindow.GridStart(new DateTime(2026, 9, 1), DayOfWeek.Sunday).AddDays(41) == new DateTime(2026, 10, 10), "订阅覆盖完整六周网格");
    // 覆盖浏览范围内的闰年、跨年及七种周起始日，并验证相邻网格完整包含。
    bool rangesValid = true;
    for (int year = 1901; year <= 2100; year++)
    for (int m = 1; m <= 12; m++)
    for (int first = 0; first < 7; first++)
    {
        var center = new DateTime(year, m, 1);
        var range = MainWindow.PreloadRange(center, (DayOfWeek)first);
        rangesValid &= (range.To - range.From).TotalDays <= 112;
        for (int offset = -1; offset <= 1; offset++)
        {
            var start = MainWindow.GridStart(center.AddMonths(offset), (DayOfWeek)first);
            rangesValid &= range.From <= start && range.To >= start.AddDays(42);
        }
    }
    Check(rangesValid, "相邻月份预加载覆盖全部网格及范围边界");
    Check(System.Text.Json.JsonSerializer.Deserialize<Subscription>("{}")!.Kind == SubscriptionKind.Ordinary, "旧订阅默认普通日历");
    Check(Download.Validate("webcal://example.com/a.ics?secret=test").Scheme == "https", "Webcal 标准化");
    Reject(() => Download.Validate("http://example.com/a.ics"), "拒绝明文 HTTP");
    Reject(() => Download.Validate("file:///C:/private.ics"), "拒绝文件地址");
    Reject(() => Download.Validate("https://user:password@example.com/a"), "拒绝 URL 用户密码");
    var cfg = Store.Load(); cfg.Sources.Add(new Subscription { Name = "测试", Url = "https://example.com/a.ics" }); Store.Save(cfg);
    Check(Store.Load().Sources[0].Name == "测试", "配置持久化");
    File.WriteAllText(Store.PathFor("settings.json"), "broken");
    Reject(() => Store.Load(), "配置损坏不覆盖");
    Check(File.ReadAllText(Store.PathFor("settings.json")) == "broken", "保留损坏配置供恢复");
    File.Delete(Store.PathFor("settings.json")); // 后续持久化检查重新创建有效配置。

    string Wrap(string events) => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//WinCalendar Checks//EN\r\n" + events.Replace("\n", "\r\n") + "\r\nEND:VCALENDAR\r\n";
    string allDay = Wrap("BEGIN:VEVENT\nUID:all\nDTSTART;VALUE=DATE:20260907\nDTEND;VALUE=DATE:20260909\nSUMMARY:原文 title\nEND:VEVENT");
    var from = new DateTime(2026, 9, 1); var to = from.AddMonths(1);
    var a = IcsParser.Parse(allDay, from, to, "one", "#2563EB").Single();
    Check(a.On(new DateTime(2026, 9, 7)) && a.On(new DateTime(2026, 9, 8)) && !a.On(new DateTime(2026, 9, 9)), "全天结束日期排他");
    Check(a.Title == "原文 title", "订阅文本不翻译");
    var recurrence = Wrap("BEGIN:VEVENT\nUID:repeat\nDTSTART:20260901T090000\nDTEND:20260901T100000\nRRULE:FREQ=DAILY;COUNT=5\nEXDATE:20260903T090000\nSUMMARY:Repeat\nEND:VEVENT");
    var repeats = IcsParser.Parse(recurrence, from, to, "one", "#2563EB");
    Check(repeats.Count == 4 && repeats.All(x => x.Start.Day != 3), "重复与例外日期");
    Check(IcsParser.Parse(recurrence, from, to, "one", "#2563EB").Select(x => x.Id).SequenceEqual(repeats.Select(x => x.Id)), "刷新稳定标识");
    var zoned = Wrap("BEGIN:VEVENT\nUID:timezone\nDTSTART;TZID=America/New_York:20260907T090000\nDTEND;TZID=America/New_York:20260907T100000\nSUMMARY:Meeting\nEND:VEVENT");
    var z = IcsParser.Parse(zoned, from, to, "one", "#2563EB").Single();
    var expected = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 9, 7, 13, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local);
    Check(z.Start == expected, "跨时区转换");
    var overnight = Wrap("BEGIN:VEVENT\nUID:night\nDTSTART:20260907T230000\nDTEND:20260908T020000\nSUMMARY:Night\nEND:VEVENT");
    var n = IcsParser.Parse(overnight, from, to, "one", "#2563EB").Single();
    Check(n.On(new DateTime(2026, 9, 7)) && n.On(new DateTime(2026, 9, 8)), "跨天日程");
    var cancelled = Wrap("BEGIN:VEVENT\nUID:cancelled\nDTSTART:20260907T090000\nDTEND:20260907T100000\nSTATUS:CANCELLED\nSUMMARY:Cancelled\nEND:VEVENT");
    Check(IcsParser.Parse(cancelled, from, to, "one", "blue").Count == 0, "取消事件不显示");
    var boundary = Wrap("BEGIN:VEVENT\nUID:boundary\nDTSTART:20260831T160000Z\nDTEND:20260831T170000Z\nSUMMARY:Boundary\nEND:VEVENT");
    if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.FromHours(9)) Check(IcsParser.Parse(boundary, from, to, "one", "blue").Count == 1, "跨时区月份边界");
    var longEvent = Wrap("BEGIN:VEVENT\nUID:long\nDTSTART;VALUE=DATE:20260801\nDTEND;VALUE=DATE:20261002\nSUMMARY:Long\nEND:VEVENT");
    Check(IcsParser.Parse(longEvent, from, to, "one", "blue").Count == 1, "早于可视月份开始的跨月事件");
    var overridden = Wrap("BEGIN:VEVENT\nUID:override\nDTSTART:20260901T090000\nDTEND:20260901T100000\nRRULE:FREQ=DAILY;COUNT=3\nSUMMARY:Original\nEND:VEVENT\nBEGIN:VEVENT\nUID:override\nRECURRENCE-ID:20260902T090000\nDTSTART:20260902T140000\nDTEND:20260902T150000\nSUMMARY:Changed\nEND:VEVENT");
    var changed = IcsParser.Parse(overridden, from, to, "one", "blue");
    Check(changed.Count == 3 && changed.Single(x => x.Start.Day == 2).Start.Hour == 14, "单次重复事件改期");
    var folded = Wrap("BEGIN:VEVENT\nUID:fold\nDTSTART;VALUE=DATE:20260907\nDTEND;VALUE=DATE:20260908\nSUMMARY:中文\n 续行\nEND:VEVENT");
    Check(IcsParser.Parse(folded, from, to, "one", "blue").Single().Title == "中文续行", "ICS 折行文本");
    Reject(() => IcsParser.Parse("not ICS", from, to, "one", "blue"), "非法 ICS");
    Reject(() => IcsParser.Parse(allDay, from, from.AddYears(1), "one", "blue"), "限制展开范围");

    Check(IcsParser.Parse(allDay, from, from.AddDays(112), "one", "blue").Count == 1, "允许112天预加载解析");
    Reject(() => IcsParser.Parse(allDay, from, from.AddDays(113), "one", "blue"), "拒绝超过112天展开");
    var preloadIcs = Wrap("BEGIN:VEVENT\nUID:preload\nDTSTART;VALUE=DATE:20260101\nDTEND;VALUE=DATE:20260102\nRRULE:FREQ=DAILY;COUNT=730\nSUMMARY:假日 假期 第1天/共1天\nEND:VEVENT");
    // 连续前翻、后翻及跨年切换，检查每次加载的首尾、休班和事件去重。
    foreach (int offset in new[] { 0, 1, 2, 5, 4, 0 })
    {
        var range = MainWindow.PreloadRange(from.AddMonths(offset), DayOfWeek.Monday);
        var loaded = IcsParser.Parse(preloadIcs, range.From, range.To, "one", "blue", SubscriptionKind.ChinaHolidays);
        Check(loaded.Any(e => e.HolidayOn(range.From)) && loaded.Any(e => e.HolidayOn(range.To.AddDays(-1))) &&
            loaded.All(e => e.IsOffDay == true) && loaded.Select(e => e.Id).Distinct().Count() == loaded.Count,
            "连续切换预加载假日边界及去重 " + offset);
    }

    // 用完整 ICS 样本校验类型、源日期与日期格聚合，避免依赖当前系统时区。
    string HolidayIcs(string title, string dates = "DTSTART;VALUE=DATE:20260925\nDTEND;VALUE=DATE:20260928") => Wrap("BEGIN:VEVENT\nUID:holiday\n" + dates + "\nSUMMARY:" + title + "\nDESCRIPTION:假期 补班 不参与识别\nEND:VEVENT");
    var offIcs = HolidayIcs("中秋节 假期 第1天/共3天");
    var off = IcsParser.Parse(offIcs, from, to, "a", "blue", SubscriptionKind.ChinaHolidays).Single();
    Check(off.IsOffDay == true && off.HolidayName == "中秋节", "按完整假期标题识别");
    Check(off.HolidayOn(new DateTime(2026, 9, 27)) && !off.HolidayOn(new DateTime(2026, 9, 28)), "假期源日期结束排他");
    Check(IcsParser.Parse(offIcs, from, to, "a", "blue").Single().HolidayName == null, "普通订阅不生成角标");
    foreach (var title in new[] { "中秋节", "中秋节 假期", "中秋节 假期 第4天/共3天", "中秋节 假期 第1天/共3天 会议" })
        Check(IcsParser.Parse(HolidayIcs(title), from, to, "a", "blue", SubscriptionKind.ChinaHolidays).Single().HolidayName == null, "未知标题不猜测 " + title);
    var work = IcsParser.Parse(HolidayIcs("中秋节 补班 第1天/共1天", "DTSTART;TZID=Pacific/Kiritimati:20260925T003000\nDTEND;TZID=Pacific/Kiritimati:20260925T013000"), from, to, "b", "blue", SubscriptionKind.ChinaHolidays).Single();
    Check(work.IsOffDay == false && work.SourceStart == new DateTime(2026, 9, 25) && work.SourceEnd == new DateTime(2026, 9, 26), "定时补班按源日期标识");
    Check(work.Start == TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 9, 24, 10, 30, 0, DateTimeKind.Utc), TimeZoneInfo.Local), "补班详情按本地时区");
    var crossYear = IcsParser.Parse(HolidayIcs("元旦 假期 第1天/共3天", "DTSTART;VALUE=DATE:20261231\nDTEND;VALUE=DATE:20270103"), new DateTime(2027, 1, 1), new DateTime(2027, 2, 1), "a", "blue", SubscriptionKind.ChinaHolidays).Single();
    Check(crossYear.HolidayOn(new DateTime(2027, 1, 2)) && !crossYear.HolidayOn(new DateTime(2027, 1, 3)), "跨年订阅日期");
    var sources = new Settings();
    var subscriptionA = new Subscription { Id = "a", Kind = SubscriptionKind.ChinaHolidays };
    var subscriptionB = new Subscription { Id = "b", Kind = SubscriptionKind.ChinaHolidays };
    var calendar = new Subscriptions(sources);
    var date = new DateTime(2026, 9, 25);
    calendar.Events.Add(off);
    Check(calendar.HolidayForDay(date).Names.Length == 0, "无订阅时不显示节假日");
    sources.Sources.Add(subscriptionA);
    Check(calendar.HolidayForDay(date).IsOffDay == true, "添加中国节假日订阅生效");
    subscriptionA.Enabled = false;
    Check(calendar.HolidayForDay(date).Names.Length == 0, "停用后立即移除标记");
    subscriptionA.Enabled = true; subscriptionA.Kind = SubscriptionKind.Ordinary;
    Check(calendar.HolidayForDay(date).Names.Length == 0, "改回普通类型立即移除标记");
    subscriptionA.Kind = SubscriptionKind.ChinaHolidays;
    sources.Sources.Add(subscriptionB); calendar.Events.Add(work);
    Check(calendar.HolidayForDay(date).Conflict && calendar.HolidayForDay(date).IsOffDay == null && calendar.ForDay(date).Count() == 2, "休班冲突隐藏角标并保留详情");
    calendar.Events.Remove(work); calendar.Events.Add(off with { SourceId = "b", HolidayName = "其他节日" });
    Check(!calendar.HolidayForDay(date).Conflict && calendar.HolidayForDay(date).Names.SequenceEqual(new[] { "中秋节", "其他节日" }), "同类合并且名称按订阅顺序");
    sources.Sources.Clear();
    Check(calendar.HolidayForDay(date).Names.Length == 0 && !calendar.ForDay(date).Any(), "删除订阅立即移除内容");
    cfg.Sources[0].Kind = SubscriptionKind.ChinaHolidays; Store.Save(cfg);
    Check(Store.Load().Sources[0].Kind == SubscriptionKind.ChinaHolidays, "订阅类型持久化");
    // 农历日期独立于订阅配置，显式语言参数避免依赖检查机器语言。
    var zh = CultureInfo.GetCultureInfo("zh-Hans");
    Check(!System.Text.Json.JsonSerializer.Deserialize<Settings>("{}")!.ShowChineseLunar, "旧配置农历默认关闭");
    cfg.ShowChineseLunar = true; Store.Save(cfg);
    Check(Store.Load().ShowChineseLunar && Store.Load().Sources[0].Kind == SubscriptionKind.ChinaHolidays, "农历设置重载且不改变订阅");
    var draft = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
    draft.ShowChineseLunar = false;
    Check(cfg.ShowChineseLunar && Store.Load().ShowChineseLunar, "取消编辑不修改当前农历设置");
    Check(ChineseLunar.Label(new DateTime(2026, 2, 17), zh) == "正月", "农历新年月初");
    Check(ChineseLunar.Label(new DateTime(2026, 2, 16), zh) == "廿九", "农历年界");
    Check(ChineseLunar.Label(new DateTime(2026, 9, 25), zh) == "十五", "仅日期不生成中秋节名称");
    var lc = new ChineseLunisolarCalendar();
    var leapMonth = lc.GetLeapMonth(2025);
    var leapDate = lc.ToDateTime(2025, leapMonth, 1, 0, 0, 0, 0);
    Check(ChineseLunar.Label(leapDate, zh) == "闰六月", "闰月月序");
    Check(ChineseLunar.Label(lc.ToDateTime(2025, leapMonth + 1, 1, 0, 0, 0, 0), zh) == "七月", "闰月后月序");
    Check(ChineseLunar.Label(lc.MinSupportedDateTime.AddDays(-1), zh) == "" && ChineseLunar.Label(lc.MaxSupportedDateTime.AddDays(1), zh) == "", "农历支持范围外安全留空");
    Check(ChineseLunar.Label(lc.MinSupportedDateTime, zh) != "" && ChineseLunar.Label(lc.MaxSupportedDateTime, zh) != "", "农历支持范围边界");
    Check(ChineseLunar.Label(leapDate, CultureInfo.GetCultureInfo("zh-Hant")) == "閏六月" && ChineseLunar.Label(leapDate, CultureInfo.GetCultureInfo("en")) == "Leap Month 6" && ChineseLunar.Label(leapDate, CultureInfo.GetCultureInfo("ja")) == "閏6月", "四语言农历月份");
    var lightTint = Ui.SubscriptionBackground("#2563EB", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 251))).Color;
    var darkTint = Ui.SubscriptionBackground("#2563EB", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32))).Color;
    Check(lightTint.B > lightTint.R && lightTint.R > 180 && darkTint.B > darkTint.R && darkTint.B < 100, "订阅背景保留蓝色且适配深浅主题");
    Check(SubscriptionPreset.All.Count == 3 && new Settings().Sources.Count == 0, "常用订阅不自动添加");
    var chinaPreset = SubscriptionPreset.All[0].Create();
    Check(chinaPreset.Kind == SubscriptionKind.ChinaHolidays && chinaPreset.Color == "#2563EB" && chinaPreset.Name == L.T("PresetChina"), "中国预置字段");
    Check(SubscriptionPreset.All[1].Create().Kind == SubscriptionKind.Ordinary && SubscriptionPreset.All[1].Color == "#DC2626" && SubscriptionPreset.All[2].Color == "#8B5CF6", "日美预置类型与颜色");
    chinaPreset.Name = "用户名称";
    Check(SubscriptionPreset.All[0].Create().Name != chinaPreset.Name, "预填副本独立");
    Check(Download.SameUrl("webcal://EXAMPLE.COM:443/a.ics?Token=A", "https://example.com/a.ics?Token=A"), "标准化订阅地址比较");
    Check(!Download.SameUrl("https://example.com/A.ics", "https://example.com/a.ics") && !Download.SameUrl("https://example.com/a?Token=A", "https://example.com/a?Token=a"), "路径和令牌大小写不同不合并");
    Check(!Download.SameUrl("invalid", SubscriptionPreset.All[0].Url), "损坏旧链接不阻止使用预置");
    var japanOptions = SubscriptionPreset.All[1];
    Check(japanOptions.Options.Count == 3 && japanOptions.Options[0].Url == japanOptions.Url, "日本三个来源及默认地址");
    Check(japanOptions.Options.All(option => japanOptions.Matches(option.Url)) && japanOptions.Matches("webcal://www.officeholidays.com/ics-clean/japan"), "已有日本来源均可识别");
    Check(!japanOptions.Matches("https://example.com/custom.ics") && SubscriptionPreset.All.All(p => p.Options.Count > 1), "所有常用订阅支持多来源且自定义地址不误匹配");
    Check(SubscriptionPreset.All[0].Options.Count == 3 && SubscriptionPreset.All[2].Options.Count == 4, "中国与美国备选来源完整");
    Check(SubscriptionPreset.All.All(p => p.Options.All(o => p.Matches(o.Url))), "所有常用来源支持再次编辑识别");
    var baseBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 249, 251));
    Check(ReferenceEquals(Ui.SubscriptionBackground(Array.Empty<string>(), baseBrush), baseBrush), "无订阅沿用底色");
    Check(Ui.SubscriptionBackground(new[] { "#2563EB" }, baseBrush) is System.Windows.Media.SolidColorBrush, "单订阅纯色背景");
    foreach (int count in new[] { 2, 3, 4, 8 })
    {
        var colors = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? "#2563EB" : "#DC2626").ToArray();
        var brush = (System.Windows.Media.LinearGradientBrush)Ui.SubscriptionBackground(colors, baseBrush);
        Check(brush.GradientStops.Count == 2 * Math.Min(count, 3) && Enumerable.Range(0, Math.Min(count, 3)).All(i => brush.GradientStops[2 * i].Offset == (double)i / Math.Min(count, 3) && brush.GradientStops[2 * i + 1].Offset == (double)(i + 1) / Math.Min(count, 3) && brush.GradientStops[2 * i].Color == Ui.SubscriptionBackground(colors[i], baseBrush).Color), "最多三个等宽分区与颜色顺序 " + count);
    }
    sources.Sources.Add(subscriptionB); sources.Sources.Add(subscriptionA);
    calendar.Events.Add(off with { Id = "same-source-another-event" });
    Check(calendar.SourcesForDay(date).Select(x => x.Id).SequenceEqual(new[] { "b", "a" }), "按订阅顺序取色且同源事件去重");
    subscriptionB.Color = subscriptionA.Color;
    Check(calendar.SourcesForDay(date).Length == 2, "同色订阅分别保留分区");
    subscriptionB.Enabled = false;
    Check(calendar.SourcesForDay(date).Single().Id == "a", "停用来源移除分区");
    sources.Sources.Clear();
    Check(calendar.SourcesForDay(date).Length == 0, "删除来源移除分区");
    // WPF 控件在独立 STA 线程检查，不接管时钟、不读取私人订阅。
    Exception? pickerFailure = null;
    var pickerThread = new Thread(() =>
    {
        try
        {
            var window = new MainWindow(new Settings(), true, true);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
            void Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
            var chosen = (DateTime)Field("selected");
            Call("ChangeMonth", new DateTime(2026, 9, 1));
            var years = (System.Windows.Controls.ComboBox)Field("yearPicker");
            Check(years.Items.Count == 200 && years.Items[0].ToString() == "1901" && years.Items[199].ToString() == "2100", "年份下拉范围");
            years.SelectedIndex = 2027 - 1901;
            Check((DateTime)Field("month") == new DateTime(2027, 9, 1), "选择年份保留月份");
            var months = (System.Windows.Controls.ComboBox)Field("monthPicker");
            months.SelectedIndex = 11;
            Check((DateTime)Field("month") == new DateTime(2027, 12, 1), "选择月份保留年份");
            Call("MoveMonth", 1);
            Check((DateTime)Field("month") == new DateTime(2028, 1, 1) && ((System.Windows.Controls.ComboBox)Field("monthPicker")).SelectedIndex == 0, "箭头跨年同步下拉");
            var stable = Field("yearPicker");
            Call("ChangeMonth", new DateTime(2028, 1, 1));
            Check(ReferenceEquals(stable, Field("yearPicker")), "相同年月不重建或加载");
            Call("ChangeMonth", new DateTime(1901, 1, 1)); Call("MoveMonth", -1);
            Check((DateTime)Field("month") == new DateTime(1901, 1, 1), "年份下限不越界");
            Call("ChangeMonth", new DateTime(2100, 12, 1)); Call("MoveMonth", 1);
            Check((DateTime)Field("month") == new DateTime(2100, 12, 1), "年份上限不越界");
            Check((DateTime)Field("selected") == chosen, "年月切换保留选中日期");
            // 模拟焦点装饰显示前后，检查头部尺寸及后续内容的位置不变。
            foreach (bool dark in new[] { false, true })
            foreach (var label in new[] { "‹", L.T("Today"), "›" })
            {
                Ui.Theme(window, dark);
                var host = new System.Windows.Controls.StackPanel { Resources = window.Resources };
                var button = Ui.Button(label, "Today", () => { });
                var below = new System.Windows.Controls.Border { Height = 20 };
                host.Children.Add(button); host.Children.Add(below);
                void Layout()
                {
                    host.Measure(new System.Windows.Size(400, 200));
                    host.Arrange(new System.Windows.Rect(0, 0, 400, 200)); host.UpdateLayout();
                }
                Layout();
                var beforeSize = button.DesiredSize;
                var beforePosition = below.TransformToAncestor(host).Transform(new System.Windows.Point());
                var decoration = (System.Windows.Controls.Border)button.Template.FindName("ButtonFocusBorder", button);
                var focusTrigger = button.Template.Triggers.OfType<System.Windows.Trigger>().Single(t => t.Property == System.Windows.UIElement.IsKeyboardFocusedProperty);
                Check(focusTrigger.Setters.OfType<System.Windows.Setter>().All(s => s.TargetName == "ButtonFocusBorder" && s.Property == System.Windows.UIElement.OpacityProperty), "焦点只改变覆盖层 " + label + dark);
                decoration.Opacity = 1; Layout();
                Check(button.DesiredSize == beforeSize && below.TransformToAncestor(host).Transform(new System.Windows.Point()) == beforePosition && button.BorderThickness == new System.Windows.Thickness(0) && button.Focusable,
                    "按钮焦点前后尺寸和内容位置稳定 " + label + dark);
            }
            window.Cleanup();
        }
        catch (Exception e) { pickerFailure = e; }
    });
    pickerThread.SetApartmentState(ApartmentState.STA); pickerThread.Start(); pickerThread.Join();
    if (pickerFailure != null) throw pickerFailure;
    Console.WriteLine($"{passed} checks passed");
}
catch (Exception e) { Console.WriteLine("FAIL " + e.Message); Environment.ExitCode = 1; }
finally { if (Directory.Exists(Store.Root)) Directory.Delete(Store.Root, true); }

// 固定 HTTP 响应覆盖错误路径，不建立外部测试服务。
sealed class UpdateHttpStub(int status, string body) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new System.Net.Http.StringContent(body) });
    }
}

// 模拟重定向，验证不安全目标在下一次请求之前被拒绝。
sealed class SubscriptionRedirectStub(bool privateTarget) : System.Net.Http.HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        var response = new System.Net.Http.HttpResponseMessage(Calls == 1 ? System.Net.HttpStatusCode.Redirect : System.Net.HttpStatusCode.OK);
        if (Calls == 1) response.Headers.Location = new Uri(privateTarget ? "https://127.0.0.1/private" : "https://other.example/b");
        else response.Content = new System.Net.Http.StringContent("calendar");
        return Task.FromResult(response);
    }
}
