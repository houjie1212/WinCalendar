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
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WinCalendar;

// 订阅类型决定是否参与日期格的节假日标识。
public enum SubscriptionKind { Ordinary, ChinaHolidays }
public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    // 仅解密失败时保留原密文；内存草稿复制需要保留这两个字段。
    public string ProtectedUrl { get; set; } = "";
    public bool UrlUnreadable { get; set; }
    public string Color { get; set; } = "#2563EB";
    public SubscriptionKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastSuccess { get; set; }
    [JsonIgnore] public bool Failed { get; set; }
    [JsonIgnore] public bool Blocked { get; set; }
}
// 常用订阅仅提供编辑初始值，不自动修改用户订阅列表。
public sealed record SubscriptionOption(string NameKey, string Url);
public sealed record SubscriptionPreset(string NameKey, string Url, string Color, SubscriptionKind Kind, SubscriptionOption[]? Alternatives = null)
{
    public IReadOnlyList<SubscriptionOption> Options => Alternatives ?? new[] { new SubscriptionOption("DefaultSource", Url) };
    public bool Matches(string url) => Options.Any(option => Download.SameUrl(option.Url, url));
    public Subscription Create() => new() { Name = L.T(NameKey), Url = Url, Color = Color, Kind = Kind };
    public static readonly IReadOnlyList<SubscriptionPreset> All = Array.AsReadOnly(new[]
    {
        new SubscriptionPreset("PresetChina", "https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/holidayCal.ics", "#2563EB", SubscriptionKind.ChinaHolidays, new[]
        {
            new SubscriptionOption("SourceChinaAll", "https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/holidayCal.ics"),
            new SubscriptionOption("SourceChinaOff", "https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/holidayCal-HO.ics"),
            new SubscriptionOption("SourceChinaWork", "https://raw.githubusercontent.com/lanceliao/china-holiday-calender/master/holidayCal-CO.ics")
        }),
        new SubscriptionPreset("PresetJapan", "https://www.officeholidays.com/ics/japan", "#DC2626", SubscriptionKind.Ordinary, new[]
        {
            new SubscriptionOption("SourceCountry", "https://www.officeholidays.com/ics/japan"),
            new SubscriptionOption("SourceClean", "https://www.officeholidays.com/ics-clean/japan"),
            new SubscriptionOption("SourceAll", "https://www.officeholidays.com/ics-all/japan")
        }),
        new SubscriptionPreset("PresetUsa", "https://www.officeholidays.com/ics-fed/usa", "#8B5CF6", SubscriptionKind.Ordinary, new[]
        {
            new SubscriptionOption("SourceUsFederal", "https://www.officeholidays.com/ics-fed/usa"),
            new SubscriptionOption("SourceUsGeneral", "https://www.officeholidays.com/ics/usa"),
            new SubscriptionOption("SourceUsClean", "https://www.officeholidays.com/ics-clean/usa"),
            new SubscriptionOption("SourceUsAll", "https://www.officeholidays.com/ics-all/usa")
        })
    });
}
public sealed class Settings
{
    public List<Subscription> Sources { get; set; } = new();
    public int RefreshMinutes { get; set; } = 30;
    public int? FirstDay { get; set; }
    public bool ShowChineseLunar { get; set; }
}
public sealed class SettingsStorageException(string key) : IOException
{
    public string Key { get; } = key;
}
public static class Store
{
    public static string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCalendar");
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string PathFor(string name) => Path.Combine(Root, name);
    public static Settings Load(bool migrate = true)
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(PathFor("settings.json"))) return new();
        var document = ReadDocument(PathFor("settings.json"));
        int format = Format(document);
        var sources = document["Sources"] as JsonArray;
        if (sources != null && sources.Any(n => n is not JsonObject ||
            (format == 1 ? ((JsonObject)n).ContainsKey("Url") : ((JsonObject)n).ContainsKey("ProtectedUrl")))) throw new InvalidDataException();
        var s = document.Deserialize<Settings>(Json) ?? throw new InvalidDataException();
        Validate(s);
        foreach (var source in s.Sources)
        {
            source.UrlUnreadable = false;
            if (format == 0) continue;
            source.Url = "";
            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(source.ProtectedUrl), null, DataProtectionScope.CurrentUser);
                try { source.Url = new UTF8Encoding(false, true).GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                source.ProtectedUrl = "";
            }
            catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
            {
                source.UrlUnreadable = true;
            }
        }
        // 先完整加密再替换，失败时不备份或覆盖旧明文文件。
        if (format == 0 && migrate)
        {
            try { Save(s); }
            catch { throw new SettingsStorageException("AddressMigrationFailed"); }
        }
        return s;
    }
    private static JsonObject ReadDocument(string path) => JsonNode.Parse(File.ReadAllText(path),
        new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject ?? throw new InvalidDataException();
    private static int Format(JsonObject document)
    {
        if (!document.ContainsKey("Format")) return 0;
        if (document["Format"] is JsonValue value && value.TryGetValue<int>(out int format) && format == 1) return format;
        throw new SettingsStorageException("SettingsFormatUnsupported");
    }
    private static void Validate(Settings s)
    {
        s.Sources ??= new();
        s.RefreshMinutes = Math.Clamp(s.RefreshMinutes, 5, 1440);
        if (s.FirstDay is < 0 or > 6) s.FirstDay = null;
        foreach (var source in s.Sources)
            if (source == null || source.Url == null || source.ProtectedUrl == null || !Guid.TryParseExact(source.Id, "N", out _)) throw new InvalidDataException();
        if (s.Sources.Select(x => x.Id).Distinct().Count() != s.Sources.Count) throw new InvalidDataException();
    }
    private static string ProtectAddress(string address)
    {
        var plain = Encoding.UTF8.GetBytes(address);
        try { return Convert.ToBase64String(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    // 加密仅用于落盘，不影响草稿比较；可注入失败的加密函数用于隔离检查。
    public static void Save(Settings settings, Func<string, string>? protect = null)
    {
        string path = PathFor("settings.json");
        if (File.Exists(path)) Format(ReadDocument(path)); // 未知或损坏配置禁止后台覆盖。
        Validate(settings);
        var document = JsonSerializer.SerializeToNode(settings, Json)!.AsObject();
        document["Format"] = 1;
        var sources = document["Sources"]!.AsArray();
        for (int i = 0; i < settings.Sources.Count; i++)
        {
            var source = settings.Sources[i];
            var saved = sources[i]!.AsObject();
            saved.Remove("Url"); saved.Remove("UrlUnreadable");
            saved["ProtectedUrl"] = source.UrlUnreadable ? source.ProtectedUrl : (protect ?? ProtectAddress)(source.Url);
        }
        // 临时文件中也只有密文；替换失败仍保留原配置。
        try { Write(path, document.ToJsonString(Json)); }
        finally { try { File.Delete(path + ".tmp"); } catch { } }
    }
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
    private static readonly HttpClient Client = new(PublicNetwork.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
    public static Uri Validate(string value)
    {
        value = value.Trim();
        if (value.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value[9..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("InvalidUrl");
        PublicNetwork.ValidateHost(uri.DnsSafeHost);
        return uri;
    }
    // 主机及协议由 Uri 标准化；路径、令牌等查询参数保持区分大小写。
    public static bool SameUrl(string left, string right)
    {
        try { return string.Equals(Validate(left).AbsoluteUri, Validate(right).AbsoluteUri, StringComparison.Ordinal); }
        catch (Exception e) when (e is ArgumentException or SubscriptionBlockedException) { return false; }
    }
    public static async Task<string> Text(Uri uri, CancellationToken ct = default, HttpClient? transport = null)
    {
        const int limit = 5 * 1024 * 1024;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        for (int hop = 0; hop < 5; hop++)
        {
            uri = Validate(uri.AbsoluteUri);
            using var response = await (transport ?? Client).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
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

// 仅计算农历日期，不补充传统节日或休班安排。
public static class ChineseLunar
{
    private static readonly ChineseLunisolarCalendar Calendar = new();
    public static string Label(DateTime date, CultureInfo? language = null)
    {
        date = date.Date;
        if (date < Calendar.MinSupportedDateTime || date > Calendar.MaxSupportedDateTime) return "";
        var culture = language ?? L.Ui;
        string Text(string key) => L.Resources.GetString(key, culture) ?? key;
        int year = Calendar.GetYear(date), month = Calendar.GetMonth(date), day = Calendar.GetDayOfMonth(date);
        int leap = Calendar.GetLeapMonth(year);
        bool isLeap = leap > 0 && month == leap;
        // .NET 把闰月算作独立月份，转换为实际农历月序。
        if (leap > 0 && month >= leap) month--;
        if (day == 1) return (isLeap ? Text("LunarLeap") : "") + Text("LunarMonth" + month);
        return Text("LunarDay" + day);
    }
}
