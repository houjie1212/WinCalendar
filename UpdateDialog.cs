using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WinCalendar;

// 更新使用独立模态窗口，禁止重复点击；设置草稿在真正安装前才处理。
public sealed class UpdateDialog : Window
{
    private readonly Func<bool> saveDraft;
    private readonly Action exit;
    private readonly CancellationTokenSource cancel = new();
    private readonly TextBlock status;
    private readonly TextBox notes;
    private readonly ProgressBar progress;
    private readonly Button install;
    private readonly Button close;
    private UpdateRelease? release;
    private bool working, replacing;

    public UpdateDialog(Window owner, Func<bool> saveDraft, Action exit)
    {
        this.saveDraft = saveDraft; this.exit = exit;
        Owner = owner; Title = L.T("CheckUpdate"); Width = 500; Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = owner.Background; Foreground = owner.Foreground; Resources = owner.Resources; FontFamily = owner.FontFamily;
        var panel = new StackPanel { Margin = new Thickness(20) };
        status = Ui.Text(L.T("UpdateChecking"), 14); status.TextWrapping = TextWrapping.Wrap; panel.Children.Add(status);
        notes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 180, Margin = new Thickness(0, 12, 0, 12) };
        AutomationProperties.SetName(notes, L.T("UpdateNotes")); panel.Children.Add(notes);
        progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(progress, L.T("UpdateDownloading")); panel.Children.Add(progress);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        install = Ui.Button(L.T("UpdateInstall"), "UpdateInstall", async () => await Install()); install.IsEnabled = false;
        close = Ui.Button(L.T("Cancel"), "Cancel", Close); buttons.Children.Add(install); buttons.Children.Add(close); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closing += (_, e) => { if (replacing) e.Cancel = true; else cancel.Cancel(); };
        Loaded += async (_, _) => await Check();
    }

    private void Status(string text) { status.Text = text; AutomationProperties.SetName(status, text); }
    private async Task Check()
    {
        try
        {
            release = await UpdateService.Check(cancel.Token);
            if (cancel.IsCancellationRequested) return;
            if (release == null) { Status(L.T("UpdateNoRelease")); return; }
            if (release.Version <= UpdateService.Current) { Status(L.T("UpdateLatest")); return; }
            Status(string.Format(L.T("UpdateAvailable"), UpdateService.Current, release.Version));
            notes.Text = release.Notes; install.IsEnabled = true;
        }
        catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) Status(L.T("UpdateCheckFailed")); }
        catch (Exception e) { Status(L.T(e is UpdateException update ? update.Key : "UpdateCheckFailed")); }
    }

    private async Task Install()
    {
        if (working || release == null || !saveDraft()) return;
        working = true; install.IsEnabled = false; progress.Visibility = Visibility.Visible;
        try
        {
            Status(L.T("UpdateDownloading"));
            var job = await UpdateService.Prepare(release, new Progress<double>(value => progress.Value = value), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            replacing = true; close.IsEnabled = false; Status(L.T("UpdateInstalling"));
            await UpdateInstaller.Launch(job);
            replacing = false;
            exit();
        }
        catch (OperationCanceledException) { if (!cancel.IsCancellationRequested) Status(L.T("UpdateFailed")); }
        catch (Exception e) { Status(L.T(e is UpdateException update ? update.Key : "UpdateCannotInstall")); }
        finally { replacing = false; working = false; close.IsEnabled = true; }
    }
}
