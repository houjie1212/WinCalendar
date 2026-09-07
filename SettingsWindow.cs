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
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private bool startup;
    public SettingsWindow(Window owner, Settings settings, Action exit, Func<Task> refreshData)
    {
        live = settings; draft = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Store.Json), Store.Json)!; this.exit = exit; this.refreshData = refreshData;
        Owner = owner; Title = L.T("Settings"); Width = 520; Height = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResize;
        Background = owner.Background; Foreground = owner.Foreground; Resources = owner.Resources; FontFamily = owner.FontFamily; FontSize = 14;
        startup = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, "WinCalendar", null) != null;
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Render();
        L.Changed += LanguageChanged;
        Closed += (_, _) => L.Changed -= LanguageChanged;
    }
    private void LanguageChanged() { Title = L.T("Settings"); Background = Owner.Background; Foreground = Owner.Foreground; Render(); }
    private void Render()
    {
        body.Children.Clear();
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
        AutomationProperties.SetName(versionLabel, versionLabel.Text); body.Children.Add(versionLabel);
        var footer = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(Ui.Button(L.T("Save"), "Save", Save));
        footer.Children.Add(Ui.Button(L.T("Refresh"), "Refresh", async () => { if (Apply()) { IsEnabled = false; try { await refreshData(); Render(); } finally { IsEnabled = true; } } }));
        footer.Children.Add(Ui.Button(L.T("Cancel"), "Cancel", () => DialogResult = false));
        footer.Children.Add(Ui.Button(L.T("Exit"), "Exit", exit)); body.Children.Add(footer);
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
    private void Edit(Subscription? original)
    {
        var w = Ui.Dialog(this, original == null ? "Add" : "Edit");
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = new TextBox { Text = original?.Name ?? "", Margin = new Thickness(0, 4, 0, 12) };
        var url = new TextBox { Text = original?.Url ?? "", Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(name, L.T("Name")); AutomationProperties.SetName(url, L.T("Url"));
        panel.Children.Add(Ui.Label("Name")); panel.Children.Add(name);
        panel.Children.Add(Ui.Label("Url")); panel.Children.Add(url);
        var kind = new ComboBox { ItemsSource = new[] { L.T("OrdinaryCalendar"), L.T("ChinaHolidays") }, SelectedIndex = original?.Kind == SubscriptionKind.ChinaHolidays ? 1 : 0, Margin = new Thickness(0, 4, 0, 12) };
        AutomationProperties.SetName(kind, L.T("SubscriptionType")); panel.Children.Add(Ui.Label("SubscriptionType")); panel.Children.Add(kind);
        var colors = new[] { "#2563EB", "#16834A", "#C95D12", "#8B5CF6", "#DB2777", "#DC2626" };
        var color = new ComboBox { ItemsSource = new[] { "Blue", "Green", "Orange", "Purple", "Pink", "Red" }.Select(L.T).ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(colors, original?.Color ?? colors[0])), Margin = new Thickness(0, 4, 0, 16) };
        AutomationProperties.SetName(color, L.T("Color")); panel.Children.Add(Ui.Label("Color")); panel.Children.Add(color);
        var error = Ui.Text("", 12); error.Foreground = System.Windows.Media.Brushes.IndianRed; panel.Children.Add(error);
        panel.Children.Add(Ui.Button(L.T("Save"), "Save", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = L.T("InvalidName"); return; }
            Uri uri; try { uri = Download.Validate(url.Text); } catch { error.Text = L.T("InvalidUrl"); return; }
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
