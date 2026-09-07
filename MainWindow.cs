using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WinCalendar;

public sealed class MainWindow : Window
{
    private readonly Settings settings;
    private readonly Subscriptions subscriptions;
    private readonly ClockHook? clock;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1), selected = DateTime.Today;
    private DateTimeOffset lastNetwork = DateTimeOffset.MinValue;
    private bool busy, modal, exit, dataError;
    private Point anchor;
    private ComboBox yearPicker = new(), monthPicker = new();
    private bool renderPending;
    private Rect pickerBounds = Rect.Empty;
    private bool PickerOpen => yearPicker.IsDropDownOpen || monthPicker.IsDropDownOpen;
    private TextBlock status = new();
    private DateTime lastDay = DateTime.Today;
    private readonly bool preview;
    public MainWindow(Settings settings, bool preview = false, bool snapshot = false)
    {
        this.preview = preview;
        this.settings = settings;
        subscriptions = new(settings);
        Title = "WinCalendar";
        Width = 440; Height = 710;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = preview; Topmost = true;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        if (!snapshot)
        {
            clock = new(Dispatcher);
            clock.Toggle += Toggle;
            clock.OutsideClick += p => { if (IsVisible && !modal && !preview && !PhysicalBounds().Contains(p) && !PickerContains(p)) Hide(); };
        }
        Deactivated += (_, _) => { if (!modal && !preview && !PickerOpen) Hide(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || modal) return;
            if (PickerOpen) { yearPicker.IsDropDownOpen = false; monthPicker.IsDropDownOpen = false; }
            else Hide();
            e.Handled = true;
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) { yearPicker.IsDropDownOpen = false; monthPicker.IsDropDownOpen = false; } };
        Closing += (_, e) => { if (!exit) { e.Cancel = true; Hide(); } };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);
            HwndSource.FromHwnd(h)?.AddHook(Messages);
            ApplyTheme();
        };
        ApplyTheme(); Render();
        timer.Tick += async (_, _) =>
        {
            status.Text = StatusText();
            if (lastDay != DateTime.Today) { lastDay = DateTime.Today; Render(); }
            if (!busy && DateTimeOffset.Now - lastNetwork > TimeSpan.FromMinutes(settings.RefreshMinutes)) await RefreshData(true);
        };
        if (!snapshot)
        {
            timer.Start();
            Loaded += async (_, _) => { await RefreshData(false); await RefreshData(true); };
        }
    }
    private nint Messages(nint h, int msg, nint w, nint l, ref bool handled)
    {
        if (msg is 0x1A or 0x31A or 0x7E) Dispatcher.BeginInvoke(RefreshEnvironment);
        if (msg == 0x2E0) Dispatcher.BeginInvoke(() => Position(anchor));
        return 0;
    }
    public void Cleanup() { timer.Stop(); clock?.Dispose(); }
    public async void RefreshEnvironment() { L.Reload(); ApplyTheme(); Render(); if (IsVisible) Position(anchor); await RefreshData(false); }
    public static DateTime GridStart(DateTime month, DayOfWeek first) => month.AddDays(-((7 + (int)month.DayOfWeek - (int)first) % 7));
    // 前后各预加载一个完整月历网格，结束日期为排他边界。
    public static (DateTime From, DateTime To) PreloadRange(DateTime month, DayOfWeek first) =>
        (GridStart(month.AddMonths(-1), first), GridStart(month.AddMonths(1), first).AddDays(42));
    private void ApplyTheme()
    {
        bool dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0;
        Ui.Theme(this, dark);
        int flag = dark ? 1 : 0;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) DwmSetWindowAttribute(handle, 20, ref flag, 4);
    }
    public void ShowAtCursor()
    {
        var p = Forms.Cursor.Position;
        Toggle(new Point(p.X, p.Y));
    }
    private void Toggle(Point point)
    {
        if (modal) return;
        if (IsVisible) { Hide(); return; }
        anchor = point;
        // 先创建 HWND 并定位再显示，避免在屏幕中央短暂闪现。
        new WindowInteropHelper(this).EnsureHandle();
        Position(point); Show(); ShowWindow(new WindowInteropHelper(this).Handle, 5); Position(point); Activate(); Focus();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }
    private Rect PhysicalBounds()
    {
        if (GetWindowRect(new WindowInteropHelper(this).Handle, out var r)) return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return Rect.Empty;
    }
    private void Position(Point point)
    {
        var monitor = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
        var area = monitor.WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        Height = Math.Min(710, area.Height / scale - 16);
        Width = Math.Min(440, area.Width / scale - 16);
        int w = (int)(Width * scale), height = (int)(Height * scale);
        // 右侧与底部均保留 8 个物理像素，不随时钟点击位置变化。
        int x = area.Right - w - 8;
        int y = point.Y <= area.Top ? area.Top + 8 : area.Bottom - height - 8;
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, x, y, w, height, 0x0014);
    }
    private string StatusText() => clock == null ? L.T("Preview") : busy ? L.T("Refreshing") : dataError ? L.T("SaveFailed") : !clock.Available ? L.T("ClockMissing") : settings.Sources.Any(x => x.Enabled && x.Failed) ? L.T("RefreshFailed") : L.T("ClockReady");
    private async Task RefreshData(bool network)
    {
        if (busy) return;
        busy = true; status.Text = StatusText();
        if (network) lastNetwork = DateTimeOffset.Now;
        var displayed = month;
        try
        {
            var range = PreloadRange(displayed, (DayOfWeek)(settings.FirstDay ?? (int)L.Format.DateTimeFormat.FirstDayOfWeek));
            await subscriptions.Refresh(range.From, range.To, network);
            dataError = false;
        }
        catch { dataError = true; }
        finally { busy = false; Render(); }
        // 加载期间的多次翻月合并为最新月份；原有日程在后台加载完成前继续显示。
        if (displayed != month) await RefreshData(false);
    }
    private void MoveMonth(int offset) => ChangeMonth(month.AddMonths(offset));
    // 所有年月入口共用同一切换逻辑，不改变选中日期或重复加载相同月份。
    private async void ChangeMonth(DateTime next)
    {
        if (next.Year is < 1901 or > 2100 || next == month) return;
        month = next; Render(); await RefreshData(false);
    }
    // 下拉弹窗使用独立 HWND，需按物理坐标纳入面板内部区域。
    private bool PickerContains(Point point)
    {
        foreach (var picker in new[] { yearPicker, monthPicker })
            if (picker.IsDropDownOpen && picker.Template.FindName("PART_Popup", picker) is Popup popup &&
                popup.Child != null && PresentationSource.FromVisual(popup.Child) is HwndSource source &&
                GetWindowRect(source.Handle, out var rect))
                pickerBounds = new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        return pickerBounds.Contains(point);
    }
    private ComboBox MonthSelector(string key, object[] items, int index)
    {
        var picker = new ComboBox
        {
            ItemsSource = items, SelectedIndex = index, IsEditable = false, MaxDropDownHeight = 280,
            MinWidth = 60, Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 14, ToolTip = L.T(key)
        };
        AutomationProperties.SetName(picker, L.T(key));
        picker.DropDownOpened += (_, _) => Dispatcher.BeginInvoke(() => PickerContains(new Point()), DispatcherPriority.Loaded);
        picker.DropDownClosed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            // 等本次点击处理结束再清除弹窗范围，避免钩子误判刚选中的列表项。
            if (PickerOpen) return;
            pickerBounds = Rect.Empty;
            if (renderPending) Render();
        }, DispatcherPriority.ContextIdle);
        picker.PreviewMouseWheel += (_, e) => { if (!picker.IsDropDownOpen) e.Handled = true; };
        return picker;
    }
    // 仅月历区域接管滚轮，日程列表继续使用自身滚动行为。
    private void CalendarMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || PickerOpen || yearPicker.IsMouseOver || monthPicker.IsMouseOver) return;
        e.Handled = true;
        MoveMonth(e.Delta > 0 ? -1 : 1);
    }
    // 供离屏布局检查使用，不读取订阅缓存或发起网络请求。
    internal void RenderPreviewEvents(AgendaEvent[] events)
    {
        if (!preview) throw new InvalidOperationException();
        subscriptions.Events.Clear(); subscriptions.Events.AddRange(events); Render();
    }
    private void Render()
    {
        // 异步刷新仅延迟重建控件，不关闭用户正在操作的下拉列表。
        if (PickerOpen) { renderPending = true; return; }
        renderPending = false;
        bool focusYear = yearPicker.IsKeyboardFocusWithin, focusMonth = monthPicker.IsKeyboardFocusWithin;
        var layout = new DockPanel { Margin = new Thickness(20, 16, 20, 12), LastChildFill = true };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Ui.Button("⚙", "Settings", OpenSettings));
        buttons.Children.Add(Ui.Button("×", "Close", Hide));
        DockPanel.SetDock(buttons, Dock.Right); title.Children.Add(buttons);
        top.Children.Add(title);
        top.Children.Add(Ui.Text(L.T("LocalTime"), 12));
        var nav = new DockPanel { Margin = new Thickness(0, 16, 0, 12) };
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal };
        navButtons.Children.Add(Ui.Button("‹", "Previous", () => MoveMonth(-1)));
        navButtons.Children.Add(Ui.Button(L.T("Today"), "Today", async () => { selected = DateTime.Today; month = new(selected.Year, selected.Month, 1); Render(); await RefreshData(false); }));
        navButtons.Children.Add(Ui.Button("›", "Next", () => MoveMonth(1)));
        DockPanel.SetDock(navButtons, Dock.Right); nav.Children.Add(navButtons);
        var selectors = new Grid();
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        yearPicker = MonthSelector("SelectYear", Enumerable.Range(1901, 200).Select(y => (object)y.ToString(CultureInfo.InvariantCulture)).ToArray(), month.Year - 1901);
        monthPicker = MonthSelector("SelectMonth", Enumerable.Range(1, 12).Select(m => (object)L.Format.DateTimeFormat.GetMonthName(m)).ToArray(), month.Month - 1);
        // 先设置默认选项，再绑定事件，防止构造控件时触发加载。
        yearPicker.SelectionChanged += (_, _) => { if (yearPicker.SelectedIndex >= 0) ChangeMonth(new DateTime(yearPicker.SelectedIndex + 1901, month.Month, 1)); };
        monthPicker.SelectionChanged += (_, _) => { if (monthPicker.SelectedIndex >= 0) ChangeMonth(new DateTime(month.Year, monthPicker.SelectedIndex + 1, 1)); };
        selectors.Children.Add(yearPicker); Grid.SetColumn(monthPicker, 1); selectors.Children.Add(monthPicker);
        nav.Children.Add(selectors); top.Children.Add(nav);
        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        int first = settings.FirstDay ?? (int)L.Format.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++) weekdays.Children.Add(new TextBlock { Text = L.Format.DateTimeFormat.GetShortestDayName((DayOfWeek)((first + i) % 7)), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
        top.Children.Add(weekdays);
        var grid = new UniformGrid { Columns = 7, Rows = 6, Background = Brushes.Transparent };
        grid.PreviewMouseWheel += CalendarMouseWheel;
        weekdays.PreviewMouseWheel += CalendarMouseWheel;
        nav.PreviewMouseWheel += CalendarMouseWheel;
        weekdays.Background = nav.Background = Brushes.Transparent;
        var begin = GridStart(month, (DayOfWeek)first);
        for (int i = 0; i < 42; i++)
        {
            DateTime date = begin.AddDays(i); var holiday = subscriptions.HolidayForDay(date);
            var cell = new Grid { Height = 51, Margin = new Thickness(1) };
            var lunar = settings.ShowChineseLunar ? ChineseLunar.Label(date) : "";
            var name = holiday.Names.Select(L.Festival).FirstOrDefault() ?? lunar;
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 6, 1, 1) };
            var number = Ui.Text(date.Day.ToString(L.Format), 15, date == DateTime.Today); number.HorizontalAlignment = HorizontalAlignment.Center;
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) number.Foreground = Brushes.Coral;
            stack.Children.Add(number);
            stack.Children.Add(new TextBlock { Text = name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Center });
            cell.Children.Add(stack);
            if (holiday.IsOffDay.HasValue)
            {
                var badge = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(2), Padding = new Thickness(2, 0, 2, 0), Background = Ui.Brush(holiday.IsOffDay == true ? "#16744A" : "#A94A12"), Child = new TextBlock { Text = L.T(holiday.IsOffDay == true ? "Off" : "Work"), FontSize = 8, Foreground = Brushes.White } };
                cell.Children.Add(badge);
            }
            var b = new Button { Content = cell, Padding = new Thickness(0), Margin = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(date == DateTime.Today ? 1 : 0), BorderBrush = Ui.Brush("#3B82F6"), Opacity = date.Month == month.Month ? 1 : .55 };
            // 来源按设置顺序等宽分色，选中状态只改变边框。
            var daySources = subscriptions.SourcesForDay(date);
            if (daySources.Length > 0)
            {
                b.Background = Ui.SubscriptionBackground(daySources.Select(source => source.Color).ToArray(), (SolidColorBrush)Background);
                number.Foreground = Foreground;
                if (date == selected) { b.BorderThickness = new Thickness(2); b.BorderBrush = Foreground; }
            }
            else if (date == selected) { b.Background = Ui.Brush("#285AA8"); b.Foreground = Brushes.White; number.Foreground = Brushes.White; }
            var accessible = date.ToString("D", L.Format) + " · " + string.Join(" · ", holiday.Names.Select(L.Festival));
            if (daySources.Length > 0) accessible += " · " + L.T("Subscriptions") + ": " + string.Join(" · ", daySources.Select(source => source.Name));
            if (lunar.Length > 0) accessible += " · " + L.T("ChineseLunar") + " " + lunar;
            if (holiday.Conflict) accessible += " · " + L.T("HolidayConflict");
            else if (holiday.IsOffDay.HasValue) accessible += " · " + L.T(holiday.IsOffDay.Value ? "OffFull" : "WorkFull");
            b.ToolTip = accessible; AutomationProperties.SetName(b, accessible);
            b.Click += (_, _) => { selected = date; Render(); }; grid.Children.Add(b);
        }
        top.Children.Add(grid);
        top.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 8) });
        top.Children.Add(Ui.Text(selected.ToString("D", L.Format), 14, true));
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom);
        status = Ui.Text(StatusText(), 11); status.TextWrapping = TextWrapping.Wrap; bottom.Children.Add(status); layout.Children.Add(bottom);
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var items = subscriptions.ForDay(selected).ToArray();
        if (items.Length == 0) { list.Children.Add(Ui.Text(L.T("NoEvents"), 14)); if (settings.Sources.Count == 0) list.Children.Add(Ui.Text(L.T("NoSubscriptions"), 12)); }
        foreach (var ev in items)
        {
            var entry = new StackPanel(); entry.Children.Add(Ui.Text(ev.Title, 14, true));
            entry.Children.Add(Ui.Text(settings.Sources.FirstOrDefault(s => s.Id == ev.SourceId)?.Name ?? "", 11));
            entry.Children.Add(Ui.Text(EventTime(ev), 12));
            var b = new Button { Content = entry, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(10, 6, 8, 6), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = Ui.Brush(ev.Color) };
            AutomationProperties.SetName(b, ev.Title + " " + EventTime(ev)); b.Click += (_, _) => Detail(ev); list.Children.Add(b);
        }
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
        if (focusYear) yearPicker.Focus();
        else if (focusMonth) monthPicker.Focus();
    }
    public static string EventTime(AgendaEvent ev) => ev.AllDay ? L.T("AllDay") : ev.Start.ToString("g", L.Format) + " – " + ev.End.ToString(ev.Start.Date == ev.End.Date ? "t" : "g", L.Format);
    private void Detail(AgendaEvent ev)
    {
        modal = true;
        try
        {
            var w = Ui.Dialog(this, "Details");
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(Ui.Text(ev.Title, 20, true)); panel.Children.Add(Ui.Text(settings.Sources.FirstOrDefault(s => s.Id == ev.SourceId)?.Name ?? "", 12)); panel.Children.Add(Ui.Text(EventTime(ev), 13));
            if (ev.Location.Length > 0) panel.Children.Add(Ui.Text(ev.Location, 14));
            panel.Children.Add(new TextBox { Text = ev.Description, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 16, 0, 12) });
            panel.Children.Add(Ui.Button(L.T("Close"), "Close", w.Close)); w.Content = panel; w.ShowDialog();
        }
        finally { modal = false; Activate(); }
    }
    private async void OpenSettings()
    {
        modal = true;
        try
        {
            var dialog = new SettingsWindow(this, settings, () => { exit = true; Cleanup(); Application.Current.Shutdown(); }, () => RefreshData(true));
            if (dialog.ShowDialog() == true) { Render(); await RefreshData(true); }
        }
        finally { modal = false; if (!exit) Activate(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, int attribute, ref int value, int size);
}

public static class Ui
{
    private sealed record ResourceTag(string Key, bool TranslateContent);
    public static Brush Brush(string color)
    {
        try { return (Brush)new BrushConverter().ConvertFromInvariantString(color)!; }
        catch { return Brushes.DodgerBlue; }
    }
    // 订阅色占 28%，其余使用系统主题背景，避免深色订阅遮住文字。
    public static SolidColorBrush SubscriptionBackground(string color, SolidColorBrush background)
    {
        var tint = ((SolidColorBrush)Brush(color)).Color;
        var baseColor = background.Color;
        byte Blend(byte value, byte basis) => (byte)Math.Round(value * .28 + basis * .72);
        return new SolidColorBrush(Color.FromRgb(Blend(tint.R, baseColor.R), Blend(tint.G, baseColor.G), Blend(tint.B, baseColor.B)));
    }
    // 相邻分区在同一偏移处放置两个色点，边界清晰且不混成渐变色。
    public static Brush SubscriptionBackground(string[] colors, SolidColorBrush background)
    {
        // 仅限制背景分区数量，提示与详情继续使用全部订阅。
        colors = colors.Take(3).ToArray();
        if (colors.Length == 0) return background;
        if (colors.Length == 1) return SubscriptionBackground(colors[0], background);
        var brush = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (int i = 0; i < colors.Length; i++)
        {
            var color = SubscriptionBackground(colors[i], background).Color;
            brush.GradientStops.Add(new GradientStop(color, (double)i / colors.Length));
            brush.GradientStops.Add(new GradientStop(color, (double)(i + 1) / colors.Length));
        }
        brush.Freeze();
        return brush;
    }
    public static TextBlock Text(string text, double size = 14, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
    public static TextBlock Label(string key) { var t = Text(L.T(key)); t.Tag = new ResourceTag(key, true); return t; }
    public static Button Button(string text, string key, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2), MinHeight = 30, ToolTip = L.T(key) };
        b.Tag = new ResourceTag(key, text == L.T(key));
        AutomationProperties.SetName(b, L.T(key)); b.Click += (_, _) => action(); return b;
    }
    public static void Theme(Window w, bool dark)
    {
        w.Background = Brush(dark ? "#202020" : "#F8F9FB"); w.Foreground = Brush(dark ? "#F5F5F5" : "#20242C");
        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, Brush(dark ? "#2C2C2C" : "#EEF1F5")));
        button.Setters.Add(new Setter(Control.ForegroundProperty, w.Foreground));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.AppendChild(presenter);
        button.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = border }));
        var hover = new Trigger { Property = System.Windows.UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(UIElement.OpacityProperty, .8)); button.Triggers.Add(hover);
        var focus = new Trigger { Property = System.Windows.UIElement.IsKeyboardFocusedProperty, Value = true }; focus.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.DodgerBlue)); focus.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2))); button.Triggers.Add(focus);
        w.Resources[typeof(Button)] = button;
    }
    public static Window Dialog(Window owner, string key)
    {
        var w = new Window { Title = L.T(key), Owner = owner, Width = 480, SizeToContent = SizeToContent.Height, MaxHeight = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Background = owner.Background, Foreground = owner.Foreground, FontFamily = owner.FontFamily, FontSize = 14 };
        void TranslateElement(DependencyObject element)
        {
            if (element is FrameworkElement { Tag: ResourceTag resource } f)
            {
                if (f is TextBlock text) text.Text = L.T(resource.Key);
                if (f is Button b) { if (resource.TranslateContent) b.Content = L.T(resource.Key); b.ToolTip = L.T(resource.Key); AutomationProperties.SetName(b, L.T(resource.Key)); }
            }
            foreach (var child in LogicalTreeHelper.GetChildren(element)) if (child is DependencyObject d) TranslateElement(d);
        }
        void Translate() { w.Title = L.T(key); w.Background = owner.Background; w.Foreground = owner.Foreground; TranslateElement(w); }
        L.Changed += Translate; w.Closed += (_, _) => L.Changed -= Translate;
        w.Resources = owner.Resources; w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); }; return w;
    }
}
