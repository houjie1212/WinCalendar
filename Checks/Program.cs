using System.Collections;
using System.Globalization;
using WinCalendar;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception(name); }
L.Reload();
Store.Root = Path.Combine(Path.GetTempPath(), "WinCalendarChecks-" + Guid.NewGuid().ToString("N"));
try
{
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
    Console.WriteLine($"{passed} checks passed");
}
catch (Exception e) { Console.WriteLine("FAIL " + e.Message); Environment.ExitCode = 1; }
finally { if (Directory.Exists(Store.Root)) Directory.Delete(Store.Root, true); }
