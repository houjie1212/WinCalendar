using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinCalendar;

// 公开发布信息只用于展示和下载，不执行更新说明中的内容。
public sealed record UpdateRelease(Version Version, string Notes, Uri Package, Uri Checksum)
{
    public string FileName => $"WinCalendar-{Version}-win-x64.zip";
}

public sealed class UpdateException : Exception
{
    public string Key { get; }
    public UpdateException(string key) : base(key) { Key = key; }
}

// 固定更新源、大小限制和哈希校验独立于用户的 ICS 下载设置。
public static class UpdateService
{
    public const string Repository = "houjie1212/WinCalendar";
    public const string ManifestName = "release-files.json";
    public const long PackageLimit = 256L * 1024 * 1024;
    public const long ExpandedLimit = 1024L * 1024 * 1024;
    public static Version Current => ParseVersion(typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);
    public static string Root => Store.PathFor("Updates");

    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") ||
            !Version.TryParse(value.TrimStart('v'), out var version)) throw new UpdateException("UpdateInvalidVersion");
        return version;
    }

    // 初始下载地址只能来自本仓库；重定向仅允许 GitHub 的资产主机。
    public static bool AllowedUri(Uri uri, bool redirect = false) => uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        ((uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/" + Repository + "/releases/download/", StringComparison.Ordinal)) ||
         (redirect && (uri.Host == "release-assets.githubusercontent.com" || uri.Host == "objects.githubusercontent.com")));

    public static UpdateRelease? ReadRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var value = doc.RootElement;
        if (value.GetProperty("draft").GetBoolean() || value.GetProperty("prerelease").GetBoolean()) return null;
        var version = ParseVersion(value.GetProperty("tag_name").GetString() ?? "");
        if (version <= Current) return new(version, "", new Uri("https://github.com"), new Uri("https://github.com"));
        string name = $"WinCalendar-{version}-win-x64.zip";
        Uri Asset(string expected)
        {
            var assets = value.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == expected).ToArray();
            if (assets.Length != 1 || !Uri.TryCreate(assets[0].GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri) || !AllowedUri(uri))
                throw new UpdateException("UpdateMissingPackage");
            return uri;
        }
        return new(version, value.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "", Asset(name), Asset("SHA256SUMS.txt"));
    }

    public static async Task<UpdateRelease?> Check(CancellationToken ct, HttpMessageHandler? transport = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var handler = transport ?? new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("WinCalendar/" + Current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new UpdateException("UpdateRateLimit");
        response.EnsureSuccessStatusCode();
        using var data = new MemoryStream();
        await CopyLimited(await response.Content.ReadAsStreamAsync(timeout.Token), data, 2 * 1024 * 1024, null, null, timeout.Token);
        return ReadRelease(Encoding.UTF8.GetString(data.ToArray()));
    }

    private static async Task CopyLimited(Stream input, Stream output, long limit, long? total, IProgress<double>? progress, CancellationToken ct)
    {
        using (input)
        {
            var buffer = new byte[81920]; long count = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                count += read;
                if (count > limit) throw new InvalidDataException();
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                if (total > 0) progress?.Report(Math.Min(100, count * 100d / total.Value));
            }
        }
    }

    public static async Task DownloadAsset(Uri uri, string path, long limit, IProgress<double>? progress, CancellationToken ct, HttpMessageHandler? transport = null)
    {
        if (!AllowedUri(uri)) throw new InvalidDataException();
        using var handler = transport ?? new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                uri = new Uri(uri, response.Headers.Location ?? throw new InvalidDataException());
                if (!AllowedUri(uri, true)) throw new InvalidDataException();
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyLimited(await response.Content.ReadAsStreamAsync(ct), output, limit, response.Content.Headers.ContentLength, progress, ct);
            return;
        }
        throw new InvalidDataException();
    }

    public static void VerifyHash(string package, string sums, string name)
    {
        var matches = File.ReadAllLines(sums).Select(l => Regex.Match(l, @"\A([a-fA-F0-9]{64})\s+\*?(.+)\z"))
            .Where(m => m.Success && m.Groups[2].Value == name).ToArray();
        using var file = File.OpenRead(package);
        if (matches.Length != 1 || !Convert.ToHexString(SHA256.HashData(file)).Equals(matches[0].Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("UpdateHashFailed");
    }

    // Windows 路径比较不区分大小写；禁止 ADS、设备名、路径跳转和重解析点。
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative)) throw new InvalidDataException();
        foreach (var part in relative.Split('/'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException();
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        for (string? p = full; p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        return full;
    }

    public static string[] ReadManifest(string directory)
    {
        var names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(directory, ManifestName))) ?? throw new InvalidDataException();
        if (names.Length is < 4 or > 10000 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            !new[] { "WinCalendar.exe", "WinCalendar.dll", "WinCalendar.deps.json", "WinCalendar.runtimeconfig.json" }.All(names.Contains)) throw new InvalidDataException();
        foreach (var name in names) SafePath(directory, name);
        if (names.Contains(ManifestName, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException();
        return names.Append(ManifestName).ToArray();
    }

    public static void Extract(string zipPath, string destination, Version version, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 10000) throw new InvalidDataException();
        long expanded = 0; var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            int mode = (entry.ExternalAttributes >> 16) & 0xf000;
            if (mode == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            if (entry.FullName.EndsWith('/')) { SafePath(destination, entry.FullName.TrimEnd('/')); continue; }
            var path = SafePath(destination, entry.FullName);
            if (!files.Add(entry.FullName) || (expanded += entry.Length) > ExpandedLimit) throw new InvalidDataException();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        var manifest = ReadManifest(destination);
        if (!files.SetEquals(manifest)) throw new InvalidDataException();
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(destination, "WinCalendar.dll")).Version!;
        if (new Version(assembly.Major, assembly.Minor, assembly.Build) != version) throw new UpdateException("UpdateInvalidVersion");
    }

    public static async Task<string> Prepare(UpdateRelease release, IProgress<double> progress, CancellationToken ct)
    {
        var job = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            string zip = Path.Combine(job, release.FileName), sums = Path.Combine(job, "SHA256SUMS.txt");
            await DownloadAsset(release.Checksum, sums, 65536, null, timeout.Token);
            await DownloadAsset(release.Package, zip, PackageLimit, progress, timeout.Token);
            await Task.Run(() => { VerifyHash(zip, sums, release.FileName); Extract(zip, Path.Combine(job, "payload"), release.Version, timeout.Token); }, timeout.Token);
            return job;
        }
        catch { Directory.Delete(job, true); throw; }
    }
}
