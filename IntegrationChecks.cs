using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinCalendar;

internal static class IntegrationChecks
{
    internal static int RenderPreviews(string folder)
    {
        // 直接渲染本程序控件，不改变系统语言、不接管输入、不读取私人订阅。
        Directory.CreateDirectory(folder);
        Store.Root = Path.Combine(Path.GetFullPath(folder), "unused-preview-cache");
        _ = new App();
        foreach (var language in new[] { "zh-Hans", "zh-Hant", "en", "ja" }) foreach (bool dark in new[] { false, true }) foreach (bool lunar in new[] { false, true })
        {
            L.PreviewLanguage(language);
            var window = new MainWindow(new Settings { ShowChineseLunar = lunar }, true, true);
            Ui.Theme(window, dark);
            var content = (System.Windows.UIElement)window.Content;
            window.Content = null;
            var root = new System.Windows.Controls.Border { Width = 440, Height = 710, Background = window.Background, Resources = window.Resources, Child = content };
            root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
            root.Measure(new System.Windows.Size(440, 710)); root.Arrange(new System.Windows.Rect(0, 0, 440, 710)); root.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(440, 710, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, language + (dark ? "-dark" : "-light") + (lunar ? "-lunar" : "") + ".png")); encoder.Save(stream);
            if (!lunar)
            {
                // 使用空白设置渲染常用订阅布局，不读取用户订阅。
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                var settingsWindow = new SettingsWindow(window, new Settings(), () => { }, () => Task.CompletedTask);
                var settingsContent = (System.Windows.UIElement)settingsWindow.Content;
                settingsWindow.Content = null;
                root.Child = settingsContent; root.Width = 520; root.Height = 650;
                root.Measure(new System.Windows.Size(520, 650)); root.Arrange(new System.Windows.Rect(0, 0, 520, 650)); root.UpdateLayout();
                bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(520, 650, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var settingsStream = File.Create(Path.Combine(folder, language + (dark ? "-dark" : "-light") + "-settings.png")); encoder.Save(settingsStream);
                settingsWindow.Close();
            }
            window.Cleanup();
        }
        // 构造零至六个来源的日期格，验证整块分色、选中边框和跨月淡化。
        L.PreviewLanguage("zh-Hans");
        foreach (bool dark in new[] { false, true }) foreach (int dpi in new[] { 96, 144 })
        {
            var config = new Settings { ShowChineseLunar = true };
            foreach (var color in new[] { "#2563EB", "#DC2626", "#8B5CF6", "#16834A", "#2563EB", "#C95D12" })
                config.Sources.Add(new Subscription { Name = "订阅 " + (config.Sources.Count + 1), Color = color, Kind = SubscriptionKind.ChinaHolidays });
            var window = new MainWindow(config, true, true); Ui.Theme(window, dark);
            var events = new List<AgendaEvent>();
            var first = MainWindow.GridStart(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), L.Format.DateTimeFormat.FirstDayOfWeek);
            for (int day = 0; day < 42; day++)
            {
                var date = first.AddDays(day);
                int count = date == DateTime.Today ? 3 : day % 7;
                foreach (var source in config.Sources.Take(count)) events.Add(new AgendaEvent(source.Id + day, "示例假期", "", "", date, date.AddDays(1), true, source.Id, source.Color, "示例假期", date, date.AddDays(1), true));
            }
            window.RenderPreviewEvents(events.ToArray());
            var content = (System.Windows.UIElement)window.Content; window.Content = null;
            var root = new System.Windows.Controls.Border { Width = 440, Height = 710, Background = window.Background, Resources = window.Resources, Child = content };
            root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, window.Foreground);
            root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, window.FontFamily);
            root.Measure(new System.Windows.Size(440, 710)); root.Arrange(new System.Windows.Rect(0, 0, 440, 710)); root.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(440 * dpi / 96, 710 * dpi / 96, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(folder, "multi-" + (dark ? "dark-" : "light-") + dpi + ".png")); encoder.Save(stream);
            window.Cleanup();
        }
        return 0;
    }
    // 使用隔离目录验证真实发布程序的解析子进程，不读取或改写用户订阅。
    public static async Task<int> Run(string report)
    {
        var checks = new List<string>();
        Store.Root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "integration-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Store.Root);
        void Assert(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add("PASS " + name); }
        try
        {
            var source = new Subscription { Name = "Integration", Url = "https://example.invalid/calendar.ics" };
            var input = Store.PathFor(source.Id + ".ics");
            var original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//WinCalendar//Checks//EN\r\nBEGIN:VEVENT\r\nUID:sample\r\nDTSTART;VALUE=DATE:20260907\r\nDTEND;VALUE=DATE:20260909\r\nSUMMARY:测试 Test\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
            File.WriteAllText(input, original);
            var from = new DateTime(2026, 9, 1); var to = from.AddMonths(1);
            var events = await IcsParser.Isolated(input, from, to, source);
            Assert(events.Count == 1 && events[0].Title == "测试 Test", "真实子进程与内存限制正常");
            var config = new Settings(); config.Sources.Add(source);
            var feed = new Subscriptions(config);
            await feed.Refresh(from, to, false);
            Assert(feed.Events.Count == 1, "离线缓存可读");
            await feed.Refresh(from, to, true);
            Assert(source.Failed && feed.Events.Count == 1 && File.ReadAllText(input) == original, "网络失败保留缓存");
            source.Kind = SubscriptionKind.ChinaHolidays;
            source.Url = "https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/holidayCal.ics";
            await feed.Refresh(from, to, true);
            Assert(!source.Failed && source.LastSuccess != null && feed.Events.Count > 0, "公开 HTTPS ICS 下载与解析");
            Assert(feed.HolidayForDay(new DateTime(2026, 9, 25)).IsOffDay == true && feed.HolidayForDay(new DateTime(2026, 9, 20)).IsOffDay == false, "真实订阅休班标识与子进程序列化");
            int count = feed.Events.Count;
            await feed.Refresh(from, to, true);
            Assert(feed.Events.Count == count, "连续刷新不重复");
            // 实际使用应用下载器和隔离解析器核验全部预置，不触碰用户配置。
            foreach (var preset in SubscriptionPreset.All) foreach (var option in preset.Options)
            {
                var sample = preset.Create(); sample.Url = option.Url;
                var path = Store.PathFor(sample.Id + ".ics");
                File.WriteAllText(path, await Download.Text(Download.Validate(sample.Url)));
                var parsed = await IcsParser.Isolated(path, from, to, sample);
                Assert(parsed.Count > 0 && (sample.Kind == SubscriptionKind.ChinaHolidays ? parsed.All(e => e.HolidayName != null) : parsed.All(e => e.HolidayName == null)), "公开预置解析 " + preset.NameKey + "/" + option.NameKey);
            }
            var saved = File.ReadAllText(input);
            source.Url = "https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/README.md";
            await feed.Refresh(from, to, true);
            Assert(source.Failed && feed.Events.Count == count && File.ReadAllText(input) == saved, "非法订阅更新保留旧缓存");
            var broken = Store.PathFor("broken.ics"); File.WriteAllText(broken, "invalid");
            bool rejected = false;
            try { await IcsParser.Isolated(broken, from, to, source); } catch { rejected = true; }
            Assert(rejected, "非法 ICS 子进程正常退出");
            var extreme = Store.PathFor("extreme.ics");
            File.WriteAllText(extreme, original.Replace("DTSTART;VALUE=DATE:20260907", "DTSTART:20260901T000000").Replace("DTEND;VALUE=DATE:20260909", "DTEND:20260901T000001\r\nRRULE:FREQ=SECONDLY;COUNT=5000000"));
            var watch = System.Diagnostics.Stopwatch.StartNew(); rejected = false;
            try { await IcsParser.Isolated(extreme, from, to, source); } catch { rejected = true; }
            Assert(rejected && watch.Elapsed < TimeSpan.FromSeconds(15), "极端重复规则受限且不阻塞主进程");
            File.WriteAllLines(report, checks); return 0;
        }
        catch (Exception e) { checks.Add("FAIL " + e.GetType().Name + (e is InvalidDataException ? " " + e.Message : "")); File.WriteAllLines(report, checks); return 1; }
        finally { Directory.Delete(Store.Root, true); }
    }
}
