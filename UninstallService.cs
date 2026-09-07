using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinCalendar;

// 仅允许启动与当前运行目录匹配的本用户安装记录。
public static class UninstallService
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{0D94FB44-DB53-44F3-B04E-F005DDBDA764}_is1";
    public static string? Resolve(string current, string? installed, string? command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(command)) return null;
            string path = command.Trim();
            if (path.StartsWith('"') && path.EndsWith('"')) path = path[1..^1];
            if (!Path.IsPathFullyQualified(path) || !Regex.IsMatch(Path.GetFileName(path), @"\Aunins[0-9]{3}\.exe\z", RegexOptions.IgnoreCase)) return null;
            string Normalize(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (!Normalize(current).Equals(Normalize(installed), StringComparison.OrdinalIgnoreCase) ||
                !Normalize(current).Equals(Normalize(Path.GetDirectoryName(path)!), StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    public static string? Find()
    {
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = root.OpenSubKey(Key);
                var path = Resolve(AppContext.BaseDirectory, key?.GetValue("InstallLocation") as string, key?.GetValue("UninstallString") as string);
                if (path != null) return path;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return null;
    }

    // 启动脚本不加载应用程序集，等待本进程退出后才启动卸载，避免文件占用。
    public static async Task Launch()
    {
        string uninstaller = Find() ?? throw new InvalidOperationException();
        string folder = Path.Combine(Path.GetTempPath(), "WinCalendar-Uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string script = Path.Combine(folder, "launch.ps1"), ready = Path.Combine(folder, "ready");
        await File.WriteAllTextAsync(script, """
param([int]$ParentId, [long]$Started, [string]$Uninstaller, [string]$Ready, [string]$ErrorText)
$ErrorActionPreference = 'Stop'
try {
    $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText($Ready, 'ready')
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $Started) {
        if (-not $parent.WaitForExit(30000)) { exit 1 }
    }
    Start-Process -FilePath $Uninstaller
} catch {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($ErrorText, 'WinCalendar') | Out-Null
} finally {
    Remove-Item -LiteralPath $Ready -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PSScriptRoot -ErrorAction SilentlyContinue
}
""", new UTF8Encoding(true));
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        using var parent = Process.GetCurrentProcess();
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
            parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), uninstaller, ready, L.T("UninstallFailed") }) info.ArgumentList.Add(arg);
        using var helper = Process.Start(info) ?? throw new IOException();
        for (int i = 0; i < 100; i++)
        {
            if (File.Exists(ready)) return;
            if (helper.HasExited) break;
            await Task.Delay(100);
        }
        throw new IOException();
    }
}
