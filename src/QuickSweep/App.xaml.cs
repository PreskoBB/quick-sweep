using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickSweep.Core;
using QuickSweep.Native;
using QuickSweep.Ui;

namespace QuickSweep;

public partial class App : Application
{
    public const string TaskName = "QuickSweep";

    public static SystemMonitor Mon { get; private set; } = null!;
    public static App Instance => (App)Current;

    TrayIcon? _tray;
    FlyoutWindow? _flyout;
    MainWindow? _main;
    EventWaitHandle? _openEvent, _dumpEvent;
    int _lastCpu = int.MinValue, _lastGpu = int.MinValue;
    DateTime _lastToggle = DateTime.MinValue;
    bool _iconPending;

    protected override void OnStartup(StartupEventArgs e)
    {
        // --data=<folder> moves the state folder of a NON-elevated --dev run, before anything reads or writes settings.
        // An elevated process never honors it (or QUICKSWEEP_DATA): anything running as the user can pass an argument or set a variable.
        var dataDir = StartupArgs.DataFolder(e.Args, Elevation.IsTokenElevated, out var ignoredData);
        if (dataDir != null) Environment.SetEnvironmentVariable("QUICKSWEEP_DATA", dataDir);
        if (ignoredData != null) Paths.AddNote($"ignored --data={ignoredData}: only a non-elevated --dev run may move the state folder");
        // --shots --demo: neutral sample data instead of this PC's readings; never touches the real state folder
        if (e.Args.Contains("--demo") && e.Args.Any(a => a.StartsWith("--shots=")))
        {
            if (Elevation.IsTokenElevated) { Shutdown(5); return; }     // demo files must never land in the real, secured state folder
            Demo.On = true;
            Demo.Problem = e.Args.Any(a => a.StartsWith("--demo-problem")); Demo.Worse = e.Args.Contains("--demo-problem=worse");
            if (Environment.GetEnvironmentVariable("QUICKSWEEP_DATA") is not { Length: > 0 })
                Environment.SetEnvironmentVariable("QUICKSWEEP_DATA", Path.Combine(Path.GetTempPath(), "QuickSweep-demo-" + Guid.NewGuid().ToString("N")[..8]));
        }
        base.OnStartup(e);
        // numbers and dates the same way everywhere (1.5 GB, not 1,5 GB)
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        DispatcherUnhandledException += (_, ex) => { Log.Write("UI exception: " + ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("fatal: " + ex.ExceptionObject);

        var args = e.Args;
        // --dev runs a second, isolated instance (own single-instance event, never relaunches the installed task)
        bool dev = args.Contains("--dev");
        if (dev) SingleInstance.EventName = @"Local\QuickSweep.Open.Dev";
        string taskName = args.FirstOrDefault(a => a.StartsWith("--task="))?[7..] ?? TaskName;
        string? shotsDir = args.FirstOrDefault(a => a.StartsWith("--shots="))?[8..];
        string? dumpFile = args.FirstOrDefault(a => a.StartsWith("--dump="))?[7..];
        string? selfTest = args.FirstOrDefault(a => a.StartsWith("--selftest="))?[11..];
        string? cleanScan = args.FirstOrDefault(a => a.StartsWith("--clean-scan="))?[13..];
        string? importFile = SettingsImport.FileFromArgs(args, out bool importRequested);
        if (importRequested)
        {
            // install.ps1 -ImportSettings: merge a settings file into this state folder (elevated by the installer), then leave.
            // No tray, no window, no single-instance event; the exit code says what happened.
            StateFolder.MigrateLegacy();
            int code = SettingsImport.Run(importFile, () => { var s = AppSettings.TryLoadForImport(out var why); return (s, why); }, s => s.Save(), out var msg);
            Log.Write(msg);
            Shutdown(code);
            return;
        }
        string? jobFile = args.FirstOrDefault(a => a.StartsWith("--delete-job="))?[13..].Trim('"');
        if (jobFile != null)
        {
            // The cleaner's helper: the elevated instance starts this with the user's own token to delete in places a normal user can
            // write. It exists to run WITHOUT elevation, so an elevated copy refuses.
            if (Elevation.IsTokenElevated) { Shutdown(QuickSweep.Core.Cleaner.DeleteHelper.WrongToken); return; }
            string? jobOut = args.FirstOrDefault(a => a.StartsWith("--job-out="))?[10..].Trim('"');
            Shutdown(QuickSweep.Core.Cleaner.DeleteHelper.Run(jobFile, jobOut));
            return;
        }
        if (cleanScan != null)
        {
            // dev: dry-run measurement of every cleaner category on real data (read-only)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cfg = AppSettings.Load();
            var res = QuickSweep.Core.Cleaner.CleanerEngine.ScanAll(new QuickSweep.Core.Cleaner.CleanContext(cfg), null, CancellationToken.None);
            var lines = res.Select(r => new
            {
                r.Category.Id, GB = Math.Round(r.TotalBytes / 1073741824.0, 2), Items = r.Items.Count, r.Error,
                Denied = r.Items.Count(i => i.Denied != null), Recent = r.Items.Count(i => i.Recent),
                Top = r.Items.Take(4).Select(i => $"{i.Bytes / 1048576} MB  {i.Display}  {(i.Denied != null ? "[DENIED: " + i.Denied + "]" : "")}{(i.Recent ? " [recent]" : "")}")
            });
            File.WriteAllText(cleanScan, $"elapsed {sw.Elapsed.TotalSeconds:0.0}s\n" + System.Text.Json.JsonSerializer.Serialize(lines, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Shutdown();
            return;
        }
        if (selfTest != null)
        {
            File.WriteAllText(selfTest, SelfTest.Run());
            Shutdown();
            return;
        }
        bool open = args.Any(a => a == "--open" || a.StartsWith("--open="));
        string? page = args.FirstOrDefault(a => a.StartsWith("--open="))?[7..];

        if (args.FirstOrDefault(a => a.StartsWith("--cmd="))?[6..] is { } cmd && SingleInstance.TryOpen(out _) is { } target)
        {
            // dev: toggle the flyout of a running instance (the only command besides "open a page"), then leave
            if (cmd == "toggle-flyout") SignalOpen(target, OpenRequest.FlyoutKey);
            target.Dispose();
            Shutdown();
            return;
        }
        if (dumpFile != null && SingleInstance.TryOpen(out _, SingleInstance.DumpEventName) is { } dumpEvent)
        {
            // The running instance never takes a path from us: it writes its readings to dump.json in its own state
            // folder when this event is set, and this (unelevated) client copies the file to where it was asked to.
            bool ok = DumpExchange.Request(dumpEvent, dumpFile);
            dumpEvent.Dispose();
            if (!ok) Log.Write("--dump: the running instance did not answer within 15 s");
            Shutdown();
            return;
        }
        if (shotsDir == null && dumpFile == null)
        {
            // 1. An instance is already running (elevated or not): it opens its main window, this launcher leaves.
            var existing = SingleInstance.TryOpen(out bool inaccessible);
            if (existing != null)
            {
                Win.AllowSetForegroundWindow(-1);        // this launcher was started by the user and may hand the foreground over
                SignalOpen(existing, page);
                existing.Dispose();
                Shutdown();
                return;
            }
            if (inaccessible)
            {
                // an instance exists but this process may not signal it: never start a second tray icon
                Log.Write("launcher: a Quick Sweep instance is running but its open-event is not accessible; leaving without starting another");
                Shutdown();
                return;
            }
            // 2. Nothing running and this launcher is not elevated: ask the elevated logon task to start (no UAC prompt,
            //    no console) and tell it to open the main window. Never run a non-elevated copy next to it.
            if (!Elevation.IsElevated && !args.Contains("--no-elevate") && (!dev || taskName != TaskName) && TaskExists(taskName))
            {
                OpenRequest.Write(page);                        // read by the instance at start-up (allowlisted keys only)
                Win.AllowSetForegroundWindow(-1);
                int rc = RunHidden("schtasks.exe", $"/run /tn {taskName}");
                Log.Write($"launcher: no instance running; started task '{taskName}' (exit code {rc}) and asked it to open the main window");
                Shutdown();
                return;
            }
            // 3. This process becomes the instance (elevated by the task, or a plain run when Quick Sweep is not installed).
            if (!CreateOpenEvent())
            {
                // lost a race with another instance that just started: hand over and leave
                if (SingleInstance.TryOpen(out _) is { } other) { SignalOpen(other, page); other.Dispose(); }
                Shutdown();
                return;
            }
        }

        StateFolder.MigrateLegacy();      // once: copy the earlier version's files into the elevated state folder
        var settings = AppSettings.Load();
        Mon = new SystemMonitor(settings);

        if (shotsDir == null)   // rendering screenshots must not put an extra icon in the tray
        {
            _tray = new TrayIcon();
            _tray.LeftClick += ToggleFlyout;
            _tray.RightClick += ShowMenu;
            _tray.Update(TrayIconRenderer.Make(null, null), "Quick Sweep starting...");
        }
        Mon.Toast += (title, text, warn) =>
        {
            Log.Write($"notification: {title}: {text}");
            Dispatcher.BeginInvoke(() => _tray?.Balloon(title, text, warn));
        };
        Mon.Sampled += OnSampled;
        if (Demo.On) { Demo.SeedSessions(); Mon.StartDemo(); }
        else Mon.Start();

        if (shotsDir != null) { _ = RunShots(shotsDir); return; }
        if (dumpFile != null) { _ = RunDump(dumpFile); return; }

        // the shell registers the icon asynchronously: promote it a few seconds later (first run only)
        var promo = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        promo.Tick += (_, _) => { promo.Stop(); TrayPromotion.TryPromoteOnce(settings); };
        promo.Start();

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _flyout = new FlyoutWindow(Mon, OpenMain);
            Win.TrimSelf();
            if (args.Contains("--show-flyout")) ToggleFlyout();
            if (open) OpenMain(page);
            else if (OpenRequest.Take(startup: true) is { } pending) HandleRequest(pending);   // started by a launcher through the task
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Mon?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        base.OnExit(e);
    }

    // ---- single instance -------------------------------------------------
    static void SignalOpen(EventWaitHandle ev, string? key)
    {
        OpenRequest.Write(key);     // only an allowlisted key ever reaches the file
        ev.Set();
    }

    void HandleRequest((RequestKind kind, string? page) req)
    {
        switch (req.kind)
        {
            case RequestKind.ToggleFlyout: Dispatcher.BeginInvoke(ToggleFlyout); break;
            case RequestKind.Page: Dispatcher.BeginInvoke(() => OpenMain(req.page)); break;
            case RequestKind.OpenMain: Dispatcher.BeginInvoke(() => OpenMain(null)); break;
        }
    }

    /// <summary>Takes the single-instance event. False when another instance already owns it.</summary>
    bool CreateOpenEvent()
    {
        _openEvent = SingleInstance.Create(out bool createdNew);
        if (_openEvent == null) { Log.Write("single-instance event could not be created; running without hand-off"); return true; }
        if (!createdNew) { _openEvent.Dispose(); _openEvent = null; return false; }
        var evt = _openEvent;
        var t = new Thread(() =>
        {
            // Anything that can reach this event can write the inbox file, so the only thing it can ever ask for is
            // an allowlisted key (open the main window, toggle the flyout, show one known page). No commands, no paths.
            while (evt.WaitOne())
            {
                var req = OpenRequest.Take(startup: false);
                if (req is { } r) HandleRequest(r);
            }
        }) { IsBackground = true, Name = "QuickSweep.OpenEvent" };
        t.Start();

        // second event, no payload: "write your readings to dump.json in your own state folder"
        _dumpEvent = SingleInstance.Create(out bool dumpNew, name: SingleInstance.DumpEventName);
        if (_dumpEvent != null && dumpNew)
        {
            var dev = _dumpEvent;
            new Thread(() =>
            {
                while (dev.WaitOne())
                    Dispatcher.BeginInvoke(() => { try { Mon.RefreshGroups(); WriteDump(); } catch (Exception ex) { Log.Write("dump failed: " + ex.Message); } });
            }) { IsBackground = true, Name = "QuickSweep.DumpEvent" }.Start();
        }
        return true;
    }

    static bool TaskExists(string name) => RunHidden("schtasks.exe", $"/query /tn {name}") == 0;

    public static int RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }

    // ---- tray ---------------------------------------------------------------
    void OnSampled(Telemetry t)
    {
        int c = t.CpuTemp is double ct ? (int)Math.Round(ct) : -1;
        int g = t.Gpu is { } gp ? (int)Math.Round(gp.TempC) : -1;
        if (c == _lastCpu && g == _lastGpu && !_iconPending) return;
        _lastCpu = c; _lastGpu = g;
        string tip = $"CPU {(c >= 0 ? c + " °C" : "n/a")}  ·  GPU {(g >= 0 ? g + " °C" : "n/a")}  ·  RAM {t.Mem.UsedPct:0}%";
        if (Mon.Mode.Active) tip += "  ·  background work slowed";
        Dispatcher.BeginInvoke(() =>
        {
            if (_tray == null) return;
            _tray.Update(TrayIconRenderer.Make(c >= 0 ? c : null, g >= 0 ? g : null), tip);
        }, DispatcherPriority.Background);
    }

    void ToggleFlyout()
    {
        _flyout ??= new FlyoutWindow(Mon, OpenMain);
        if (Log.Trace) Log.Write($"trace: toggle flyout (visible={_flyout.IsVisible}, recentlyClosed={_flyout.RecentlyClosed})");
        // belt and braces against duplicate tray messages: one toggle per 300 ms
        if ((DateTime.Now - _lastToggle).TotalMilliseconds < 300) return;
        _lastToggle = DateTime.Now;
        if (_flyout.IsVisible) { _flyout.HideFlyout(); return; }
        if (_flyout.RecentlyClosed) return;   // the click that closed it by deactivation
        _flyout.ShowNear(_tray?.GetRect());
    }

    void ShowMenu()
    {
        var items = new List<(int, string, bool, bool)>
        {
            (1, "Open Quick Sweep", false, false),
            (2, "Sessions", false, false),
            (0, "", false, true),
            (3, "Game Mode: slow background work only when needed", Mon.Settings.GameModeAuto, false),
            (0, "", false, true),
            (9, "Exit", false, false),
        };
        int cmd = _tray!.ShowMenu(items);
        switch (cmd)
        {
            case 1: OpenMain(null); break;
            case 2: OpenMain("sessions"); break;
            case 3: Mon.ToggleGameMode(); break;
            case 9: Shutdown(); break;
        }
    }

    public void OpenMain(string? page)
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => { _main = null; Win.TrimSelf(); };
        }
        _main.Navigate(page ?? _main.PageKeys.First());
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        // an elevated instance answering a launcher must still come to the front (foreground lock)
        Win.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_main).Handle);
        _main.Activate();
        Log.Write($"main window opened (page {page ?? "default"})");
    }

    // ---- diagnostics (dev) -----------------------------------------------
    async Task RunDump(string file)
    {
        await Task.Delay(8000);
        Mon.RefreshGroups();
        File.WriteAllText(file, BuildDump());      // this instance was started by the user with this explicit path
        Shutdown();
    }

    /// <summary>Answer to a --dump client: always the same file in this instance's own state folder, never a caller's path.</summary>
    void WriteDump()
    {
        if (!StateIo.WriteText("dump.json", BuildDump())) Log.Write("dump not written: " + StateIo.LastWhy);
    }

    string BuildDump()
    {
        var t = Mon.Latest;
        var groups = Mon.LastGroups;
        var rect = _tray?.GetRect();
        var o = new
        {
            TrayRect = rect is { } r ? $"{r.Left},{r.Top} {r.Width}x{r.Height}" : "none",
            t.Time, t.CpuTemp, t.CpuLoad, t.CpuClock, t.CpuPower, t.CpuStatus,
            Gpu = t.Gpu, Mem = t.Mem, t.Drives, UptimeHours = t.Uptime.TotalHours,
            Health = Mon.LastHealth.Select(h => new { h.Number, h.Model, h.Letters, h.IsSsd, h.Bus, State = h.State.ToString(), h.TempC, h.LifeLeftPct, h.Reallocated, h.Pending, h.Uncorrectable, h.PowerOnHours, h.Error, h.Findings }),
            Alerts = Mon.Alerts.Active.Select(a => a.Title),
            PingRouterAddress = Mon.Ping.RouterAddress,
            Elevated = Elevation.IsElevated, SensorName = Mon.Cpu.SensorName, CpuName = Mon.Cpu.CpuName,
            StateFolder = Paths.Data, StateFolderRefused = Paths.Refused,
            Top = groups.Take(8).Select(g => new { g.Name, g.Count, MB = g.PrivateBytes / 1048576, g.Protected })
        };
        return System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
    }

    // ---- screenshots (dev) ------------------------------------------------
    async Task RunShots(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(Demo.On ? 400 : 7000); // let sensors and the process scan warm up
        Mon.RefreshGroups();
        await Task.Delay(Demo.On ? 300 : 1500);

        SaveIconPreview(Path.Combine(dir, "tray-icon.png"));
        if (Environment.GetCommandLineArgs().Contains("--gif")) await RenderFlyoutFrames(Path.Combine(dir, "gif-frames"));
        var fly = new FlyoutWindow(Mon, _ => { });
        fly.Left = -20000; fly.Top = 0; fly.ShowActivated = false;
        fly.Render(Mon.Latest, true);
        fly.Show();
        fly.RefreshApps();
        await Task.Delay(2500);
        var root = (FrameworkElement)fly.Content;
        await Task.Delay(400);
        Save(root, Path.Combine(dir, "flyout.png"));
        fly.Close();

        var main = new MainWindow { Left = -20000, Top = 0, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        main.Show();
        foreach (var key in main.PageKeys)
        {
            main.Navigate(key);
            await Task.Delay(1500);
            if (key == "disk" && main.Current is DiskPage dp)
            {
                await dp.DemoCleanAsync();
                await Task.Delay(800);
                main.Height = 1500; main.UpdateLayout();
                Save((FrameworkElement)main.Content, Path.Combine(dir, "disk.png"));
                await dp.DemoExploreAsync(Demo.On ? @"D:\" : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                await Task.Delay(800);
                main.Height = 900; main.UpdateLayout();
                Save((FrameworkElement)main.Content, Path.Combine(dir, "disk-explore.png"));
                main.Height = 720;
                continue;
            }
            if (key == "settings" && main.Current is SettingsPage sp)
            {
                // a long page: one picture of the top, one of the bottom
                main.Height = 1500; main.UpdateLayout();
                var sv = (System.Windows.Controls.ScrollViewer)sp.Content;
                sv.ScrollToVerticalOffset(0); await Task.Delay(300);
                Save((FrameworkElement)main.Content, Path.Combine(dir, "settings.png"));
                sv.ScrollToBottom(); await Task.Delay(300); main.UpdateLayout();
                Save((FrameworkElement)main.Content, Path.Combine(dir, "settings-lists.png"));
                main.Height = 720;
                continue;
            }
            if (key == "network" && Demo.On && main.Current is NetworkPage np) { np.DemoHover(); await Task.Delay(300); }   // hover readout, as if the pointer were on a chart
            Save((FrameworkElement)main.Content, Path.Combine(dir, $"{key}.png"));
        }
        main.Close();
        Shutdown();
    }

    /// <summary>Demo only: frames of the flyout opening above the tray icon, for the README animation (assembled with ffmpeg).</summary>
    async Task RenderFlyoutFrames(string dir)
    {
        Directory.CreateDirectory(dir);
        var fly = new FlyoutWindow(Mon, _ => { });
        fly.Left = -20000; fly.Top = 0; fly.ShowActivated = false;
        fly.Render(Mon.Latest, true);
        fly.Show();
        fly.RefreshApps();
        await Task.Delay(800);
        var root = (FrameworkElement)fly.Content;
        const double Pad = 22, Bar = 54;
        var rnd = new Random(3);
        double cpu = 55, gpu = 49, load = 22, gload = 10;
        int frame = 0;
        void Emit(double flyOpacity, double slide, bool pressed, int cpuT, int gpuT)
        {
            fly.Render(Demo.Reading(cpu, load, gpu, gload), true);
            root.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(root);
            int fw = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), fh = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
            var flyBmp = new RenderTargetBitmap(fw, fh, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            flyBmp.Render(root);
            double w = root.ActualWidth + Pad * 2, h = root.ActualHeight + Pad * 2 + Bar;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x24, 0x26, 0x33), Color.FromRgb(0x14, 0x15, 0x1C), 90), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(Palette.Frozen("#16161B"), null, new Rect(0, h - Bar, w, Bar));
                dc.DrawLine(new Pen(Palette.Frozen("#2A2A33"), 1), new Point(0, h - Bar), new Point(w, h - Bar));
                // the tray icon, at the right end of the taskbar, with a soft highlight while it is clicked
                double ix = w - 120, iy = h - Bar + 11;
                if (pressed) dc.DrawRoundedRectangle(Palette.Frozen("#33FFFFFF"), null, new Rect(ix - 8, iy - 6, 48, 44), 6, 6);
                var hIcon = TrayIconRenderer.Make(cpuT, gpuT, 32);
                var iconBmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                Win.DestroyIcon(hIcon);
                dc.DrawImage(iconBmp, new Rect(ix, iy, 32, 32));
                var clock = new FormattedText("20:42", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Palette.Frozen("#C8C8D0"), 1.0);
                dc.DrawText(clock, new Point(w - 62, h - Bar + 12));
                if (flyOpacity > 0)
                {
                    dc.PushOpacity(flyOpacity);
                    dc.DrawImage(flyBmp, new Rect(Pad, Pad + slide, root.ActualWidth, root.ActualHeight));
                    dc.Pop();
                }
            }
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, $"f{frame++:000}.png"));
            enc.Save(fs);
        }
        for (int i = 0; i < 5; i++) Emit(0, 0, i >= 3, (int)cpu, (int)gpu);                                     // idle, then the click
        for (int i = 1; i <= 6; i++) { double t = i / 6.0; double ease = 1 - Math.Pow(1 - t, 3); Emit(ease, 14 * (1 - ease), false, (int)cpu, (int)gpu); }   // fade and slide in
        for (int i = 0; i < 26; i++)                                                                              // live values
        {
            cpu = Math.Clamp(cpu + (rnd.NextDouble() - 0.45) * 3.2, 50, 66);
            gpu = Math.Clamp(gpu + (rnd.NextDouble() - 0.48) * 1.6, 45, 54);
            load = Math.Clamp(load + (rnd.NextDouble() - 0.5) * 16, 8, 60);
            gload = Math.Clamp(gload + (rnd.NextDouble() - 0.5) * 8, 3, 28);
            Emit(1, 0, false, (int)cpu, (int)gpu);
        }
        fly.Close();
    }

    /// <summary>Tray icon at 16/20/24/32 px for a few temperature pairs, magnified with nearest neighbour.</summary>
    static void SaveIconPreview(string path)
    {
        var pairs = new (double? c, double? g)[] { (48, 41), (68, 63), (78, 71), (88, 79), (93, 85), (null, 41) };
        int[] sizes = { 16, 20, 24, 32 };
        int scale = 8, pad = 8;
        int w = pairs.Length * (32 * scale + pad) + pad, h = sizes.Length * (32 * scale + pad) + pad;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Palette.Frozen("#1B1B1F"), null, new Rect(0, 0, w, h));
            for (int r = 0; r < sizes.Length; r++)
                for (int c = 0; c < pairs.Length; c++)
                {
                    var hIcon = TrayIconRenderer.Make(pairs[c].c, pairs[c].g, sizes[r]);
                    var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    Win.DestroyIcon(hIcon);
                    var img = new ImageBrush(bmp);
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                    double x = pad + c * (32 * scale + pad), y = pad + r * (32 * scale + pad);
                    dc.DrawRectangle(img, null, new Rect(x, y, sizes[r] * scale, sizes[r] * scale));
                }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void Save(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(el);
        int w = (int)Math.Ceiling(el.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(el.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
