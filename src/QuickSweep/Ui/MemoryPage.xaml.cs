using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickSweep.Core;

namespace QuickSweep.Ui;

public sealed class LegendRow
{
    public Brush Dot { get; set; } = Palette.Accent;
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Size { get; set; } = "";
}

public sealed class PoolRow
{
    public string Tag { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Size { get; set; } = "";
}

public partial class MemoryPage : UserControl, IPage
{
    static readonly Brush CApps = Palette.Frozen("#60CDFF"), CKernel = Palette.Frozen("#B48CFF"), CComp = Palette.Frozen("#4DD0C4"),
        COther = Palette.Frozen("#8393B0"), CMod = Palette.Frozen("#FFA24D"), CStandby = Palette.Frozen("#5FB06E"), CFree = Palette.Frozen("#6A6B76");

    readonly SystemMonitor _mon;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly ObservableCollection<AppRow> _apps = new();
    bool _busy;

    public MemoryPage(SystemMonitor mon)
    {
        InitializeComponent();
        _mon = mon;
        AppList.ItemsSource = _apps;
        _timer.Tick += (_, _) => Refresh();
    }

    public void OnShown() { _mon.PushFast(); _timer.Start(); Refresh(); }
    public void OnHidden() { _timer.Stop(); _mon.PopFast(); }

    void Refresh()
    {
        if (_busy) return;
        _busy = true;
        Task.Run(() =>
        {
            try
            {
                var groups = _mon.RefreshGroups();
                var tags = Elevation.IsElevated ? PoolTags.ReadTop(6) : PoolTags.ReadTop(6);
                var mem = _mon.Latest.Mem;
                Dispatcher.BeginInvoke(() => Apply(mem, groups, tags));
            }
            finally { _busy = false; }
        });
    }

    void Apply(MemoryStats m, List<AppGroup> groups, List<PoolTag>? tags)
    {
        long apps = groups.Where(g => g.Name != "Memory Compression (Windows)").Sum(g => g.PrivateBytes);
        long kernel = m.KernelPool;
        long comp = m.Compressed;
        long other = Math.Max(0, m.InUse - apps - kernel - comp);

        PhysTotal.Text = $"{Palette.Bytes(m.Total)} installed";
        PhysBar.SetSegments(new (double, Brush)[]
        {
            (apps, CApps), (kernel, CKernel), (comp, CComp), (other, COther), (m.Modified, CMod), (m.Standby, CStandby), (m.Free, CFree)
        });
        Legend.ItemsSource = new List<LegendRow>
        {
            new() { Dot = CApps, Name = "Apps", Size = Palette.Bytes(apps), Desc = "The programs you run: private memory of all their processes. This is what Task Manager's Processes tab adds up." },
            new() { Dot = CKernel, Name = "Windows kernel", Size = Palette.Bytes(kernel), Desc = "Memory the Windows kernel and drivers keep for themselves (paged and non-paged pool). Task Manager does not list it. Normally 1 to 3 GB." },
            new() { Dot = CComp, Name = "Compressed", Size = Palette.Bytes(comp), Desc = "Idle app memory Windows squeezed instead of writing to disk. Already counted in use; it saves disk traffic." },
            new() { Dot = COther, Name = "Other in use", Size = Palette.Bytes(other), Desc = "Drivers, graphics buffers, shared libraries, page tables and file mappings." },
            new() { Dot = CMod, Name = "Modified", Size = Palette.Bytes(m.Modified), Desc = "Changed data waiting to be written to disk. It turns into cache a moment later." },
            new() { Dot = CStandby, Name = "Standby cache", Size = Palette.Bytes(m.Standby), Desc = "Recently used files and app data kept in RAM for speed. Windows hands it back instantly when an app needs memory, so a large number here is healthy, not a problem." },
            new() { Dot = CFree, Name = "Free", Size = Palette.Bytes(m.Free), Desc = "Completely unused. Windows keeps this small on purpose because empty RAM does nothing." },
        };

        // commit
        CommitText.Text = $"{Palette.Bytes(m.Commit)} of {Palette.Bytes(m.CommitLimit)} limit ({m.CommitPct:0}%)";
        CommitBar.Value = m.CommitPct / 100;
        CommitBar.Fill = m.CommitPct > 90 ? Palette.Bad : m.CommitPct > 75 ? Palette.Warn : Palette.Accent;
        (CommitVerdict.Text, CommitVerdict.Foreground) = m.CommitPct > 90 ? ("Critical: programs may start failing to allocate memory.", Palette.Bad)
            : m.CommitPct > 75 ? ("High: close something big or raise the page file before it gets worse.", Palette.Warn)
            : ("Healthy: plenty of room before the limit.", Palette.Good);

        // kernel pool
        double poolGb = kernel / 1073741824.0;
        PoolTotal.Text = Palette.Bytes(kernel);
        (PoolVerdict.Text, PoolVerdict.Foreground) = poolGb > 6 ? ($"Very high ({poolGb:0.0} GB). A restart clears it; avoid recursive scans of huge folders.", Palette.Bad)
            : poolGb > 3 ? ($"Elevated ({poolGb:0.0} GB). Probably a big file scan; it will not shrink on its own.", Palette.Warn)
            : ($"Normal ({poolGb:0.0} GB).", Palette.Good);
        PoolSplit.Text = $"Paged pool {Palette.Bytes(m.KernelPaged)} (can be swapped out)  ·  Non-paged pool {Palette.Bytes(m.KernelNonpaged)} (always in RAM)";
        if (tags is { Count: > 0 })
        {
            PoolList.ItemsSource = tags.Select(t => new PoolRow { Tag = t.Tag, Meaning = t.Meaning, Size = Palette.Bytes(t.Total) }).ToList();
            PoolTagsTitle.Visibility = Visibility.Visible; PoolTagsNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            PoolList.ItemsSource = null;
            PoolTagsTitle.Visibility = Visibility.Collapsed;
            PoolTagsNote.Visibility = Visibility.Visible;
            PoolTagsNote.Text = Elevation.IsElevated ? "Windows did not return the per-driver breakdown." : "The per-driver breakdown needs admin rights (it appears when Quick Sweep runs from its logon task).";
        }

        // apps
        var top = groups.Take(12).ToList();
        while (_apps.Count < top.Count) _apps.Add(new AppRow());
        while (_apps.Count > top.Count) _apps.RemoveAt(_apps.Count - 1);
        for (int i = 0; i < top.Count; i++)
        {
            var g = top[i]; var r = _apps[i];
            if (r.Confirming && r.Group?.Key != g.Key) r.Confirming = false;
            r.Group = g; r.Name = g.Name; r.SizeText = Palette.Bytes(g.PrivateBytes);
            r.CommitText = "commit " + Palette.Bytes(g.Commit);
            r.CountText = g.Count == 1 ? "1 process" : $"{g.Count} processes";
            r.Icon = IconCache.Get(g.ExePath); r.CanEnd = !g.Protected;
        }
    }

    void OnEndClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AppRow r) { foreach (var o in _apps) o.Confirming = false; r.Confirming = true; }
    }
    void OnEndCancel(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.Tag is AppRow r) r.Confirming = false; }
    void OnEndConfirm(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppRow r || r.Group == null) return;
        var g = r.Group; r.Confirming = false;
        Task.Run(() => { ProcessInsight.EndGroup(g); Thread.Sleep(600); }).ContinueWith(_ => Dispatcher.BeginInvoke(Refresh));
    }
}
