using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WeeklyTasks.Core;
using Forms = System.Windows.Forms;

namespace WeeklyTasks.App;

public partial class MainWindow : Window
{
    private readonly Storage _storage;
    private readonly WidgetSettings _settings;
    private readonly bool _demo;
    private SavedPlan? _saved;
    private Forms.NotifyIcon? _tray;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? _refresh;
    private bool _exit, _loaded;
    private DateOnly _shownDate;
    private DateTime _lastHomeWrite, _lastCacheWrite;
    private string? _currentHash;

    public MainWindow(Storage storage, WidgetSettings settings, bool demo, string? export)
    {
        _storage = storage; _settings = settings; _demo = demo;
        InitializeComponent(); ApplyTheme();
        if (demo) Title += " — デモ";
        Width = Math.Clamp(double.IsFinite(settings.Width) ? settings.Width : 390, 340, 1000);
        Height = Math.Clamp(double.IsFinite(settings.Height) ? settings.Height : 600, 420, 1400);
        Topmost = settings.AlwaysOnTop;
        var area = SystemParameters.WorkArea;
        Left = settings.Left is double left && double.IsFinite(left) ? left : area.Right - Width - 22;
        Top = settings.Top is double top && double.IsFinite(top) ? top : area.Top + 28;
        EnsureVisible();
        _saved = demo ? DemoPlan() : storage.LoadPlan();
        RenderDays();
        Loaded += async (_, _) =>
        {
            _loaded = true;
            if (!demo) { CreateTray(); SetStartup(settings.StartWithWindows); settings.StartWithWindows = File.Exists(StartupPath); storage.SaveSettings(settings); }
            CheckSource();
            _timer.Tick += (_, _) => Poll(); _timer.Start();
            if (export is not null)
            {
                await Task.Delay(400);
                ExportImage(export); _exit = true; System.Windows.Application.Current.Shutdown();
            }
        };
        LocationChanged += (_, _) => SaveGeometry(); SizeChanged += (_, _) => SaveGeometry();
        Closing += OnClosing;
        Closed += (_, _) => System.Windows.Application.Current.Shutdown();
    }

    private void ApplyTheme()
    {
        bool dark = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) as int?) == 0;
        if (!dark) return;
        foreach (var (key, color) in new[] { ("CanvasBrush", "#192421"), ("CardBrush", "#23332D"), ("TextBrush", "#E5ECE7"), ("MutedBrush", "#A2B4AC"), ("LineBrush", "#36463E"), ("AccentBrush", "#4FB996"), ("TintBrush", "#243D32"), ("HoverBrush", "#2B4337") })
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private void RenderDays()
    {
        var today = Dates.Today; _shownDate = today;
        var culture = CultureInfo.GetCultureInfo("ja-JP");
        RangeText.Text = today.ToString("M/d(ddd)", culture) + " — " + today.AddDays(6).ToString("M/d(ddd)", culture) + (_demo ? "  ·  デモ" : "");
        var views = Enumerable.Range(0, 7).Select(i =>
        {
            var date = today.AddDays(i);
            var day = _saved?.Plan.Days.FirstOrDefault(d => d.Date == Dates.Iso(date));
            var items = (day?.Items ?? []).Select(item => new ItemView(item)).ToList();
            return new DayView(i == 0 ? "今日" : i == 1 ? "明日" : date.ToString("M/d", culture),
                i < 2 ? date.ToString("M/d(ddd)", culture) : date.ToString("dddd", culture),
                items.Count > 0 ? items.Count + "件" : "", items,
                day is null ? "未整理 — 更新すると表示されます" : "予定されているタスクはありません");
        }).ToList();
        DaysControl.ItemsSource = views;
        ReviewControl.ItemsSource = (_saved?.Plan.Review ?? []).Select(i => new ItemView(i));
        ReviewExpander.Visibility = (_saved?.Plan.Review.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewExpander.Header = "確認したいこと  " + (_saved?.Plan.Review.Count ?? 0) + "件";
        UpdatedText.Text = _saved is null ? "表示中はAIを呼び出しません" : "最終整理 " + _saved.GeneratedAt.ToString("M/d HH:mm", culture) + "  ·  Codex";
        PinButton.Content = Topmost ? "固定中" : "固定";
        if (_demo) { NoticeText.Text = "これは架空のタスクを使った画面サンプルです。"; NoticeBox.Visibility = Visibility.Visible; RefreshButton.IsEnabled = false; }
        else UpdateNotice();
    }

    private void UpdateNotice()
    {
        if (_saved is null) { NoticeText.Text = "「更新する」を押すと、ホームから7日分の一覧を作ります。"; NoticeBox.Visibility = Visibility.Visible; return; }
        var messages = new List<string>();
        if (!string.Equals(_saved.SourcePath, _settings.SourcePath, StringComparison.OrdinalIgnoreCase)) messages.Add("参照ファイルが変わっています。更新してください。");
        else if (_currentHash is not null && _saved.SourceHash != _currentHash) messages.Add("ホームが更新されています。一覧への反映には更新が必要です。");
        if (DateOnly.FromDateTime(_saved.GeneratedAt.DateTime) != Dates.Today) messages.Add("日付が変わりました。最新の7日分に更新できます。");
        NoticeText.Text = string.Join("\n", messages);
        NoticeBox.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CheckSource()
    {
        if (_demo) { StatusText.Text = "デモ表示 · AIを呼び出していません"; return; }
        try
        {
            var source = HomeSource.Read(_settings.SourcePath);
            _currentHash = source.Hash; _lastHomeWrite = File.GetLastWriteTimeUtc(_settings.SourcePath);
            StatusText.Text = "ホームを参照 · 更新はボタンを押したときだけ";
            UpdateNotice();
        }
        catch (IOException) { StatusText.Text = "ホームを読めません。設定で参照ファイルを確認してください。"; }
        catch (UnauthorizedAccessException) { StatusText.Text = "ホームを読めません。設定で参照ファイルを確認してください。"; }
    }

    private void Poll()
    {
        if (_refresh is not null || _demo) return;
        if (_shownDate != Dates.Today) { RenderDays(); ApplyTheme(); }
        if (File.GetLastWriteTimeUtc(_settings.SourcePath) != _lastHomeWrite) CheckSource();
        var cacheWrite = File.GetLastWriteTimeUtc(_storage.CachePath);
        if (cacheWrite != _lastCacheWrite) { _lastCacheWrite = cacheWrite; _saved = _storage.LoadPlan(); RenderDays(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refresh is not null || _demo) return;
        _refresh = new CancellationTokenSource();
        RefreshButton.Visibility = Visibility.Collapsed; CancelButton.Visibility = Visibility.Visible;
        try
        {
            _saved = await new CodexRunner(_storage).RefreshAsync(_settings.SourcePath,
                new Progress<string>(s => StatusText.Text = s), _refresh.Token);
            _lastCacheWrite = File.GetLastWriteTimeUtc(_storage.CachePath);
            CheckSource(); RenderDays(); ListScroll.ScrollToTop();
            StatusText.Text = "7日分の一覧を更新しました";
        }
        catch (OperationCanceledException) { StatusText.Text = "更新を中止しました。前の一覧を保持しています。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _refresh.Dispose(); _refresh = null; RefreshButton.Visibility = Visibility.Visible; CancelButton.Visibility = Visibility.Collapsed; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _refresh?.Cancel();
    private void Pin_Click(object sender, RoutedEventArgs e) { Topmost = !Topmost; _settings.AlwaysOnTop = Topmost; _storage.SaveSettings(_settings); PinButton.Content = Topmost ? "固定中" : "固定"; }
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject obj)
            for (DependencyObject? p = obj; p is not null; p = VisualTreeHelper.GetParent(p)) if (p is Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => OpenHome();
    private void OpenHome()
    {
        if (_demo) return;
        try { Process.Start(new ProcessStartInfo(_settings.SourcePath) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "ホームを開けませんでした。設定で参照ファイルを確認してください。"; }
    }

    private void Task_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ItemView view }) return;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Text(view.Title, 20, true));
        panel.Children.Add(Text(view.Badge + (string.IsNullOrEmpty(view.Deadline) ? "" : "  ·  " + view.Deadline), 12));
        panel.Children.Add(Text(view.Reason, 14));
        panel.Children.Add(Text("根拠（ホームの記録）", 13, true));
        foreach (var source in (_saved?.Sources ?? []).Where(s => view.Item.SourceIds.Contains(s.Id)))
        { panel.Children.Add(Text(source.Heading + " · " + source.Line + "行目", 11)); panel.Children.Add(Text(source.Text, 12)); }
        var open = new Button { Content = "ホームを開く", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), IsEnabled = !_demo };
        open.Click += (_, _) => OpenHome(); panel.Children.Add(open);
        var details = new Window { Title = "タスクの詳細", Owner = this, Width = 530, Height = 550, MinWidth = 380, MinHeight = 350,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("CanvasBrush"), Topmost = Topmost,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, FontFamily = FontFamily };
        details.PreviewKeyDown += (_, key) => { if (key.Key == Key.Escape) details.Close(); };
        details.ShowDialog();
    }

    private TextBlock Text(string text, double size, bool bold = false) => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = (Brush)FindResource("TextBrush"), LineHeight = size * 1.55 };

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Text("表示と参照ファイル", 18, true));
        panel.Children.Add(Text("タスクを読むホーム.md", 12));
        var path = new TextBox { Text = _settings.SourcePath, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(path);
        var choose = new Button { Content = "参照ファイルを変更", HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += (_, _) => { var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown|*.md", CheckFileExists = true }; if (picker.ShowDialog() == true) path.Text = picker.FileName; };
        panel.Children.Add(choose);
        var top = new CheckBox { Content = "他のアプリの上に表示する", IsChecked = _settings.AlwaysOnTop, Margin = new Thickness(0, 20, 0, 12) };
        var start = new CheckBox { Content = "Windowsログイン時に起動する", IsChecked = _settings.StartWithWindows && !_demo, IsEnabled = !_demo, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(top); panel.Children.Add(start);
        panel.Children.Add(Text("更新ボタンを押したときだけ、既存のChatGPTログインでCodexを使います。通常の表示ではAIを呼び出しません。", 12));
        var reset = new Button { Content = "表示位置を右上へ戻す", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        reset.Click += (_, _) => { var area = SystemParameters.WorkArea; Left = area.Right - Width - 22; Top = area.Top + 28; EnsureVisible(); };
        panel.Children.Add(reset);
        var save = new Button { Content = "保存して閉じる", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(15, 8, 15, 8) };
        panel.Children.Add(save);
        var dialog = new Window { Title = "タスクパネルの設定", Owner = this, Width = 440, SizeToContent = SizeToContent.Height, MaxHeight = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("CanvasBrush"), Content = panel, Topmost = Topmost,
            FontFamily = FontFamily, ResizeMode = ResizeMode.NoResize };
        save.Click += (_, _) =>
        {
            _settings.SourcePath = path.Text; _settings.AlwaysOnTop = top.IsChecked == true; _settings.StartWithWindows = start.IsChecked == true;
            Topmost = _settings.AlwaysOnTop;
            if (!_demo) { SetStartup(_settings.StartWithWindows); _settings.StartWithWindows = File.Exists(StartupPath); }
            _storage.SaveSettings(_settings); CheckSource(); RenderDays(); dialog.Close();
        };
        dialog.PreviewKeyDown += (_, key) => { if (key.Key == Key.Escape) dialog.Close(); };
        dialog.ShowDialog();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("パネルを表示", null, (_, _) => Dispatcher.Invoke(() => { Show(); EnsureVisible(); Activate(); }));
        menu.Items.Add("ホームを開く", null, (_, _) => Dispatcher.Invoke(OpenHome));
        menu.Items.Add("終了", null, (_, _) => Dispatcher.Invoke(() => { _exit = true; Close(); }));
        _tray = new Forms.NotifyIcon { Text = "1週間のタスク", Icon = System.Drawing.SystemIcons.Information, Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { Show(); EnsureVisible(); Activate(); });
    }
    private static string StartupPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "WeeklyTasks.lnk");
    private void SetStartup(bool enabled)
    {
        try
        {
            if (!enabled) { if (File.Exists(StartupPath)) File.Delete(StartupPath); return; }
            if (Environment.ProcessPath is null || Path.GetFileName(Environment.ProcessPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(StartupPath)) return;
            Type type = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(StartupPath);
            shortcut.TargetPath = Environment.ProcessPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            shortcut.Description = "1週間のタスクを表示";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception) { StatusText.Text = "自動起動を設定できませんでした。手動で起動できます。"; }
    }
    private void EnsureVisible()
    {
        var area = SystemParameters.WorkArea;
        // Keep a reachable header even after monitor/DPI changes.
        if (Left + 80 < area.Left || Left > area.Right - 60 || Top + 45 < area.Top || Top > area.Bottom - 45)
        { Left = Math.Max(area.Left, area.Right - Width - 22); Top = area.Top + 28; }
    }
    private void SaveGeometry()
    {
        if (!_loaded || _demo || WindowState != WindowState.Normal) return;
        _settings.Left = Left; _settings.Top = Top; _settings.Width = ActualWidth; _settings.Height = ActualHeight;
        _storage.SaveSettings(_settings);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exit && !_demo) { e.Cancel = true; Hide(); return; }
        _refresh?.Cancel(); _timer.Stop(); _tray?.Dispose();
    }

    private void ExportImage(string path)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path); png.Save(file);
    }

    private static SavedPlan DemoPlan()
    {
        var today = Dates.Today;
        PlanItem Item(string title, string reason, string? deadline, string confidence, string kind = "task", string priority = "normal") => new()
        { Title = title, Reason = reason, Deadline = deadline, Confidence = confidence, Kind = kind, Priority = priority, SourceIds = ["demo"] };
        return new SavedPlan { GeneratedAt = Dates.Now, Model = "デモ", Sources = [new("demo", 1, "画面サンプル", "これは架空のタスクです。実際のホームやAIは使っていません。")], Plan = new AiPlan
        {
            Days = Enumerable.Range(0, 7).Select(i => new DayPlan { Date = Dates.Iso(today.AddDays(i)), Items = i switch
            {
                0 => [Item("資料の最終確認と提出", "今日が締切。提出前に添付と内容を確認する。", "今日 23:59", "confirmed", priority: "high"), Item("明日の打ち合わせを準備する", "議題を3つに絞って、短いメモを用意する。", null, "suggested")],
                1 => [Item("プロジェクトの打ち合わせ", "参加が決まっている予定。準備メモを手元に置く。", "14:00–15:00", "confirmed", "appointment")],
                3 => [Item("申込フォームを確認する", "週末の締切に間に合うよう、必要事項を先に確認する。", "週末まで", "suggested")],
                6 => [Item("1週間を振り返る", "終わったことと来週の優先事項を整理する。", null, "suggested")],
                _ => []
            } }).ToList(),
            Review = [Item("次回の日程を確認する", "日時がまだ確定していないため、一覧には割り当てていない。", null, "uncertain", "confirm")]
        } };
    }
}

public sealed record DayView(string Label, string DateLabel, string CountLabel, List<ItemView> Items, string EmptyText)
{ public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }

public sealed class ItemView(PlanItem item)
{
    public PlanItem Item => item;
    public string Title => item.Title;
    public string Reason => item.Reason;
    public string Deadline => item.Deadline ?? "";
    public string Badge => item.Confidence == "uncertain" ? "要確認" : item.Kind == "appointment" ? "予定" : item.Confidence == "suggested" ? "提案" : item.Priority == "high" ? "優先" : "確定";
    public Brush Accent => new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Confidence == "uncertain" ? "#B57921" : item.Priority == "high" ? "#BC5A3D" : item.Kind == "appointment" ? "#6A64AC" : item.Confidence == "suggested" ? "#54778B" : "#25856A"));
    public Brush BadgeBackground => new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.Confidence == "uncertain" ? "#FFF0D5" : item.Priority == "high" ? "#FAE8DF" : item.Kind == "appointment" ? "#EEEAF9" : item.Confidence == "suggested" ? "#E9F1F5" : "#E5F2EC"));
}
