using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WinCalendar;

// 编辑副本，只有保存成功才替换运行中的配置；取消不会丢失原有订阅。
public sealed class SettingsWindow : Window
{
    private readonly Settings live;
    private readonly Settings draft;
    private readonly Action exit;
    private readonly Func<Task> refreshData;
    private readonly StackPanel body = new() { Margin = new Thickness(20) };
    private readonly DockPanel page = new();
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private bool startup;
    public SettingsWindow(Window owner, Settings settings, Action exit, Func<Task> refreshData)
    {
        live = settings; draft = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Store.Json), Store.Json)!; this.exit = exit; this.refreshData = refreshData;
        Owner = owner; Title = L.T("Settings"); Width = 520; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResize;
        Background = owner.Background; Foreground = owner.Foreground; Resources = owner.Resources; FontFamily = owner.FontFamily; FontSize = 14;
        startup = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, "WinCalendar", null) != null;
        Content = page;
        Render();
        L.Changed += LanguageChanged;
        Closed += (_, _) => L.Changed -= LanguageChanged;
    }
    private void LanguageChanged() { Title = L.T("Settings"); Background = Owner.Background; Foreground = Owner.Foreground; Render(); }
    private void Render()
    {
        body.Children.Clear();
        foreach (var scroll in page.Children.OfType<ScrollViewer>()) scroll.Content = null;
        page.Children.Clear();
        // 底部操作栏固定显示，设置内容单独滚动。
        var exitButton = Ui.Button(L.T("Exit"), "Exit", exit);
        exitButton.HorizontalAlignment = HorizontalAlignment.Right;
        exitButton.VerticalAlignment = VerticalAlignment.Top;
        var bottomBar = new DockPanel { Margin = new Thickness(20, 8, 20, 20) };
        DockPanel.SetDock(exitButton, Dock.Right); bottomBar.Children.Add(exitButton);
        DockPanel.SetDock(bottomBar, Dock.Bottom); page.Children.Add(bottomBar);
        page.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Ui.Text(L.T("Subscriptions"), 22, true));
        body.Children.Add(Ui.Text(L.T("Privacy"), 12));
        foreach (var source in draft.Sources.ToArray())
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 2) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Ui.Button(L.T("Edit"), "Edit", () => Edit(source)));
            actions.Children.Add(Ui.Button(L.T("Delete"), "Delete", () => { if (MessageBox.Show(this, L.T("ConfirmDelete"), "WinCalendar", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { draft.Sources.Remove(source); Render(); } }));
            DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            var name = new CheckBox { Content = source.Name, IsChecked = source.Enabled, VerticalAlignment = VerticalAlignment.Center, Foreground = Foreground };
            AutomationProperties.SetName(name, source.Name + " " + L.T("Enabled"));
            name.Checked += (_, _) => source.Enabled = true; name.Unchecked += (_, _) => source.Enabled = false;
            row.Children.Add(name); body.Children.Add(row);
            body.Children.Add(Ui.Text(L.T("LastSuccess") + ": " + (source.LastSuccess?.LocalDateTime.ToString("g", L.Format) ?? L.T("Never")), 11));
        }
        body.Children.Add(Ui.Button(L.T("Add"), "Add", () => Edit(null)));
        body.Children.Add(Ui.Text(L.T("CommonSubscriptions"), 14, true));
        var presets = new WrapPanel();
        foreach (var preset in SubscriptionPreset.All)
            presets.Children.Add(Ui.Button(L.T(preset.NameKey), preset.NameKey, () => Edit(draft.Sources.FirstOrDefault(source => preset.Matches(source.Url)), preset)));
        body.Children.Add(presets);
        body.Children.Add(new Separator { Margin = new Thickness(0, 15, 0, 10) });
        body.Children.Add(Ui.Text(L.T("RefreshMinutes")));
        var refresh = new ComboBox { ItemsSource = new[] { 5, 15, 30, 60, 180, 1440, draft.RefreshMinutes }.Distinct().Order().ToArray(), SelectedItem = draft.RefreshMinutes, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(refresh, L.T("RefreshMinutes"));
        refresh.SelectionChanged += (_, _) => { if (refresh.SelectedItem is int minutes) draft.RefreshMinutes = minutes; }; body.Children.Add(refresh);
        body.Children.Add(Ui.Text(L.T("FirstDay")));
        var days = new[] { L.T("System") }.Concat(Enumerable.Range(0, 7).Select(i => L.Format.DateTimeFormat.GetDayName((DayOfWeek)i))).ToArray();
        var first = new ComboBox { ItemsSource = days, SelectedIndex = draft.FirstDay.HasValue ? draft.FirstDay.Value + 1 : 0, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(first, L.T("FirstDay")); first.SelectionChanged += (_, _) => draft.FirstDay = first.SelectedIndex == 0 ? null : first.SelectedIndex - 1; body.Children.Add(first);
        var lunar = new CheckBox { Content = L.T("ChineseLunar"), IsChecked = draft.ShowChineseLunar, Foreground = Foreground, Margin = new Thickness(0, 5, 0, 12) };
        AutomationProperties.SetName(lunar, L.T("ChineseLunar"));
        lunar.Checked += (_, _) => draft.ShowChineseLunar = true; lunar.Unchecked += (_, _) => draft.ShowChineseLunar = false; body.Children.Add(lunar);
        var start = new CheckBox { Content = L.T("Startup"), IsChecked = startup, Foreground = Foreground, Margin = new Thickness(0, 5, 0, 12) };
        start.Checked += (_, _) => startup = true; start.Unchecked += (_, _) => startup = false; body.Children.Add(start);
        body.Children.Add(Ui.Text(L.T("Language"), 12));
        // 从当前程序元数据读取工程版本，不显示构建提交哈希。
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "";
        var versionLabel = Ui.Text(string.Format(L.T("VersionFormat"), version), 12);
        AutomationProperties.SetName(versionLabel, versionLabel.Text);
        var versionRow = new WrapPanel(); versionRow.Children.Add(versionLabel);
        versionRow.Children.Add(Ui.Button(L.T("CheckUpdate"), "CheckUpdate", () => new UpdateDialog(this, SaveBeforeUpdate, exit).ShowDialog()));
        body.Children.Add(versionRow);
        var footer = new WrapPanel();
        footer.Children.Add(Ui.Button(L.T("Save"), "Save", Save));
        footer.Children.Add(Ui.Button(L.T("Refresh"), "Refresh", async () => { if (Apply()) { IsEnabled = false; try { await refreshData(); Render(); } finally { IsEnabled = true; } } }));
        footer.Children.Add(Ui.Button(L.T("Cancel"), "Cancel", () => DialogResult = false));
        var uninstall = Ui.Button(L.T("Uninstall"), "Uninstall", async () => await Uninstall());
        uninstall.IsEnabled = UninstallService.Find() != null;
        if (!uninstall.IsEnabled)
        {
            uninstall.ToolTip = L.T("UninstallUnavailable");
            AutomationProperties.SetHelpText(uninstall, L.T("UninstallUnavailable"));
            ToolTipService.SetShowOnDisabled(uninstall, true);
        }
        footer.Children.Add(uninstall); bottomBar.Children.Add(footer);
    }
    // 检查版本不保存设置；确认安装时才提示处理尚未保存的修改。
    private bool SaveBeforeUpdate()
    {
        bool savedStartup = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, "WinCalendar", null) != null;
        if (JsonSerializer.Serialize(draft, Store.Json) == JsonSerializer.Serialize(live, Store.Json) && startup == savedStartup) return true;
        var prompt = Ui.Dialog(this, "CheckUpdate");
        var panel = new StackPanel { Margin = new Thickness(20) };
        var text = Ui.Text(L.T("UpdateUnsaved")); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
        panel.Children.Add(Ui.Button(L.T("UpdateSaveContinue"), "UpdateSaveContinue", () => { if (Apply()) prompt.DialogResult = true; }));
        panel.Children.Add(Ui.Button(L.T("UpdateCancel"), "UpdateCancel", () => prompt.DialogResult = false));
        prompt.Content = panel;
        return prompt.ShowDialog() == true;
    }
    // 退出后才运行系统卸载器，避免正在运行的实例阻止卸载。
    private async Task Uninstall()
    {
        if (MessageBox.Show(this, L.T("UninstallConfirm"), L.T("Uninstall"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        IsEnabled = false;
        try { await UninstallService.Launch(); exit(); }
        catch { MessageBox.Show(this, L.T("UninstallFailed"), L.T("Uninstall")); }
        finally { IsEnabled = true; }
    }
    private void Save()
    {
        if (Apply()) DialogResult = true;
    }
    private bool Apply()
    {
        try
        {
            // 配置落盘后才更新内存；注册表变更失败则还原配置，避免半保存。
            Store.Save(draft);
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (startup) key.SetValue("WinCalendar", "\"" + Environment.ProcessPath + "\" --background");
                else key.DeleteValue("WinCalendar", false);
            }
            catch { Store.Save(live); MessageBox.Show(this, L.T("StartupError"), "WinCalendar"); return false; }
            // 保存后仍保持编辑副本独立，避免“立即刷新”之后再取消却影响运行中的订阅。
            live.Sources = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(draft, Store.Json), Store.Json)!.Sources;
            live.ShowChineseLunar = draft.ShowChineseLunar;
            live.FirstDay = draft.FirstDay; live.RefreshMinutes = draft.RefreshMinutes;
            return true;
        }
        catch { MessageBox.Show(this, L.T("SaveFailed"), "WinCalendar"); return false; }
    }
    private void Edit(Subscription? original, SubscriptionPreset? preset = null)
    {
        preset ??= SubscriptionPreset.All.FirstOrDefault(item => original != null && item.Matches(original.Url));
        var initial = original ?? preset?.Create();
        var w = Ui.Dialog(this, original == null ? "Add" : "Edit");
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = new TextBox { Text = initial?.Name ?? "", Margin = new Thickness(0, 4, 0, 12) };
        var url = new TextBox { Text = initial?.Url ?? "", Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(name, L.T("Name")); AutomationProperties.SetName(url, L.T("Url"));
        panel.Children.Add(Ui.Label("Name")); panel.Children.Add(name);
        // 用户主动选择自定义时清空地址；初始化及语言刷新不清空已有内容。
        var options = preset?.Options.ToArray() ?? Array.Empty<SubscriptionOption>();
        var sourceChoice = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        bool syncingSource = false;
        void UpdateSourceChoice()
        {
            syncingSource = true;
            try
            {
                sourceChoice.ItemsSource = options.Select(option => L.T(option.NameKey)).Append(L.T("CustomSource")).ToArray();
                int selectedOption = Array.FindIndex(options, option => Download.SameUrl(option.Url, url.Text));
                sourceChoice.SelectedIndex = selectedOption < 0 ? options.Length : selectedOption;
                AutomationProperties.SetName(sourceChoice, L.T("SubscriptionSource"));
            }
            finally { syncingSource = false; }
        }
        sourceChoice.SelectionChanged += (_, _) =>
        {
            if (syncingSource || sourceChoice.SelectedIndex < 0) return;
            if (sourceChoice.SelectedIndex < options.Length) url.Text = options[sourceChoice.SelectedIndex].Url;
            else { url.Clear(); url.Focus(); }
        };
        if (options.Length > 0)
        {
            panel.Children.Add(Ui.Label("SubscriptionSource")); panel.Children.Add(sourceChoice);
            url.TextChanged += (_, _) => UpdateSourceChoice();
            UpdateSourceChoice();
        }
        panel.Children.Add(Ui.Label("Url")); panel.Children.Add(url);
        var kind = new ComboBox { ItemsSource = new[] { L.T("OrdinaryCalendar"), L.T("ChinaHolidays") }, SelectedIndex = initial?.Kind == SubscriptionKind.ChinaHolidays ? 1 : 0, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(kind, L.T("SubscriptionType")); panel.Children.Add(Ui.Label("SubscriptionType")); panel.Children.Add(kind);
        var colors = new[] { "#2563EB", "#16834A", "#C95D12", "#8B5CF6", "#DB2777", "#DC2626" };
        var color = new ComboBox { ItemsSource = new[] { "Blue", "Green", "Orange", "Purple", "Pink", "Red" }.Select(L.T).ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(colors, initial?.Color ?? colors[0])), Margin = new Thickness(0, 4, 0, 16) };
        AutomationProperties.SetName(color, L.T("Color")); panel.Children.Add(Ui.Label("Color")); panel.Children.Add(color);
        var error = Ui.Text("", 12); error.Foreground = System.Windows.Media.Brushes.IndianRed; panel.Children.Add(error);
        panel.Children.Add(Ui.Button(L.T("Save"), "Save", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = L.T("InvalidName"); return; }
            Uri uri; try { uri = Download.Validate(url.Text); } catch { error.Text = L.T("InvalidUrl"); return; }
            if (draft.Sources.Any(other => other != original && Download.SameUrl(other.Url, uri.AbsoluteUri))) { error.Text = L.T("DuplicateSubscription"); return; }
            var source = original ?? new Subscription();
            // 更换链接使用新缓存键，失败时不会误显示旧链接的日程。
            if (source.Url != uri.AbsoluteUri) { source.Id = Guid.NewGuid().ToString("N"); source.LastSuccess = null; }
            source.Kind = kind.SelectedIndex == 1 ? SubscriptionKind.ChinaHolidays : SubscriptionKind.Ordinary;
            source.Name = name.Text.Trim(); source.Url = uri.AbsoluteUri; source.Color = colors[color.SelectedIndex];
            if (original == null) draft.Sources.Add(source);
            w.DialogResult = true;
        }));
        void Translate()
        {
            if (options.Length > 0) UpdateSourceChoice();
            var selectedKind = kind.SelectedIndex;
            kind.ItemsSource = new[] { L.T("OrdinaryCalendar"), L.T("ChinaHolidays") }; kind.SelectedIndex = selectedKind;
            AutomationProperties.SetName(kind, L.T("SubscriptionType"));
            var index = color.SelectedIndex;
            color.ItemsSource = new[] { "Blue", "Green", "Orange", "Purple", "Pink", "Red" }.Select(L.T).ToArray(); color.SelectedIndex = index;
            AutomationProperties.SetName(name, L.T("Name")); AutomationProperties.SetName(url, L.T("Url")); AutomationProperties.SetName(color, L.T("Color")); error.Text = "";
        }
        L.Changed += Translate;
        w.Content = panel;
        try { if (w.ShowDialog() == true) Render(); } finally { L.Changed -= Translate; }
    }
}
