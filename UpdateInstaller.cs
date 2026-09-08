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
    public int Format { get; set; }
    public System.Collections.Generic.Dictionary<string, UpdateFile> OldFiles { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, UpdateFile> NewFiles { get; set; } = new();
    public System.Collections.Generic.Dictionary<string, UpdateFile> RunnerFiles { get; set; } = new();
    public string RunnerName { get; set; } = "worker";
    public bool RecoveryRunner { get; set; }
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
        var path = UpdateService.SafePath(job, "journal.json");
        UpdateService.SafePath(job, "journal.json.tmp");
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

    public static UpdateJournal ReadJournal(string job, string? target = null)
    {
        var j = ReadRecord(job);
        if (target != null) UpdateSecurity.Validate(j, target);
        return j;
    }
    // 旧格式仅供确认及终态识别，更新工作进程和恢复入口仍要求新格式。
    private static UpdateJournal ReadRecord(string job, bool allowLegacy = false)
    {
        using var file = new FileStream(UpdateService.SafePath(job, "journal.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        using var document = JsonDocument.Parse(file);
        bool legacy = !document.RootElement.TryGetProperty("Format", out var format);
        if (legacy ? !allowLegacy : !format.TryGetInt32(out int number) || number != 1) throw new InvalidDataException();
        var j = document.RootElement.Deserialize<UpdateJournal>() ?? throw new InvalidDataException();
        if (legacy) { j.Format = 0; j.RunnerName = "worker"; j.RecoveryRunner = false; }
        return j;
    }
    public static void ValidateConfirmation(UpdateJournal j, string target)
    {
        if (j.Format == 1) { UpdateSecurity.Validate(j, target); return; }
        if (j.Format != 0 || !Path.IsPathFullyQualified(j.Target) || !UpdateSecurity.SamePath(j.Target, target) ||
            j.Phase is not ("prepared" or "applying" or "starting" or "complete" or "rolledback") ||
            j.Files == null || j.Existing == null || j.Files.Length > 10001 || j.Existing.Length > 10001 ||
            j.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != j.Files.Length ||
            j.Existing.Except(j.Files, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException();
        foreach (var name in j.Files) UpdateService.SafePath(target, name);
    }

    // 仅发行文件参与备份和替换；用户额外文件遇到同名新文件时拒绝覆盖。
    public static UpdateJournal PrepareFiles(string job, string target)
    {
        UpdateSecurity.RequireNormalUser();
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
        var j = new UpdateJournal { Format = 1, OldFiles = UpdateSecurity.Snapshot(target), NewFiles = UpdateSecurity.Snapshot(Path.Combine(job, "payload")), Target = target, Files = names, Existing = names.Where(n => File.Exists(UpdateService.SafePath(target, n))).ToArray() };
        foreach (var name in oldFiles)
        {
            string dest = UpdateService.SafePath(Path.Combine(job, "worker"), name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(UpdateService.SafePath(target, name), dest);
        }
        j.RunnerFiles = j.OldFiles;
        UpdateSecurity.VerifyManifest(Path.Combine(job, "worker"), j.RunnerFiles);
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
        UpdateSecurity.RequireNormalUser();
        ValidateJob(job);
        var j = await Task.Run(() => PrepareFiles(job, AppContext.BaseDirectory));
        using var parent = Process.GetCurrentProcess();
        j.ParentId = parent.Id; j.ParentStarted = parent.StartTime.ToUniversalTime().Ticks;
        WriteJournal(job, j);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Signal(job, "ready"));
        using var worker = Start(Path.Combine(job, "worker", "WinCalendar.exe"), "--apply-update", job);
        if (!await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(20)))) throw new UpdateException("UpdateFailed");
    }

    public static void InstallFiles(string job, UpdateJournal j)
    {
        UpdateSecurity.RequireNormalUser();
        UpdateSecurity.Validate(j, j.Target);
        UpdateSecurity.VerifyManifest(j.Target, j.OldFiles);
        UpdateSecurity.VerifyManifest(Path.Combine(job, "payload"), j.NewFiles);
        string backup = Path.Combine(job, "backup");
        foreach (var name in j.Existing)
        {
            var path = UpdateService.SafePath(backup, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(UpdateService.SafePath(j.Target, name), path, true);
        }
        UpdateSecurity.VerifyManifest(backup, j.OldFiles);
        j.Phase = "applying"; WriteJournal(job, j);
        var fresh = UpdateService.ReadManifest(Path.Combine(job, "payload"));
        foreach (var name in fresh)
        {
            var target = UpdateService.SafePath(j.Target, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(UpdateService.SafePath(Path.Combine(job, "payload"), name), target, true);
        }
        foreach (var name in j.Existing.Except(fresh, StringComparer.OrdinalIgnoreCase)) File.Delete(UpdateService.SafePath(j.Target, name));
        UpdateSecurity.VerifyManifest(j.Target, j.NewFiles);
        j.Phase = "starting"; WriteJournal(job, j);
    }

    public static void Rollback(string job, UpdateJournal j)
    {
        UpdateSecurity.RequireNormalUser();
        UpdateSecurity.Validate(j, j.Target);
        UpdateSecurity.VerifyManifest(Path.Combine(job, "backup"), j.OldFiles);
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
        bool mayRecover = false;
        using var mutex = new Mutex(false, MutexName);
        bool held = false;
        try
        {
            UpdateSecurity.RequireNormalUser();
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) return 1;
            job = ValidateJob(job);
            var candidate = ReadJournal(job);
            using var origin = UpdateSecurity.Find(candidate.ParentId, candidate.ParentStarted, Path.Combine(candidate.Target, "WinCalendar.exe")) ?? throw new InvalidDataException();
            string actualTarget = Path.GetDirectoryName(origin.MainModule!.FileName)!;
            UpdateSecurity.Validate(candidate, actualTarget);
            if (recover != candidate.RecoveryRunner || (!recover && candidate.Phase != "prepared") ||
                (recover && candidate.Phase is not ("applying" or "starting"))) throw new InvalidDataException();
            string runner = Path.Combine(job, candidate.RunnerName);
            if (!UpdateSecurity.SamePath(AppContext.BaseDirectory, runner)) throw new InvalidDataException();
            UpdateSecurity.VerifyManifest(runner, candidate.RunnerFiles);
            if (recover) UpdateSecurity.VerifyManifest(Path.Combine(job, "backup"), candidate.OldFiles);
            else { UpdateSecurity.VerifyManifest(actualTarget, candidate.OldFiles); UpdateSecurity.VerifyManifest(Path.Combine(job, "payload"), candidate.NewFiles); }
            j = candidate;
            using var self = Process.GetCurrentProcess(); j.WorkerId = self.Id; j.WorkerStarted = self.StartTime.ToUniversalTime().Ticks; WriteJournal(job, j);
            if (!recover)
            {
                using var ready = EventWaitHandle.OpenExisting(Signal(job, "ready")); ready.Set();
                if (!origin.WaitForExit(30000)) return 1;
                mayRecover = true;
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
            using (var ready = EventWaitHandle.OpenExisting(Signal(job, "ready"))) ready.Set();
            // 恢复可重复执行，保留备份直到明确完成。
            if (!origin.WaitForExit(30000)) return 1;
            mayRecover = true;
            if (j.Phase is "applying" or "starting") RestoreAndStart(job, j);
            return 0;
        }
        catch (Exception error)
        {
            if (error is InvalidDataException or UpdateException) return 1;
            try
            {
                if (mayRecover && j?.Phase is "applying" or "starting") RestoreAndStart(job, j);
                else if (mayRecover && j?.Phase == "prepared") { UpdateSecurity.VerifyManifest(j.Target, j.OldFiles); Start(Path.Combine(j.Target, "WinCalendar.exe"), "--update-failed").Dispose(); }
            }
            catch { }
            return 1;
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private static void RestoreAndStart(string job, UpdateJournal j)
    {
        UpdateSecurity.Validate(j, j.Target);
        UpdateSecurity.VerifyManifest(Path.Combine(job, "backup"), j.OldFiles);
        using var child = UpdateSecurity.Find(j.ChildId, j.ChildStarted, Path.Combine(j.Target, "WinCalendar.exe"));
        if (child != null && !child.HasExited) { child.Kill(); if (!child.WaitForExit(10000)) throw new IOException(); }
        Rollback(job, j);
        Start(Path.Combine(j.Target, "WinCalendar.exe"), "--update-failed").Dispose();
    }

    public static UpdateJournal Acknowledge(string job)
    {
        ValidateJob(job);
        UpdateSecurity.RequireNormalUser();
        using var self = Process.GetCurrentProcess();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var j = ReadRecord(job, true);
            ValidateConfirmation(j, AppContext.BaseDirectory);
            if (j.Phase != "starting" || j.RecoveryRunner) throw new InvalidDataException();
            using var worker = UpdateSecurity.Find(j.WorkerId, j.WorkerStarted, Path.Combine(job, "worker", "WinCalendar.exe")) ?? throw new InvalidDataException();
            // 旧工作进程在启动子进程后才写入 PID；仅允许短暂等待尚未填入的字段。
            if (j.ChildId == 0 && j.ChildStarted == 0 && DateTime.UtcNow < deadline) { Thread.Sleep(50); continue; }
            if (j.ChildId != self.Id || j.ChildStarted != self.StartTime.ToUniversalTime().Ticks) throw new InvalidDataException();
            using var ack = EventWaitHandle.OpenExisting(Signal(job, "ack")); ack.Set();
            return j;
        }
    }
    // 只有工作进程提交 complete 后才允许迁移配置，避免旧版回滚后读到新格式。
    public static async Task ConfirmStartup(string job)
    {
        try
        {
            var accepted = await Task.Run(() => Acknowledge(job));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                var j = ReadRecord(job, true);
                ValidateConfirmation(j, AppContext.BaseDirectory);
                if (j.Format != accepted.Format || j.WorkerId != accepted.WorkerId || j.WorkerStarted != accepted.WorkerStarted ||
                    j.ChildId != accepted.ChildId || j.ChildStarted != accepted.ChildStarted || j.RecoveryRunner) throw new InvalidDataException();
                if (j.Phase == "complete") return;
                if (j.Phase != "starting" || DateTime.UtcNow >= deadline) throw new InvalidDataException();
                await Task.Delay(50);
            }
        }
        catch (UpdateException) { throw; }
        catch { throw new UpdateException("UpdateConfirmationFailed"); }
    }

    // 若上次更新中断且主程序仍可启动，交给临时工作进程恢复后再运行。
    public static bool ResumeIfNeeded()
    {
        if (!Directory.Exists(UpdateService.Root)) return false;
        try { UpdateSecurity.RequireNormalUser(); }
        catch (UpdateException e) { System.Windows.MessageBox.Show(L.T(e.Key), "WinCalendar"); return false; }
        bool invalid = false;
        foreach (var job in Directory.GetDirectories(UpdateService.Root))
        {
            if (!File.Exists(JournalPath(job))) continue;
            try
            {
                var j = ReadRecord(ValidateJob(job), true);
                if (!UpdateSecurity.SamePath(j.Target, AppContext.BaseDirectory)) continue;
                ValidateConfirmation(j, AppContext.BaseDirectory);
                if (j.Format == 0)
                {
                    if (j.Phase is "complete" or "rolledback") continue;
                    throw new InvalidDataException();
                }
                if (j.Phase is not ("applying" or "starting")) continue;
                using var worker = UpdateSecurity.Find(j.WorkerId, j.WorkerStarted, Path.Combine(job, j.RunnerName, "WinCalendar.exe"));
                if (worker != null) return true;
                UpdateSecurity.VerifyManifest(Path.Combine(job, "backup"), j.OldFiles);
                // 只复制当前正在运行的发行文件，绝不启动上次留下的工作程序。
                j.RunnerName = "recovery-" + Guid.NewGuid().ToString("N");
                var runner = Path.Combine(job, j.RunnerName);
                j.RunnerFiles = UpdateSecurity.Snapshot(AppContext.BaseDirectory);
                foreach (var name in j.RunnerFiles.Keys)
                {
                    var destination = UpdateService.SafePath(runner, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(UpdateService.SafePath(AppContext.BaseDirectory, name), destination);
                }
                UpdateSecurity.VerifyManifest(runner, j.RunnerFiles);
                using var parent = Process.GetCurrentProcess();
                j.ParentId = parent.Id; j.ParentStarted = parent.StartTime.ToUniversalTime().Ticks; j.RecoveryRunner = true;
                WriteJournal(job, j);
                using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, Signal(job, "ready"));
                using var launched = Start(Path.Combine(runner, "WinCalendar.exe"), "--recover-update", job);
                if (!ready.WaitOne(TimeSpan.FromSeconds(20))) throw new InvalidDataException();
                return true;
            }
            catch { invalid = true; }
        }
        if (invalid) System.Windows.MessageBox.Show(L.T("UpdateUnsafeRecovery"), "WinCalendar");
        return false;
    }
}
