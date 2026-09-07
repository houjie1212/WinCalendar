using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WinCalendar;

public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "#2563EB";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastSuccess { get; set; }
    [JsonIgnore] public bool Failed { get; set; }
}
public sealed class Settings
{
    public List<Subscription> Sources { get; set; } = new();
    public int RefreshMinutes { get; set; } = 30;
    public int? FirstDay { get; set; }
}
public static class Store
{
    public static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCalendar");
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string PathFor(string name) => Path.Combine(Root, name);
    public static Settings Load()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(PathFor("settings.json"))) return new();
        // 损坏配置不静默覆盖，调用方展示错误后退出，方便用户恢复私人订阅。
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathFor("settings.json")), Json) ?? throw new InvalidDataException();
        s.Sources ??= new();
        s.RefreshMinutes = Math.Clamp(s.RefreshMinutes, 5, 1440);
        if (s.FirstDay is < 0 or > 6) s.FirstDay = null;
        foreach (var source in s.Sources)
            if (!Guid.TryParseExact(source.Id, "N", out _)) throw new InvalidDataException();
        if (s.Sources.Select(x => x.Id).Distinct().Count() != s.Sources.Count) throw new InvalidDataException();
        return s;
    }
    public static void Save(Settings settings) => Write(PathFor("settings.json"), JsonSerializer.Serialize(settings, Json));
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
public static class Download
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    public static Uri Validate(string value)
    {
        value = value.Trim();
        if (value.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[9..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("InvalidUrl");
        return uri;
    }
    public static async Task<string> Text(Uri uri, CancellationToken ct = default)
    {
        const int limit = 5 * 1024 * 1024;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        for (int hop = 0; hop < 5; hop++)
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = Validate(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > limit) throw new InvalidDataException();
                memory.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
        }
        throw new InvalidDataException();
    }
}
public sealed record Holiday([property: JsonRequired] string Name, [property: JsonRequired] DateTime Date, [property: JsonRequired] bool IsOffDay);
public sealed class HolidayYear
{
    public int Year { get; set; }
    public List<string> Papers { get; set; } = new();
    public List<Holiday> Days { get; set; } = new();
}
public sealed class Holidays
{
    private readonly Dictionary<int, HolidayYear> years = new();
    public DateTimeOffset LastCheck { get; private set; }
    public bool Failed { get; private set; }
    public Holidays()
    {
        foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames().Where(x => x.Contains(".Data.") && x.EndsWith(".json")))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var year = Parse(reader.ReadToEnd()); years[year.Year] = year;
        }
        if (Directory.Exists(Store.Root)) foreach (var file in Directory.GetFiles(Store.Root, "holiday-*.json"))
            try { var year = Parse(File.ReadAllText(file)); years[year.Year] = year; } catch { Failed = true; }
    }
    public static HolidayYear Parse(string json)
    {
        var y = JsonSerializer.Deserialize<HolidayYear>(json, Store.Json) ?? throw new InvalidDataException();
        if (y.Year is < 1900 or > 2200 || y.Papers.Count == 0 || y.Days.Count == 0 || y.Days.Any(x => string.IsNullOrWhiteSpace(x.Name) || Math.Abs(x.Date.Year - y.Year) > 1)) throw new InvalidDataException();
        return y;
    }
    public bool Known(int year) => years.ContainsKey(year);
    public Holiday? Get(DateTime date) => years.OrderByDescending(x => x.Key).SelectMany(x => x.Value.Days).FirstOrDefault(x => x.Date.Date == date.Date);
    public async Task Refresh(int visibleYear)
    {
        Failed = false;
        foreach (var year in new[] { visibleYear - 1, visibleYear, visibleYear + 1 }.Distinct())
        {
            try
            {
                var json = await Download.Text(new Uri($"https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{year}.json"));
                var parsed = Parse(json);
                if (parsed.Year != year) throw new InvalidDataException();
                Store.Write(Store.PathFor($"holiday-{year}.json"), json);
                years[year] = parsed;
            }
            catch { if (year == visibleYear) Failed = true; }
        }
        LastCheck = DateTimeOffset.Now;
    }
    private static readonly ChineseLunisolarCalendar Lunar = new();
    public static string? TraditionalKey(DateTime date)
    {
        if (date < Lunar.MinSupportedDateTime || date > Lunar.MaxSupportedDateTime.AddDays(-1)) return null;
        int year = Lunar.GetYear(date), month = Lunar.GetMonth(date), day = Lunar.GetDayOfMonth(date), leap = Lunar.GetLeapMonth(year);
        if (Lunar.GetYear(date.AddDays(1)) != year) return "NewYearsEve";
        if (month == leap) return null;
        if (leap > 0 && month > leap) month--;
        return (month, day) switch
        {
            (1, 1) => "SpringFestival", (1, 15) => "Lantern", (5, 5) => "DragonBoat", (7, 7) => "Qixi",
            (7, 15) => "Ghost", (8, 15) => "MidAutumn", (9, 9) => "DoubleNinth", (12, 8) => "Laba", _ => null
        };
    }
    public static string LunarLabel(DateTime date)
    {
        if (date < Lunar.MinSupportedDateTime || date > Lunar.MaxSupportedDateTime) return "";
        int month = Lunar.GetMonth(date), day = Lunar.GetDayOfMonth(date), leap = Lunar.GetLeapMonth(Lunar.GetYear(date));
        bool isLeap = month == leap;
        if (leap > 0 && month >= leap) month--;
        if (L.Ui.Name.StartsWith("zh")) return day == 1 ? (isLeap ? L.T("Leap") : "") + L.T("LunarMonth" + month) : L.T("LunarDay" + day);
        return string.Format(L.T("LunarFormat"), month, day) + (isLeap ? " " + L.T("Leap") : "");
    }
    public string Label(DateTime date)
    {
        var traditional = TraditionalKey(date);
        if (traditional != null) return L.T(traditional);
        var holiday = Get(date);
        return holiday is { IsOffDay: true } ? L.Festival(holiday.Name) : LunarLabel(date);
    }
}
