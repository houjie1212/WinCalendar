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
    public static Clock[] FindClocks()
    {
        var result = new List<Clock>();
        foreach (var handle in Taskbars())
        {
            var root = AutomationElement.FromHandle(handle);
            var found = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.DateTimeIcon"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ClockButton"),
                new AndCondition(new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.OmniButtonLeft"), new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"))));
            foreach (AutomationElement item in found)
            {
                // 25H2 使用共享按钮类，必须同时校验时间文本，避免接管其它系统按钮。
                if (item.Current.ClassName == "SystemTray.OmniButtonLeft" && !System.Text.RegularExpressions.Regex.IsMatch(item.Current.Name, @"\d{1,2}[:：]\d{2}") && !item.Current.Name.Contains(DateTime.Now.ToString("t", L.Format), StringComparison.Ordinal)) continue;
                var rect = item.Current.BoundingRectangle;
                if (!item.Current.IsOffscreen && !rect.IsEmpty && rect.Width > 5 && rect.Height > 5) result.Add(new(handle, rect));
            }
        }
        return result.ToArray();
    }
    public static string Probe()
    {
        var text = new StringBuilder();
        foreach (var h in Taskbars())
        {
            text.AppendLine("Taskbar " + h);
            var items = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
            foreach (AutomationElement e in items)
            {
                var c = e.Current;
                if (c.ClassName.Contains("Tray", StringComparison.OrdinalIgnoreCase) || c.AutomationId.Contains("Clock", StringComparison.OrdinalIgnoreCase))
                    text.AppendLine($"{c.ClassName} | {c.AutomationId} | {c.BoundingRectangle} | {c.Name}");
            }
        }
        foreach (var c in FindClocks()) text.AppendLine("CLOCK " + c.Bounds);
        return text.ToString();
    }
    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MouseData>(data);
            var p = new Point(m.Point.X, m.Point.Y);
            if (message == 0x201 || message == 0x203)
            {
                swallowed = Volatile.Read(ref clocks).Any(c => c.Bounds.Contains(p) && IsWindow(c.Taskbar) && GetAncestor(WindowFromPoint(m.Point), 2) == c.Taskbar);
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
