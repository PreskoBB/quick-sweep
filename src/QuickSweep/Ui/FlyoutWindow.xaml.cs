using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using QuickSweep.Core;
using QuickSweep.Native;

namespace QuickSweep.Ui;

public sealed class DriveRow : Observable
{
    string _letter = "", _freeText = ""; double _used; Brush _fill = Palette.Accent, _textBrush = Palette.Muted;
    public string Letter { get => _letter; set => Set(ref _letter, value); }
    public string FreeText { get => _freeText; set => Set(ref _freeText, value); }
    public double Used { get => _used; set => Set(ref _used, value); }
    public Brush Fill { get => _fill; set => Set(ref _fill, value); }
    public Brush TextBrush { get => _textBrush; set => Set(ref _textBrush, value); }
}

public sealed class AppRow : Observable
{
    string _name = "", _size = "", _count = "", _commit = ""; ImageSource? _icon; bool _confirm, _canEnd;
    public AppGroup? Group;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string SizeText { get => _size; set => Set(ref _size, value); }
    public string CountText { get => _count; set => Set(ref _count, value); }
    public string CommitText { get => _commit; set => Set(ref _commit, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public bool Confirming { get => _confirm; set { if (Set(ref _confirm, value)) { Raise(nameof(NormalVis)); Raise(nameof(ConfirmVis)); Raise(nameof(ConfirmText)); } } }
    public bool CanEnd { get => _canEnd; set { if (Set(ref _canEnd, value)) Raise(nameof(EndVis)); } }
    public Visibility NormalVis => _confirm ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ConfirmVis => _confirm ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EndVis => _canEnd ? Visibility.Visible : Visibility.Hidden;
    public string ConfirmText => Group == null ? "" : $"End {Group.Count} process{(Group.Count == 1 ? "" : "es")} of {Group.Name}?";
}

public partial class FlyoutWindow : Window
{
    readonly SystemMonitor _mon;
    readonly Action<string?> _openMain;
    readonly ObservableCollection<AppRow> _apps = new();
    readonly ObservableCollection<DriveRow> _drives = new();
    DateTime _closedAt = DateTime.MinValue, _shownAt = DateTime.MinValue;
    bool _updatePending;
    DateTime _lastAppsRefresh = DateTime.MinValue;
    bool _refreshingApps;
    string _alertSig = "";
    bool _taskbarTop; double _topDip, _bottomDip;

    public FlyoutWindow(SystemMonitor mon, Action<string?> openMain)
    {
        InitializeComponent();
        _mon = mon; _openMain = openMain;
        AppList.ItemsSource = _apps;
        DriveList.ItemsSource = _drives;
        Activated += (_, _) => _activated = true;
        // Click-away by losing activation only counts once the window was really activated and the fade-in is over:
        // the shell hands the foreground back and forth around a tray click, and a window that never became foreground
        // must not close itself. Click-away is also detected by polling the mouse (below), which needs no activation.
        Deactivated += (_, _) => { if (IsVisible && _activated && (DateTime.Now - _shownAt).TotalMilliseconds > GraceMs) HideFlyout("deactivated"); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) HideFlyout("escape"); };
        _awayTimer.Tick += (_, _) => CheckClickAway();
        SourceInitialized += (_, _) => WindowEffects.Apply(this, true);
        // the list of apps can change the height: keep the flyout glued to the taskbar edge
        SizeChanged += (_, e) => { if (IsVisible && !_taskbarTop && _bottomDip > 0) Top = _bottomDip - e.NewSize.Height; };
    }

    public bool RecentlyClosed => (DateTime.Now - _closedAt).TotalMilliseconds < 350;

    const double GraceMs = 1200;
    bool _activated, _mouseWasDown;
    RECT? _iconRect;
    readonly DispatcherTimer _awayTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    /// <summary>Closes the flyout when a mouse button goes down outside it (works without foreground rights).</summary>
    void CheckClickAway()
    {
        if (!IsVisible) { _awayTimer.Stop(); return; }
        bool down = (Win.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win.GetAsyncKeyState(0x02) & 0x8000) != 0 || (Win.GetAsyncKeyState(0x04) & 0x8000) != 0;
        bool pressed = down && !_mouseWasDown;
        _mouseWasDown = down;
        if (!pressed || (DateTime.Now - _shownAt).TotalMilliseconds < 250) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!Win.GetCursorPos(out var p) || !Win.GetWindowRect(hwnd, out var r)) return;
        if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;
        if (_iconRect is { } ic && p.X >= ic.Left && p.X < ic.Right && p.Y >= ic.Top && p.Y < ic.Bottom) return; // the icon click toggles by itself
        HideFlyout("click outside");
    }

    public void HideFlyout(string reason = "toggle")
    {
        if (!IsVisible) return;
        if (Log.Trace) Log.Write($"trace: flyout hidden ({reason}) {(DateTime.Now - _shownAt).TotalMilliseconds:0} ms after show");
        _mon.Sampled -= OnSampled;
        _mon.PopFast();
        _awayTimer.Stop();
        _closedAt = DateTime.Now;
        Hide();
        foreach (var a in _apps) a.Confirming = false;
        Win.TrimSelf();
    }

    public void ShowNear(RECT? icon)
    {
        // position (physical px -> dips of the monitor that holds the tray icon)
        POINT c = icon is { } r ? new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 }
                                : (Win.GetCursorPos(out var p) ? p : new POINT());
        var mon = Win.MonitorFromPoint(c, 2);
        var mi = new Win.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win.MONITORINFO>() };
        Win.GetMonitorInfoW(mon, ref mi);
        double s = WindowEffects.ScaleForMonitor(mon);

        ApplyApps(_mon.LastGroups.Take(5).ToList());   // cached rows so the first frame already has its final height
        Render(_mon.Latest, true);
        Root.Measure(new Size(Width, double.PositiveInfinity));
        double h = Math.Ceiling(Root.DesiredSize.Height), w = Width;
        double wpx = w * s;
        double left = Math.Clamp(c.X - wpx / 2, mi.rcWork.Left + 8 * s, mi.rcWork.Right - wpx - 8 * s);
        _taskbarTop = icon is { } ir && ir.Top < (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
        _topDip = (mi.rcWork.Top + 8 * s) / s;
        _bottomDip = (mi.rcWork.Bottom - 8 * s) / s;
        Left = left / s; Top = _taskbarTop ? _topDip : _bottomDip - h;
        Slide.Y = _taskbarTop ? -10 : 10;
        Opacity = 0;

        _mon.Sampled += OnSampled;
        _mon.PushFast();
        _shownAt = DateTime.Now;
        _activated = false; _iconRect = icon;
        _mouseWasDown = (Win.GetAsyncKeyState(0x01) & 0x8000) != 0;
        Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        bool fg = Win.ForceForeground(hwnd);
        Activate();
        if (Log.Trace) Log.Write($"trace: flyout shown, foreground={fg}, elevated={Elevation.IsElevated}");
        _awayTimer.Start();
        var dur = TimeSpan.FromMilliseconds(130);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Slide.Y, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        RefreshApps();
    }

    void OnSampled(Telemetry t)
    {
        if (_updatePending) return;
        _updatePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _updatePending = false;
            if (!IsVisible) return;
            Render(t, false);
            if ((DateTime.Now - _lastAppsRefresh).TotalSeconds >= 3) RefreshApps();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    static void T(TextBlock t, string s) { if (t.Text != s) t.Text = s; }

    public void Render(Telemetry t, bool forceLayout)
    {
        // CPU
        if (t.CpuTemp is double ct)
        {
            T(CpuTemp, $"{ct:0}"); CpuTemp.Foreground = Palette.Text;
            CpuNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            T(CpuTemp, "--");
            CpuNote.Visibility = Visibility.Visible;
            T(CpuNote, t.CpuStatus.StartsWith("needs admin") || t.CpuStatus.StartsWith("PawnIO") ? "Sensor needs admin + driver (run install.ps1)" : "Temperature not available");
        }
        var hist = _mon.History(150);
        CpuSpark.Stroke = Palette.Heat(t.CpuTemp, 70, 80, 90);
        CpuSpark.SetData(hist.Where(x => x.CpuTemp != null).Select(x => x.CpuTemp!.Value).ToList());
        string cpuLine = $"Load {t.CpuLoad:0}%";
        if (t.CpuClock is double clk) cpuLine += $"  ·  {clk / 1000:0.0} GHz";
        if (t.CpuPower is double pw) cpuLine += $"  ·  {pw:0} W";
        T(CpuLine, cpuLine);
        CpuBar.Value = t.CpuLoad / 100; CpuBar.Fill = Palette.Accent;

        // GPU
        if (t.Gpu is { } g)
        {
            T(GpuName, ShortGpu(g.Name));
            T(GpuTemp, $"{g.TempC:0}");
            GpuSpark.Stroke = Palette.Heat(g.TempC, 65, 75, 83);
            GpuSpark.SetData(hist.Where(x => x.Gpu != null).Select(x => x.Gpu!.TempC).ToList());
            T(GpuLine, $"Load {g.Load:0}%" + (g.FanPct >= 0 ? $"  ·  Fan {g.FanPct}%" : ""));
            GpuBar.Value = g.Load / 100; GpuBar.Fill = Palette.Accent;
            T(VramLine, $"VRAM {Palette.Bytes(g.VramUsed)} of {Palette.Bytes(g.VramTotal)}");
            VramBar.Value = g.VramTotal == 0 ? 0 : (double)g.VramUsed / g.VramTotal;
            VramBar.Fill = VramBar.Value > 0.92 ? Palette.Warn : Palette.Accent;
        }
        else { T(GpuTemp, "--"); T(GpuLine, "No NVIDIA GPU found"); }

        // memory
        var m = t.Mem;
        T(RamLine, $"RAM  {Palette.Bytes(m.InUse)} of {Palette.Bytes(m.Total)} in use");
        T(RamPct, $"{m.UsedPct:0}%");
        RamBar.Value = m.UsedPct / 100; RamBar.Fill = m.UsedPct > 92 ? Palette.Bad : m.UsedPct > 82 ? Palette.Warn : Palette.Accent;
        T(CommitLine, $"Committed  {Palette.Bytes(m.Commit)} of {Palette.Bytes(m.CommitLimit)} limit");
        T(CommitPct, $"{m.CommitPct:0}%");
        CommitBar.Value = m.CommitPct / 100; CommitBar.Fill = m.CommitPct > 90 ? Palette.Bad : m.CommitPct > 75 ? Palette.Warn : Palette.Accent;

        // drives
        SyncDrives(t.Drives);
        T(UptimeText, "Up " + FormatUptime(t.Uptime));

        // game mode
        var mode = _mon.Mode;
        bool enabled = _mon.Settings.GameModeAuto;
        GameSwitch.IsChecked = enabled;
        var game = _mon.Watcher.Current;
        if (!enabled) T(GameLine, "Off: background work is never touched.");
        else if (game == null) T(GameLine, "Waiting for a game. It only acts if the PC is overloaded while you play and background work is part of it.");
        else if (mode.Active) T(GameLine, $"Playing {game.Name}: slowed {mode.LastNames} ({mode.LastReason}). Priority only; restored when the load drops or the game closes.");
        else T(GameLine, $"Playing {game.Name}: no overload, nothing touched.");

        // alerts
        var alerts = _mon.Alerts.Active;
        // rebuild the banners only when something changed: a click must not land on an item that was just replaced
        string sig = string.Join("\n", alerts.Select(a => $"{a.Key}|{a.Title}|{a.Detail}|{a.Level}|{a.Page}"));
        if (sig != _alertSig) { _alertSig = sig; AlertList.ItemsSource = alerts.Count == 0 ? null : alerts; }
        AlertList.Visibility = alerts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (alerts.Count == 0) { StatusPill.Background = Palette.Frozen("#223A2A"); StatusDot.Fill = Palette.Good; T(StatusText, "All good"); }
        else { StatusPill.Background = Palette.Frozen("#4A3A16"); StatusDot.Fill = Palette.Warn; T(StatusText, alerts.Count == 1 ? "1 issue" : $"{alerts.Count} issues"); }
    }

    static string ShortGpu(string n) => n.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");

    static string FormatUptime(TimeSpan u)
        => u.TotalDays >= 1 ? $"{(int)u.TotalDays} d {u.Hours} h" : $"{u.Hours} h {u.Minutes} min";

    void SyncDrives(DriveStat[] drives)
    {
        while (_drives.Count > drives.Length) _drives.RemoveAt(_drives.Count - 1);
        while (_drives.Count < drives.Length) _drives.Add(new DriveRow());
        for (int i = 0; i < drives.Length; i++)
        {
            var d = drives[i]; var r = _drives[i];
            r.Letter = d.Letter + ":";
            r.FreeText = d.Free >= 100L * 1073741824 ? $"{d.Free / 1073741824.0:0} GB free" : $"{d.Free / 1073741824.0:0.0} GB free";
            r.Used = d.Total == 0 ? 0 : 1.0 - (double)d.Free / d.Total;
            r.Fill = d.Low ? Palette.Bad : Palette.Accent;
            r.TextBrush = d.Low ? Palette.Bad : Palette.Muted;
        }
    }

    // ---- top memory apps -------------------------------------------------
    public void RefreshApps()
    {
        if (_refreshingApps) return;
        _refreshingApps = true; _lastAppsRefresh = DateTime.Now;
        Task.Run(() =>
        {
            try
            {
                var groups = _mon.RefreshGroups().Take(5).ToList();
                Dispatcher.BeginInvoke(() => ApplyApps(groups));
            }
            finally { _refreshingApps = false; }
        });
    }

    void ApplyApps(List<AppGroup> groups)
    {
        while (_apps.Count < groups.Count) _apps.Add(new AppRow());
        while (_apps.Count > groups.Count) _apps.RemoveAt(_apps.Count - 1);
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i]; var r = _apps[i];
            if (r.Confirming && r.Group?.Key != g.Key) r.Confirming = false;
            r.Group = g;
            r.Name = g.Name;
            r.SizeText = Palette.Bytes(g.PrivateBytes);
            r.CountText = g.Count == 1 ? "1 process" : $"{g.Count} processes";
            r.Icon = IconCache.Get(g.ExePath);
            r.CanEnd = !g.Protected;
        }
    }

    void OnEndClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRow r) { foreach (var o in _apps) o.Confirming = false; r.Confirming = true; }
    }
    void OnEndCancel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRow r) r.Confirming = false;
    }
    void OnEndConfirm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppRow r || r.Group == null) return;
        var g = r.Group;
        r.Confirming = false;
        Task.Run(() => { ProcessInsight.EndGroup(g); Thread.Sleep(600); }).ContinueWith(_ => Dispatcher.BeginInvoke(RefreshApps));
    }

    void OnGameSwitch(object sender, RoutedEventArgs e)
    {
        _mon.SetGameMode(GameSwitch.IsChecked == true);
        Render(_mon.Latest, false);
    }

    /// <summary>A click on an alert banner opens the page that explains it (Drive health for a drive alert).</summary>
    void OnAlertClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Alert a) return;
        HideFlyout();
        _openMain(a.Page);
        e.Handled = true;
    }

    void OnOpenMain(object sender, RoutedEventArgs e) { HideFlyout(); _openMain(null); }
    void OnOpenSessions(object sender, RoutedEventArgs e) { HideFlyout(); _openMain("sessions"); }
    void OnMemDetails(object sender, RoutedEventArgs e) { HideFlyout(); _openMain("memory"); }
}
