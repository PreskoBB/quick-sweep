using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickSweep.Core;

namespace QuickSweep.Ui;

public sealed class DriveVm
{
    readonly DriveReport _d;
    public DriveVm(DriveReport d) { _d = d; }
    public string Title => string.IsNullOrWhiteSpace(_d.Model) ? $"Drive {_d.Number}" : _d.Model;
    public string Sub => string.Join("  ·  ", new[]
    {
        _d.Letters.Count > 0 ? string.Join(" ", _d.Letters) : null,
        string.IsNullOrEmpty(_d.Bus) ? null : $"{_d.Bus} {(_d.IsSsd ? "SSD" : "hard disk")}",
        _d.SizeBytes > 0 ? Palette.Bytes(_d.SizeBytes) : null,
        string.IsNullOrEmpty(_d.Firmware) ? null : "firmware " + _d.Firmware
    }.Where(s => s != null));
    public string Chip => _d.Degrading ? "Getting worse" : _d.State switch
    {
        HealthState.Good => "Healthy", HealthState.Warning => "Warning", HealthState.Critical => "Failing", _ => "Not available"
    };
    public Brush ChipFg => _d.State switch { HealthState.Good => Palette.Good, HealthState.Warning => Palette.Warn, HealthState.Critical => Palette.Bad, _ => Palette.Muted };
    public Brush ChipBg => _d.State switch
    {
        HealthState.Good => Palette.Frozen("#223A2A"), HealthState.Warning => Palette.Frozen("#4A3A16"), HealthState.Critical => Palette.Frozen("#4D2328"), _ => Palette.Frozen("#34353D")
    };
    public Visibility StatsVis => _d.Attrs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Temp => _d.TempC is double t ? $"{t:0} °C" : "n/a";
    public string Life => _d.LifeLeftPct is double l ? $"{l:0}%" : _d.IsSsd ? "n/a" : "(hard disk)";
    public string LifeSource => _d.LifeSource;
    public string Realloc => _d.Reallocated?.ToString("N0") ?? "n/a";
    public string Pending => $"{_d.Pending?.ToString("N0") ?? "n/a"} / {_d.Uncorrectable?.ToString("N0") ?? "n/a"}";
    public string PowerOn => _d.PowerOnHours is long h ? (h >= 48 ? $"{h / 24.0:N0} days" : $"{h} h") : "n/a";
    public string Written => _d.WrittenTb is double w ? $"{w:0.0} TB" : "n/a";
    public string Crc => _d.CrcErrors?.ToString("N0") ?? "n/a";
    public string Findings => string.Join("\n", _d.Findings);
    public Visibility FindingsVis => _d.Findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Error => _d.Error ?? "";
    public Visibility ErrorVis => string.IsNullOrEmpty(_d.Error) ? Visibility.Collapsed : Visibility.Visible;
    public List<SmartAttr> Attrs => _d.Attrs;
}

public partial class HealthPage : UserControl, IPage
{
    readonly SystemMonitor _mon;
    List<DriveReport> _last = new();
    bool _busy;

    public HealthPage(SystemMonitor mon) { InitializeComponent(); _mon = mon; }

    public void OnShown()
    {
        if (_last.Count == 0) Check();
    }
    public void OnHidden() { }

    void OnCheck(object sender, RoutedEventArgs e) => Check();

    void Check()
    {
        if (_busy) return;
        _busy = true; CheckButton.IsEnabled = false;
        StatusText.Text = "Reading SMART data (a sleeping hard disk may spin up)...";
        Task.Run(() =>
        {
            var reports = DriveHealth.ReadAll(includeHdd: true);
            DriveHealth.ApplyBaseline(reports);
            _mon.SetHealth(reports);
            Dispatcher.BeginInvoke(() =>
            {
                _last = reports;
                List.ItemsSource = reports.Select(r => new DriveVm(r)).ToList();
                bool noAdmin = reports.Any(r => r.Error == "needs admin rights");
                StatusText.Text = noAdmin ? "Reading SMART needs admin rights. It works when Quick Sweep runs from its logon task (install.ps1)."
                    : reports.Count == 0 ? "No drives found." : $"Checked {reports.Count} drive{(reports.Count == 1 ? "" : "s")} at {DateTime.Now:HH:mm}.";
                CheckButton.IsEnabled = true; _busy = false;
            });
        });
    }

    void OnAccept(object sender, RoutedEventArgs e)
    {
        DriveHealth.ResetBaseline(_last);
        Check();
    }
}
