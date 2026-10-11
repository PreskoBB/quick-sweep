using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickSweep.Core;

namespace QuickSweep.Ui;

public sealed class StartupRow : Observable
{
    public StartupEntry Entry { get; }
    bool _enabled;
    public StartupRow(StartupEntry e) { Entry = e; _enabled = e.Enabled; }
    public string Name => Entry.Name;
    public string Command => Entry.Command;
    public ImageSource? Icon => IconCache.Get(Entry.ExePath);
    public string Sub => string.IsNullOrEmpty(Entry.Publisher) ? Entry.Source : $"{Entry.Source}  ·  {Entry.Publisher}";
    public string RamText => Entry.ProcessCount == 0 ? "not running" : Palette.Bytes(Entry.RamBytes);
    public bool CanChange => Entry.CanChange;
    public string SwitchTip => Entry.CanChange ? (Enabled ? "Starts with Windows" : "Will not start with Windows") : "Changing this entry needs admin rights";
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Raise(nameof(Fade)); Raise(nameof(SwitchTip)); } } }
    public double Fade => _enabled ? 1.0 : 0.55;
}

public partial class StartupPage : UserControl, IPage
{
    readonly SystemMonitor _mon;

    public StartupPage(SystemMonitor mon) { InitializeComponent(); _mon = mon; }

    public void OnShown() => Load();
    public void OnHidden() { }

    void Load()
    {
        Task.Run(() =>
        {
            _mon.RefreshGroups();
            var entries = StartupManager.Load(_mon.LastProcs);
            Dispatcher.BeginInvoke(() => Apply(entries));
        });
    }

    void Apply(List<StartupEntry> entries)
    {
        var rows = entries.Select(e => new StartupRow(e)).ToList();
        List.ItemsSource = rows;
        UpdateSummary(rows);
        Footer.Text = Elevation.IsElevated ? "" : "Quick Sweep is not running as admin: entries under 'All users' are read-only until it runs from its logon task.";
    }

    void UpdateSummary(List<StartupRow> rows)
    {
        long on = rows.Where(r => r.Enabled).Sum(r => r.Entry.RamBytes);
        long off = rows.Where(r => !r.Enabled).Sum(r => r.Entry.RamBytes);
        SumTitle.Text = Palette.Bytes(on);
        int enabled = rows.Count(r => r.Enabled);
        SumLine.Text = $"RAM used right now by the {enabled} startup app{(enabled == 1 ? "" : "s")} that are switched on" +
                       (rows.Count - enabled > 0 ? $"  ·  {rows.Count - enabled} switched off" + (off > 0 ? $" (still running now: {Palette.Bytes(off)}, gone after the next sign-in)" : "") : "") + ".";
    }

    void OnToggle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StartupRow row) return;
        bool want = ((CheckBox)sender).IsChecked == true;
        if (StartupManager.SetEnabled(row.Entry, want)) row.Enabled = want;
        else { ((CheckBox)sender).IsChecked = row.Enabled; }
        if (List.ItemsSource is List<StartupRow> rows) UpdateSummary(rows);
    }
}
