using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace WinCalendar;

// UI Automation 在独立线程更新物理坐标；鼠标钩子只读取快照，避免阻塞系统输入。
public sealed class ClockHook : IDisposable
{
    public sealed record Clock(nint Taskbar, Rect Bounds);
    private Clock[] clocks = Array.Empty<Clock>();
    private readonly Dispatcher dispatcher;
    private readonly HookProc callback;
    private readonly Thread scanner;
    private volatile bool stopped;
    private bool swallowed;
    private Point clickPoint;
    private nint hook;
    public event Action<Point>? Toggle;
    public event Action<Point>? OutsideClick;
    public bool Available => Volatile.Read(ref clocks).Length > 0;
    public ClockHook(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        callback = OnMouse;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new System.ComponentModel.Win32Exception();
        scanner = new Thread(ScanLoop) { IsBackground = true, Name = "Clock locator" };
        scanner.SetApartmentState(ApartmentState.MTA);
        scanner.Start();
    }
    private void ScanLoop()
    {
        while (!stopped)
        {
            try { Volatile.Write(ref clocks, FindClocks()); } catch { Volatile.Write(ref clocks, Array.Empty<Clock>()); }
            Thread.Sleep(1000);
        }
    }
    public static List<nint> Taskbars()
    {
        var result = new List<nint>();
        EnumWindows((handle, _) =>
        {
            var cls = new StringBuilder(256); GetClassName(handle, cls, cls.Capacity);
            if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") result.Add(handle);
            return true;
        }, 0);
        return result;
    }
    // 明确标识优先；共享托盘按钮还必须同时具备按钮类型和时间内容。
    public static string CandidateReason(string cls, string id, string name, bool button, Rect bounds, Rect taskbar, bool offscreen)
    {
        if (offscreen || bounds.IsEmpty || bounds.Width <= 5 || bounds.Height <= 5 ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || !taskbar.Contains(bounds)) return "invalid/offscreen bounds";
        if (cls is "SystemTray.DateTimeIcon" or "TrayClockWClass" || id == "ClockButton") return "accepted";
        if (cls is "SystemTray.OmniButton" or "SystemTray.OmniButtonLeft" && id == "SystemTrayIcon" && button)
        {
            var time = DateTime.Now.ToString("t", L.Format);
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"(?<!\d)\d{1,2}[:：]\d{2}(?!\d)") ||
                (!string.IsNullOrWhiteSpace(time) && name.Contains(time, StringComparison.Ordinal))) return "accepted";
            return "shared button without time";
        }
        return "unrecognized identity/type";
    }
    private static Rect WindowBounds(nint handle) => GetWindowRect(handle, out var r)
        ? new Rect(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top)) : Rect.Empty;
    private static string ClassName(nint handle)
    {
        var value = new StringBuilder(256); GetClassName(handle, value, value.Capacity); return value.ToString();
    }
    // 每个任务栏、每个失效控件分别隔离；下一轮重新发现，不持有 UIA 对象。
    public static Clock[] FindClocks() => FindClocks(null);
    private static Clock[] FindClocks(StringBuilder? log)
    {
        var result = new List<Clock>();
        foreach (var handle in Taskbars())
        {
            var bar = WindowBounds(handle);
            log?.AppendLine($"Taskbar {handle} class={ClassName(handle)} bounds={bar} dpi={GetDpiForWindow(handle)} visible={IsWindowVisible(handle)}");
            if (!IsWindowVisible(handle) || bar.IsEmpty) continue;
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var found = root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                foreach (AutomationElement item in found)
                {
                    try
                    {
                        var c = item.Current;
                        var reason = CandidateReason(c.ClassName, c.AutomationId, c.Name, c.ControlType == ControlType.Button,
                            c.BoundingRectangle, bar, c.IsOffscreen);
                        // 诊断只输出结构与判定，不输出通知内容、窗口标题或私人链接。
                        log?.AppendLine($"UIA class={c.ClassName} id={c.AutomationId} type={c.ControlType.ProgrammaticName} bounds={c.BoundingRectangle} result={reason}");
                        if (reason == "accepted") result.Add(new(handle, c.BoundingRectangle));
                    }
                    catch (Exception e) { log?.AppendLine("UIA item error=" + e.GetType().Name); }
                }
            }
            catch (Exception e) { log?.AppendLine("UIA taskbar error=" + e.GetType().Name); }
            // 原生时钟窗口回退独立于 UIA，绝不根据屏幕边缘估算点击区。
            EnumChildWindows(handle, (child, _) =>
            {
                if (ClassName(child) != "TrayClockWClass") return true;
                var bounds = WindowBounds(child);
                var reason = CandidateReason("TrayClockWClass", "", "", false, bounds, bar, !IsWindowVisible(child));
                log?.AppendLine($"Native clock {child} bounds={bounds} result={reason}");
                if (reason == "accepted") result.Add(new(handle, bounds));
                return true;
            }, 0);
        }
        return result.Distinct().ToArray();
    }
    public static string Probe()
    {
        var text = new StringBuilder();
        // 注册表构建号不受进程兼容性清单的版本虚拟化影响。
        using var version = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        text.AppendLine($"Windows build={version?.GetValue("CurrentBuildNumber")}.{version?.GetValue("UBR")} display={version?.GetValue("DisplayVersion")} arch={RuntimeInformation.OSArchitecture}");
        foreach (var c in FindClocks(text))
        {
            var center = new NativePoint { X = (int)(c.Bounds.Left + c.Bounds.Width / 2), Y = (int)(c.Bounds.Top + c.Bounds.Height / 2) };
            var hit = WindowFromPoint(center);
            text.AppendLine($"CLOCK {c.Bounds} centerHit={hit} hitClass={ClassName(hit)} root={GetAncestor(hit, 2)} ownerRoot={GetAncestor(hit, 3)} belongs={BelongsToTaskbar(hit, c.Taskbar)}");
        }
        return text.ToString();
    }
    public static bool HitTest(Rect clock, Rect taskbar, Point point, bool visible, bool belongs)
        => visible && belongs && !clock.IsEmpty && !taskbar.IsEmpty && taskbar.Contains(clock) && clock.Contains(point) && taskbar.Contains(point);
    private static bool BelongsToTaskbar(nint hit, nint taskbar)
    {
        if (hit == 0) return false;
        if (hit == taskbar || IsChild(taskbar, hit)) return true;
        // 某些托盘表面使用拥有者窗口；仅接受同一进程且根拥有者为任务栏的窗口。
        GetWindowThreadProcessId(hit, out var hitProcess);
        GetWindowThreadProcessId(taskbar, out var barProcess);
        return hitProcess != 0 && hitProcess == barProcess && GetAncestor(hit, 3) == taskbar;
    }
    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MouseData>(data);
            var p = new Point(m.Point.X, m.Point.Y);
            if (message == 0x201 || message == 0x203)
            {
                swallowed = Volatile.Read(ref clocks).Any(c => IsWindow(c.Taskbar) && HitTest(c.Bounds, WindowBounds(c.Taskbar), p, IsWindowVisible(c.Taskbar), BelongsToTaskbar(WindowFromPoint(m.Point), c.Taskbar)));
                if (swallowed) { clickPoint = p; return 1; }
                dispatcher.BeginInvoke(() => OutsideClick?.Invoke(p));
            }
            if (message == 0x202 && swallowed)
            {
                swallowed = false;
                var point = clickPoint;
                dispatcher.BeginInvoke(() => Toggle?.Invoke(point));
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose()
    {
        stopped = true;
        if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point; public uint MouseDataValue, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint process);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint parameter);
    private delegate nint HookProc(int nCode, nint wParam, nint lParam);
    private delegate bool EnumProc(nint hWnd, nint parameter);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hWnd, uint flags);
}
