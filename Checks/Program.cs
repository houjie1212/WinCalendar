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
    var holiday = new Holidays();
    Check(holiday.Get(new DateTime(2026, 10, 1))?.IsOffDay == true, "国庆放假");
    Check(holiday.Get(new DateTime(2026, 10, 10))?.IsOffDay == false, "周末补班");
    Check(holiday.Get(new DateTime(2026, 9, 6)) == null, "普通周末不标休");
    Check(!holiday.Known(2099), "未知年份不推算");
    Reject(() => Holidays.Parse("{\"year\":2026,\"papers\":[\"https://www.gov.cn/\"],\"days\":[{\"name\":\"节日\",\"date\":\"2026-01-01\"}]}"), "缺失休班字段不能默认为补班");
    Check(MainWindow.GridStart(new DateTime(2026, 9, 1), DayOfWeek.Sunday).AddDays(41) == new DateTime(2026, 10, 10), "订阅覆盖完整六周网格");
    Check(Holidays.TraditionalKey(new DateTime(2026, 2, 17)) == "SpringFestival", "春节");
    Check(Holidays.TraditionalKey(new DateTime(2026, 2, 16)) == "NewYearsEve", "除夕");
    Check(Holidays.TraditionalKey(new DateTime(2026, 9, 25)) == "MidAutumn", "中秋");
    var lunar = new ChineseLunisolarCalendar();
    var leap = lunar.GetLeapMonth(2023);
    Check(Holidays.TraditionalKey(lunar.ToDateTime(2023, leap, 1, 0, 0, 0, 0)) == null, "闰月不重复节日");
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
    Console.WriteLine($"{passed} checks passed");
}
catch (Exception e) { Console.WriteLine("FAIL " + e.Message); Environment.ExitCode = 1; }
finally { if (Directory.Exists(Store.Root)) Directory.Delete(Store.Root, true); }
