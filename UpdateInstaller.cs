using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinCalendar;

// 恢复记录先于文件变更落盘，备份在新版确认启动前一直保留。
public sealed class UpdateJournal
{
    public string Target { get; set; } = "";
    public string Phase { get; set; } = "prepared";
    public string[] Files { get; set; } = Array.Empty<string>();
    public string[] Existing { get; set; } = Array.Empty<string>();
    public int ParentId { get; set; }
    public long ParentStarted { get; set; }
    public int WorkerId { get; set; }
    public long WorkerStarted { get; set; }
    public int ChildId { get; set; }
    public long ChildStarted { get; set; }
}

// 使用当前发行版的完整副本执行替换，不依赖正在被覆盖的程序集。
public static class UpdateInstaller
{
    private const string MutexName = "Local\\WinCalendar.Update";
    public static string JournalPath(string job) => Path.Combine(job, "journal.json");
    private static string Signal(string job, string kind) => "Local\\WinCalendar.Update." + Path.GetFileName(job) + "." + kind;
    public static void WriteJournal(string job, UpdateJournal journal)
    {
        var path = JournalPath(job);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, journal); file.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }

    private static string ValidateJob(string job)
    {
        job = Path.GetFullPath(job);
        if (!Guid.TryParseExact(Path.GetFileName(job), "N", out _) ||
            !string.Equals(Path.GetDirectoryName(job), Path.GetFullPath(UpdateService.Root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        UpdateService.SafePath(UpdateService.Root, Path.GetFileName(job));
        return job;
    }

    private static UpdateJournal ReadJournal(string job)
    {
        var j = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(JournalPath(job))) ?? throw new InvalidDataException();
        var target = Path.GetFullPath(j.Target).TrimEnd(Path.DirectorySeparatorChar);
        if (target.Length < 4 || target.StartsWith(Path.GetFullPath(UpdateService.Root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        if (j.Files.Length > 10001 || j.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != j.Files.Length || j.Existing.Except(j.Files, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException();
        foreach (var name in j.Files) UpdateService.SafePath(target, name);
        return j;
    }

    // 仅发行文件参与备份和替换；用户额外文件遇到同名新文件时拒绝覆盖。
    public static UpdateJournal PrepareFiles(string job, string target)
    {
        target = Path.GetFullPath(target);
        var oldFiles = UpdateService.ReadManifest(target);
        var newFiles = UpdateService.ReadManifest(Path.Combine(job, "payload"));
        foreach (var name in newFiles.Except(oldFiles, StringComparer.OrdinalIgnoreCase))
            if (File.Exists(UpdateService.SafePath(target, name))) throw new UpdateException("UpdateFileConflict");
        var names = oldFiles.Union(newFiles, StringComparer.OrdinalIgnoreCase).ToArray();
        long oldSize = oldFiles.Sum(n => new FileInfo(UpdateService.SafePath(target, n)).Length);
        long newSize = newFiles.Sum(n => new FileInfo(UpdateService.SafePath(Path.Combine(job, "payload"), n)).Length);
        if (new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace < oldSize + newSize + 16 * 1024 * 1024 ||
            new DriveInfo(Path.GetPathRoot(job)!).AvailableFreeSpace < oldSize * 2 + 16 * 1024 * 1024) throw new IOException();
        string probe = Path.Combine(target, ".wincalendar-write-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var j = new UpdateJournal { Target = target, Files = names, Existing = names.Where(n => File.Exists(UpdateService.SafePath(target, n))).ToArray() };
        foreach (var name in oldFiles)
        {
            string dest = UpdateService.SafePath(Path.Combine(job, "worker"), name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(UpdateService.SafePath(target, name), dest);
        }
        WriteJournal(job, j);
        return j;
    }

    private static Process Start(string exe, params string[] args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException();
    }

    public static async Task Launch(string job)
    {
        ValidateJob(job);
        var j = await Task.Run(() => PrepareFiles(job, AppContext.BaseDirectory));
        using var parent = Process.GetCurrentProcess();
        j.ParentId = parent.Id; j.ParentStarted = parent.StartTime.ToUniversalTime().Ticks;
        WriteJournal(job, j);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Signal(job, "ready"));
        using var worker = Start(Path.Combine(job, "worker", "WinCalendar.exe"), "--apply-update", job);
        if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(20)))) throw new UpdateException("UpdateFailed");
    }

    // 进程 ID 会重用，同时校验启动时间，避免等待或终止无关进程。
    private static Process? Find(int id, long started)
    {
        if (id <= 0 || started <= 0) return null;
        try { var p = Process.GetProcessById(id); if (p.StartTime.ToUniversalTime().Ticks == started) return p; p.Dispose(); }
        catch (ArgumentException) { }
        return null;
    }

    public static void InstallFiles(string job, UpdateJournal j)
    {
        string backup = Path.Combine(job, "backup");
        foreach (var name in j.Existing)
        {
            var path = UpdateService.SafePath(backup, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(UpdateService.SafePath(j.Target, name), path, true);
        }
        j.Phase = "applying"; WriteJournal(job, j);
        var fresh = UpdateService.ReadManifest(Path.Combine(job, "payload"));
        foreach (var name in fresh)
        {
            var target = UpdateService.SafePath(j.Target, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(UpdateService.SafePath(Path.Combine(job, "payload"), name), target, true);
        }
        foreach (var name in j.Existing.Except(fresh, StringComparer.OrdinalIgnoreCase)) File.Delete(UpdateService.SafePath(j.Target, name));
        j.Phase = "starting"; WriteJournal(job, j);
    }

    public static void Rollback(string job, UpdateJournal j)
    {
        foreach (var name in j.Existing)
        {
            var path = UpdateService.SafePath(j.Target, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(UpdateService.SafePath(Path.Combine(job, "backup"), name), path, true);
        }
        foreach (var name in j.Files.Except(j.Existing, StringComparer.OrdinalIgnoreCase)) File.Delete(UpdateService.SafePath(j.Target, name));
        j.Phase = "rolledback"; WriteJournal(job, j);
    }

    public static int Worker(string job, bool recover)
    {
        UpdateJournal? j = null;
        using var mutex = new Mutex(false, MutexName);
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return 1;
            job = ValidateJob(job); j = ReadJournal(job);
            using var self = Process.GetCurrentProcess(); j.WorkerId = self.Id; j.WorkerStarted = self.StartTime.ToUniversalTime().Ticks; WriteJournal(job, j);
            if (!recover)
            {
                using var ready = EventWaitHandle.OpenExisting(Signal(job, "ready")); ready.Set();
                using var parent = Find(j.ParentId, j.ParentStarted);
                if (parent != null && !parent.WaitForExit(30000)) return 1;
                InstallFiles(job, j);
                using var ack = new EventWaitHandle(false, EventResetMode.ManualReset, Signal(job, "ack"));
                using var child = Start(Path.Combine(j.Target, "WinCalendar.exe"), "--updated", job);
                j.ChildId = child.Id; j.ChildStarted = child.StartTime.ToUniversalTime().Ticks; WriteJournal(job, j);
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!ack.WaitOne(100))
                    if (child.HasExited || DateTime.UtcNow >= deadline) throw new IOException();
                j.Phase = "complete"; WriteJournal(job, j);
                return 0;
            }
            // 恢复可重复执行，保留备份直到明确完成。
            using (var parent = Find(j.ParentId, j.ParentStarted))
                if (parent != null && !parent.WaitForExit(30000)) return 1;
            if (j.Phase is "applying" or "starting") RestoreAndStart(job, j);
            return 0;
        }
        catch
        {
            try
            {
                if (j?.Phase is "applying" or "starting") RestoreAndStart(job, j);
                else if (j?.Phase == "prepared") Start(Path.Combine(j.Target, "WinCalendar.exe"), "--update-failed").Dispose();
            }
            catch { }
            return 1;
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private static void RestoreAndStart(string job, UpdateJournal j)
    {
        using var child = Find(j.ChildId, j.ChildStarted);
        if (child != null && !child.HasExited) { child.Kill(); if (!child.WaitForExit(10000)) throw new IOException(); }
        Rollback(job, j);
        Start(Path.Combine(j.Target, "WinCalendar.exe"), "--update-failed").Dispose();
    }

    public static void Acknowledge(string job)
    {
        ValidateJob(job);
        var j = ReadJournal(job);
        if (!string.Equals(Path.GetFullPath(j.Target).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || j.Phase != "starting") throw new InvalidDataException();
        using var ack = EventWaitHandle.OpenExisting(Signal(job, "ack")); ack.Set();
    }

    // 若上次更新中断且主程序仍可启动，交给临时工作进程恢复后再运行。
    public static bool ResumeIfNeeded()
    {
        if (!Directory.Exists(UpdateService.Root)) return false;
        foreach (var job in Directory.GetDirectories(UpdateService.Root))
        {
            if (!File.Exists(JournalPath(job))) continue;
            UpdateJournal j;
            try { j = ReadJournal(ValidateJob(job)); }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { continue; }
            if (!string.Equals(j.Target.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || j.Phase is not ("applying" or "starting")) continue;
            using var worker = Find(j.WorkerId, j.WorkerStarted);
            if (worker == null)
            {
                using var parent = Process.GetCurrentProcess(); j.ParentId = parent.Id; j.ParentStarted = parent.StartTime.ToUniversalTime().Ticks; WriteJournal(job, j);
                Start(Path.Combine(job, "worker", "WinCalendar.exe"), "--recover-update", job).Dispose();
            }
            return true;
        }
        return false;
    }
}
