using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WinCalendar;

// 摘要用于一致性检查，不是防御已控制当前用户的攻击者的数字签名。
public sealed record UpdateFile(long Size, string Sha256);
public static class UpdateSecurity
{
    public static void RequireNormalUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UpdateException("UpdateElevated");
    }
    public static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static Dictionary<string, UpdateFile> Snapshot(string directory)
        => UpdateService.ReadManifest(directory).ToDictionary(n => n, n => Hash(UpdateService.SafePath(directory, n)), StringComparer.OrdinalIgnoreCase);
    private static UpdateFile Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > UpdateService.ExpandedLimit) throw new InvalidDataException();
        return new(stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
    public static void Verify(string directory, Dictionary<string, UpdateFile> files)
    {
        ValidateFiles(directory, files);
        foreach (var entry in files)
            if (Hash(UpdateService.SafePath(directory, entry.Key)) != entry.Value) throw new InvalidDataException();
    }
    private static void ValidateFiles(string directory, Dictionary<string, UpdateFile> files)
    {
        if (files == null || files.Count is < 5 or > 10001 || files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count ||
            !new[] { "WinCalendar.exe", "WinCalendar.dll", "WinCalendar.deps.json", "WinCalendar.runtimeconfig.json", UpdateService.ManifestName }.All(files.ContainsKey)) throw new InvalidDataException();
        long total = 0;
        foreach (var entry in files)
        {
            UpdateService.SafePath(directory, entry.Key);
            if (entry.Value == null || entry.Value.Size < 0 || entry.Value.Size > UpdateService.ExpandedLimit ||
                (total += entry.Value.Size) > UpdateService.ExpandedLimit || entry.Value.Sha256 == null || entry.Value.Sha256.Length != 64 ||
                !entry.Value.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException();
        }
    }
    public static void Validate(UpdateJournal j, string target)
    {
        if (j.Format != 1 || !Path.IsPathFullyQualified(j.Target) || !SamePath(j.Target, target) ||
            j.Phase is not ("prepared" or "applying" or "starting" or "complete" or "rolledback")) throw new InvalidDataException();
        if (j.RunnerName == null || (j.RecoveryRunner
            ? !j.RunnerName.StartsWith("recovery-", StringComparison.Ordinal) || !Guid.TryParseExact(j.RunnerName[9..], "N", out _)
            : j.RunnerName != "worker")) throw new InvalidDataException();
        ValidateFiles(target, j.RunnerFiles);
        ValidateFiles(target, j.OldFiles); ValidateFiles(target, j.NewFiles);
        if (j.Files == null || j.Existing == null || j.Files.Length > 20002 || j.Existing.Length > 10001 ||
            j.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != j.Files.Length || j.Existing.Distinct(StringComparer.OrdinalIgnoreCase).Count() != j.Existing.Length ||
            !j.Files.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(j.OldFiles.Keys.Union(j.NewFiles.Keys, StringComparer.OrdinalIgnoreCase)) ||
            !j.Existing.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(j.OldFiles.Keys)) throw new InvalidDataException();
    }
    public static void VerifyManifest(string directory, Dictionary<string, UpdateFile> files)
    {
        Verify(directory, files);
        if (!UpdateService.ReadManifest(directory).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files.Keys)) throw new InvalidDataException();
    }
    // 进程标识错误时拒绝操作；进程确实退出则返回 null，不杀死复用 PID 的进程。
    public static Process? Find(int id, long started, string expected)
    {
        if (id == 0 && started == 0) return null;
        if (id <= 0 || started <= 0) throw new InvalidDataException();
        Process p;
        try { p = Process.GetProcessById(id); } catch (ArgumentException) { return null; }
        try
        {
            if (p.HasExited) { p.Dispose(); return null; }
            if (p.StartTime.ToUniversalTime().Ticks != started || p.MainModule?.FileName is not string actual || !SamePath(actual, expected)) throw new InvalidDataException();
            return p;
        }
        catch
        {
            bool exited = p.HasExited; p.Dispose();
            if (exited) return null;
            throw;
        }
    }
}
