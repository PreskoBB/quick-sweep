using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QuickSweep.Core;
using QuickSweep.Native;

namespace QuickSweep.Ui;

public partial class SettingsPage : UserControl, IPage
{
    readonly SystemMonitor _mon;
    bool _loading;

    public SettingsPage(SystemMonitor mon)
    {
        InitializeComponent();
        _mon = mon;
        LoadValues();
    }

    public void OnShown() { LoadValues(); }
    public void OnHidden() { }

    static string Lines(IEnumerable<string> l) => string.Join(Environment.NewLine, l);
    static List<string> Split(string s) =>
        s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    void LoadValues()
    {
        _loading = true;
        var s = _mon.Settings;
        SwGameAuto.IsChecked = s.GameModeAuto; SwNotify.IsChecked = s.NotifyAfterGame;
        SwGameNotify.IsChecked = s.NotifyGameMode; SwBelowNormal.IsChecked = s.GameUseBelowNormal;
        SwAlerts.IsChecked = s.AlertsEnabled; SwPing.IsChecked = s.PingEnabled;
        TbGCpu.Text = s.GameCpuPct.ToString(); TbGGpu.Text = s.GameGpuPct.ToString();
        TbGRam.Text = s.GameMinFreeRamGb.ToString("0.#"); TbGReady.Text = s.GameReadyPct.ToString();
        TbGSecs.Text = s.GameTriggerSeconds.ToString(); TbGRel.Text = s.GameReleaseSeconds.ToString(); TbGMin.Text = s.GameMinContributionPct.ToString();
        TbCpu.Text = s.CpuAlertC.ToString(); TbGpu.Text = s.GpuAlertC.ToString(); TbSpace.Text = s.LowSpaceGb.ToString(); TbPool.Text = s.PoolAlertGb.ToString("0.#"); TbPoolRam.Text = s.PoolPressureFreeRamGb.ToString("0.#"); TbPoolCommit.Text = s.PoolPressureCommitPct.ToString();
        TbGameFolders.Text = Lines(s.ExtraGameFolders);
        TbBackground.Text = Lines(s.BackgroundProcesses); TbRuntimes.Text = Lines(s.ScriptRuntimes); TbAgents.Text = Lines(s.AgentApps);
        TbScan.Text = Lines(s.ExtraScanFolders.Select(f => f.ToString())); TbProtected.Text = Lines(s.ProtectedFolders);
        UpdateProtectedWarnings(s.ProtectedFolders);
        BuiltInProtected.Text = string.Join(Environment.NewLine, QuickSweep.Core.Cleaner.PathGuard.BuiltInProtection.Select(x => "\u2022  " + x));
        var cpu = _mon.Cpu;
        if (Demo.On)
            StatusBlock.Text = $"Version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}\nAdmin rights: yes\nCPU sensor: ok (CPU Package)\nPawnIO driver: installed\n" +
                               "GPU: RTX 3060 (NVIDIA, 12 GB)\nGPU temperature: NVML\nGPU usage: NVML\nGPU memory: NVML\nRouter for ping: found\nState folder: C:\\ProgramData\\QuickSweep";
        else
        StatusBlock.Text =
            $"Version {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}\n" +
            $"Admin rights: {(Elevation.IsElevated ? "yes" : "no")}\n" +
            $"CPU sensor: {cpu.Status}{(string.IsNullOrEmpty(cpu.SensorName) ? "" : " (" + cpu.SensorName + ")")}\n" +
            $"PawnIO driver: {(CpuSensor.PawnIoInstalled() ? "installed" : "not installed")}\n" +
            _mon.GpuMon.StatusText() + "\n" +
            $"Router for ping: {(string.IsNullOrEmpty(_mon.Ping.RouterAddress) ? "not found" : _mon.Ping.RouterAddress)}\n" +
            $"State folder: {Paths.Data}" + (Paths.Refused != null ? $"\nSTATE FOLDER REFUSED, settings and logs are off: {Paths.Refused}" : "");
        _loading = false;
    }

    void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _mon.Settings;
        s.GameModeAuto = SwGameAuto.IsChecked == true; s.NotifyAfterGame = SwNotify.IsChecked == true;
        s.NotifyGameMode = SwGameNotify.IsChecked == true; s.GameUseBelowNormal = SwBelowNormal.IsChecked == true;
        s.AlertsEnabled = SwAlerts.IsChecked == true; s.PingEnabled = SwPing.IsChecked == true;
        if (int.TryParse(TbGCpu.Text, out int gc) && gc is >= 40 and <= 100) s.GameCpuPct = gc;
        if (int.TryParse(TbGGpu.Text, out int gg) && gg is >= 40 and <= 100) s.GameGpuPct = gg;
        if (double.TryParse(TbGRam.Text, out double gr) && gr is >= 0.2 and <= 16) s.GameMinFreeRamGb = gr;
        if (int.TryParse(TbGReady.Text, out int gy) && gy is >= 0 and <= 100) s.GameReadyPct = gy;
        if (int.TryParse(TbGSecs.Text, out int gs) && gs is >= 3 and <= 300) s.GameTriggerSeconds = gs;
        if (int.TryParse(TbGRel.Text, out int gl) && gl is >= 10 and <= 1800) s.GameReleaseSeconds = gl;
        if (int.TryParse(TbGMin.Text, out int gm) && gm is >= 1 and <= 90) s.GameMinContributionPct = gm;
        _mon.Ping.Enabled = s.PingEnabled;
        if (int.TryParse(TbCpu.Text, out int c) && c is >= 50 and <= 110) s.CpuAlertC = c;
        if (int.TryParse(TbGpu.Text, out int g) && g is >= 50 and <= 110) s.GpuAlertC = g;
        if (int.TryParse(TbSpace.Text, out int sp) && sp is >= 1 and <= 500) s.LowSpaceGb = sp;
        if (double.TryParse(TbPool.Text, out double pg) && pg is >= 1 and <= 64) s.PoolAlertGb = pg;
        if (double.TryParse(TbPoolRam.Text, out double pr) && pr is >= 0.2 and <= 64) s.PoolPressureFreeRamGb = pr;
        if (int.TryParse(TbPoolCommit.Text, out int pc) && pc is >= 50 and <= 100) s.PoolPressureCommitPct = pc;
        s.ExtraGameFolders = Split(TbGameFolders.Text);
        s.BackgroundProcesses = Split(TbBackground.Text).Select(StripExe).ToList();
        s.ScriptRuntimes = Split(TbRuntimes.Text).Select(StripExe).ToList();
        s.AgentApps = Split(TbAgents.Text).Select(StripExe).ToList();
        s.ExtraScanFolders = Split(TbScan.Text).Select(ScanFolder.Parse).OfType<ScanFolder>().ToList();
        s.ProtectedFolders = Split(TbProtected.Text);
        s.Save();
        UpdateProtectedWarnings(s.ProtectedFolders);
    }

    int _warnVersion;

    /// <summary>Says, under the protected-folders box, which lines protect nothing (relative paths, unknown variables...) or match no folder right now.</summary>
    void UpdateProtectedWarnings(IReadOnlyList<string> lines)
    {
        int version = ++_warnVersion;
        if (lines.Count == 0) { ProtectedWarn.Visibility = Visibility.Collapsed; return; }
        var copy = lines.ToList();
        Task.Run(() => copy.Select(l => (line: l, check: QuickSweep.Core.Cleaner.PathGuard.CheckUserPattern(l)))
                .Where(x => x.check.Error != null || x.check.Warning != null)
                .Select(x => x.check.Error != null ? $"Ignored, it protects nothing: {x.line}\n      {x.check.Error}" : $"Note: {x.line}\n      {x.check.Warning}")
                .ToList())
            .ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (version != _warnVersion || t.IsFaulted) return;
                ProtectedWarn.Text = string.Join("\n", t.Result);
                ProtectedWarn.Foreground = Palette.Warn;
                ProtectedWarn.Visibility = t.Result.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }));
    }

    static string StripExe(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

    void OnResetBackground(object sender, RoutedEventArgs e)
    {
        var s = _mon.Settings;
        s.BackgroundProcesses = new(AppSettings.DefaultBackgroundProcesses);
        s.ScriptRuntimes = new(AppSettings.DefaultScriptRuntimes);
        s.AgentApps = new(AppSettings.DefaultAgentApps);
        s.Save();
        LoadValues();
    }

    void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export Quick Sweep settings", FileName = "quicksweep-settings.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, _mon.Settings.ToJson()); ImportResult.Text = "Settings exported."; }
        catch (Exception ex) { ImportResult.Text = "Could not write the file: " + ex.Message; }
    }

    /// <summary>Reads a settings file the user picked and overlays the keys it contains. Only values are taken (numbers, switches,
    /// lists); nothing in the file is ever run.</summary>
    void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import Quick Sweep settings", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var fi = new FileInfo(dlg.FileName);
            if (fi.Length > 256 * 1024) { ImportResult.Text = "That file is too large to be a settings file."; return; }
            int n = AppSettings.Import(_mon.Settings, File.ReadAllText(dlg.FileName), out var merged);
            if (n < 0) { ImportResult.Text = "That file is not a Quick Sweep settings file."; return; }
            _mon.Settings.CopyFrom(merged);
            _mon.Settings.Save();
            _mon.Ping.Enabled = _mon.Settings.PingEnabled;
            LoadValues();
            ImportResult.Text = $"Imported {n} setting{(n == 1 ? "" : "s")} from the file. Keys that were not in the file kept their value.";
        }
        catch (Exception ex) { ImportResult.Text = "Could not import: " + ex.Message; }
    }

    void OnOpenData(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.Data}\"") { UseShellExecute = false, CreateNoWindow = true });
}
