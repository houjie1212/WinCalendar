using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace WinCalendar;

public sealed class App : Application
{
    private Mutex? instance;
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 7 && args[0] == "--parse-ics") return IcsParser.Worker(args);
        if (args.Length == 2 && args[0] == "--integration-check") return IntegrationChecks.Run(args[1]).GetAwaiter().GetResult();
        L.Reload();
        if (args.Length == 2 && args[0] == "--render-previews") return IntegrationChecks.RenderPreviews(args[1]);
        if (args.Length == 2 && args[0] == "--probe-clock")
        {
            try { File.WriteAllText(args[1], ClockHook.Probe()); return 0; }
            catch (Exception e) { File.WriteAllText(args[1], e.GetType().Name); return 1; }
        }
        var app = new App();
        app.instance = new Mutex(true, "Local\\WinCalendar", out bool fresh);
        if (!fresh) { MessageBox.Show(L.T("AlreadyRunning"), "WinCalendar"); app.instance.Dispose(); return 0; }
        try
        {
            var settings = Store.Load();
            var window = new MainWindow(settings, args.Contains("--preview"));
            app.MainWindow = window;
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SystemEvents.UserPreferenceChanged += (_, _) => app.Dispatcher.BeginInvoke(() => { L.Reload(); window.RefreshEnvironment(); });
            SystemEvents.TimeChanged += (_, _) => app.Dispatcher.BeginInvoke(() => { L.Reload(); window.RefreshEnvironment(); });
            if (!args.Contains("--background")) window.ShowAtCursor();
            app.Run();
            window.Cleanup();
            return 0;
        }
        catch (Exception e)
        {
            // 不展示可能包含私人链接的异常文本。
            MessageBox.Show(L.T("Fatal") + "\n" + e.GetType().Name, "WinCalendar", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally { app.instance.ReleaseMutex(); app.instance.Dispose(); }
    }
}
