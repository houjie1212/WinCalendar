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

// 订阅类型决定是否参与日期格的节假日标识。
public enum SubscriptionKind { Ordinary, ChinaHolidays }
public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Color { get; set; } = "#2563EB";
    public SubscriptionKind Kind { get; set; }
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
