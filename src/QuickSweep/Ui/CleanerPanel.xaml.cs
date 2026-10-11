using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickSweep.Core;
using QuickSweep.Core.Cleaner;

namespace QuickSweep.Ui;

public sealed class ItemVm : INotifyPropertyChanged
{
    public CleanItem Item { get; }
    public ItemVm(CleanItem i) { Item = i; i.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
    public bool Selected { get => Item.Selected; set => Item.Selected = value; }
    public bool Selectable => Item.Selectable;
    public string Display => Item.Display;
    public string PathTip => Item.Path;
    public string SizeText => Palette.Bytes(Item.Bytes);
    public string Flags => Item.Denied != null ? "protected" : Item.IsLink ? "link" : Item.Recent ? "changed < 7 days ago" : Item.LinksInside > 0 ? $"{Item.LinksInside} link(s) skipped" : "";
    public Brush FlagBrush => Item.Denied != null ? Palette.Bad : Item.Recent || Item.IsLink ? Palette.Warn : Palette.Muted;
    public string FlagTip => Item.Denied ?? (Item.IsLink ? "A junction or symlink: only the link is removed, never what it points to." : Item.Recent ? "Modified in the last 7 days: not ticked automatically." : "Junctions and symlinks inside are never followed.");
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CategoryVm : INotifyPropertyChanged
{
    public CategoryResult Result { get; }
    public CleanCategory Cat => Result.Category;
    public List<ItemVm> Shown { get; }
    bool _expanded;
    public CategoryVm(CategoryResult r)
    {
        Result = r;
        Shown = r.Items.Take(40).Select(i => new ItemVm(i)).ToList();
        foreach (var i in r.Items) i.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllSelected)));
    }
    public string Title => Cat.Title;
    public string Sub => Result.Error != null ? "could not be scanned: " + Result.Error
        : Cat.ManagedByWindows ? $"{Result.Items.Count} location(s)" : $"{Result.Items.Count} item{(Result.Items.Count == 1 ? "" : "s")}" + (Result.Items.Any(i => i.Denied != null) ? $"  ·  {Result.Items.Count(i => i.Denied != null)} protected" : "");
    public string TotalText => Palette.Bytes(Result.TotalBytes);
    public string Badge => Cat.ManagedByWindows ? "Windows tool" : Cat.Mode == DeleteMode.Recycle ? "Recycle Bin" : "Delete";
    public Brush BadgeBg => Cat.ManagedByWindows ? Palette.Frozen("#2E3A52") : Cat.Mode == DeleteMode.Recycle ? Palette.Frozen("#2B3F2D") : Palette.Frozen("#3F3A2B");
    public Brush BadgeFg => Cat.ManagedByWindows ? Palette.Accent : Cat.Mode == DeleteMode.Recycle ? Palette.Good : Palette.Warn;
    public string What => Cat.What;
    public string WhySafe => Cat.WhySafe;
    public string Regen => Cat.Regen;
    public string DeleteHow => Cat.ManagedByWindows ? "Quick Sweep never deletes these itself."
        : Cat.Mode == DeleteMode.Recycle ? "Goes to the Recycle Bin (you can take it back)." : "Deleted for good (a cache: the Recycle Bin would only move it around on the same drive). Locked files are skipped.";
    public bool CanSelect => !Cat.ManagedByWindows && Result.Items.Any(i => i.Selectable && !i.Recent);
    public bool AllSelected
    {
        get { var s = Result.Items.Where(i => i.Selectable && !i.Recent).ToList(); return s.Count > 0 && s.All(i => i.Selected); }
    }
    public bool Expanded { get => _expanded; set { _expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BodyVis))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Arrow))); } }
    public Visibility BodyVis => _expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Arrow => _expanded ? "▲" : "▼";
    public string MoreText
    {
        get
        {
            int hidden = Result.Items.Count - Shown.Count;
            if (hidden <= 0) return "";
            long b = Result.Items.Skip(Shown.Count).Sum(i => i.Bytes);
            return $"+ {hidden} smaller item{(hidden == 1 ? "" : "s")} ({Palette.Bytes(b)}), included when you tick the whole category";
        }
    }
    public Visibility MoreVis => Result.Items.Count > Shown.Count ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManagedVis => Cat.ManagedByWindows ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class CleanerPanel : UserControl
{
    readonly SystemMonitor _mon;
    List<CategoryVm> _cats = new();
    CancellationTokenSource? _cts;
    bool _busy;

    public CleanerPanel(SystemMonitor mon)
    {
        InitializeComponent();
        _mon = mon;
        DryRunSwitch.IsChecked = mon.Settings.CleanerDryRun;
        UpdateDryRunUi();
    }

    bool DryRun => DryRunSwitch.IsChecked == true;

    /// <summary>Screenshot helper: open the biggest categories.</summary>
    public void ExpandTop(int n)
    {
        foreach (var c in _cats.Where(c => !c.Cat.ManagedByWindows).OrderByDescending(c => c.Result.TotalBytes).Take(n)) c.Expanded = true;
        foreach (var c in _cats.Where(c => c.Cat.ManagedByWindows)) c.Expanded = true;
    }

    void UpdateDryRunUi()
    {
        CleanButton.Content = DryRun ? "Review (dry run)..." : "Review and clean...";
        CleanButton.Style = (Style)FindResource(DryRun ? "AccentButton" : "DangerButton");
    }

    void OnDryRunChanged(object sender, RoutedEventArgs e)
    {
        _mon.Settings.CleanerDryRun = DryRun;
        _mon.Settings.Save();
        UpdateDryRunUi();
    }

    async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_busy) { _cts?.Cancel(); return; }
        await ScanAsync();
    }

    public async Task ScanAsync()
    {
        _busy = true; ScanButton.Content = "Cancel";
        ResultCard.Visibility = Visibility.Collapsed;
        ScanStatus.Text = "Scanning...";
        ScanSub.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var ctx = new CleanContext(_mon.Settings);
        var progress = new Progress<string>(s => ScanSub.Text = s);
        try
        {
            var sw = Stopwatch.StartNew();
            var results = await Task.Run(() => CleanerEngine.ScanAll(ctx, progress, token), token);
            _cats = results.Where(r => r.Items.Count > 0 || r.Error != null).Select(r => new CategoryVm(r)).ToList();
            foreach (var c in _cats)
                foreach (var i in c.Result.Items) i.PropertyChanged += (_, _) => UpdateSummary();
            CategoryList.ItemsSource = _cats;
            long total = results.Where(r => !r.Category.ManagedByWindows).Sum(r => r.TotalBytes);
            ScanStatus.Text = $"Scan finished in {sw.Elapsed.TotalSeconds:0} s. Nothing was changed.";
            SummaryCard.Visibility = Visibility.Visible;
            FoundText.Text = $"{Palette.Bytes(total)} can be cleaned";
            UpdateSummary();
        }
        catch (OperationCanceledException) { ScanStatus.Text = "Scan cancelled."; }
        catch (Exception ex) { ScanStatus.Text = "Scan failed: " + ex.Message; Log.Write("clean scan: " + ex); }
        finally { _busy = false; ScanButton.Content = "Scan"; ScanSub.Visibility = Visibility.Collapsed; }
    }

    IEnumerable<CleanItem> Selected() => _cats.Where(c => !c.Cat.ManagedByWindows).SelectMany(c => c.Result.Items).Where(i => i.Selected && i.Selectable);

    void UpdateSummary()
    {
        var sel = Selected().ToList();
        var guard = new PathGuard(_mon.Settings);
        int onC = sel.Count(i => guard.IsOnSystemDrive(i.Path));
        SelectedText.Text = sel.Count == 0 ? "Nothing ticked yet. Tick items, or use Select all safe."
            : $"{sel.Count} item{(sel.Count == 1 ? "" : "s")} ticked  ·  {Palette.Bytes(sel.Sum(i => i.Bytes))}" + (onC > 0 ? $"  ·  {onC} on the system drive" : "");
        CleanButton.IsEnabled = sel.Count > 0;
    }

    void OnSelectSafe(object sender, RoutedEventArgs e)
    {
        foreach (var c in _cats.Where(c => !c.Cat.ManagedByWindows))
            foreach (var i in c.Result.Items) i.Selected = i.Selectable && !i.Recent;
    }

    void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var c in _cats) foreach (var i in c.Result.Items) i.Selected = false;
    }

    void OnCategoryCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CategoryVm c) return;
        bool target = !c.AllSelected;
        foreach (var i in c.Result.Items.Where(i => i.Selectable && !i.Recent)) i.Selected = target;
        ((CheckBox)sender).IsChecked = c.AllSelected;
    }

    void OnHeaderClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindUp<CheckBox>(d) != null) return;
        if ((sender as FrameworkElement)?.Tag is CategoryVm c) c.Expanded = !c.Expanded;
    }

    static T? FindUp<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null) { if (d is T t) return t; d = VisualTreeHelper.GetParent(d); }
        return null;
    }

    void OnOpenCleanmgr(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("cleanmgr.exe", "/d C") { UseShellExecute = true }); } catch (Exception ex) { Log.Write("cleanmgr: " + ex.Message); }
    }

    async void OnClean(object sender, RoutedEventArgs e)
    {
        var items = Selected().ToList();
        if (items.Count == 0) return;
        var guard = new PathGuard(_mon.Settings);
        var dlg = new CleanConfirmWindow(items, DryRun, guard) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        CleanButton.IsEnabled = false; ScanButton.IsEnabled = false;
        ResultCard.Visibility = Visibility.Visible;
        ResultCard.Background = Palette.Frozen("#1F3340"); ResultCard.BorderBrush = Palette.Frozen("#2F5F7A");
        ResultTitle.Text = DryRun ? "Dry run in progress..." : "Cleaning...";
        ResultText.Text = "";
        var progress = new Progress<(int done, int total, string path)>(p => ResultText.Text = $"{p.done} of {p.total}  {p.path}");
        bool dry = DryRun, recycle = dlg.ForceRecycle;
        try
        {
            // Elevated: places a normal user can write are cleaned by a helper that runs with the user's own rights (see Deleter);
            // not elevated: this process already has only the user's rights and deletes directly.
            IUserDeleteRunner? helper = Elevation.IsTokenElevated ? HelperRunner.ForInstalledApp() : null;
            var results = await Task.Run(() => new Deleter(guard, dry, helper).Run(items, recycle, progress, CancellationToken.None));
            long bytes = results.Where(r => r.Success).Sum(r => r.Bytes);
            int ok = results.Count(r => r.Success), bad = results.Count - ok;
            ResultTitle.Text = dry ? $"Dry run: would free {Palette.Bytes(bytes)} from {ok} item(s). Nothing was deleted." : $"Freed {Palette.Bytes(bytes)} from {ok} item(s)." + (bad > 0 ? $" {bad} could not be fully removed." : "");
            ResultText.Text = bad == 0 ? (dry ? "Turn off Dry run, tick the items again and confirm to really clean. Every action is logged." : "Every deletion is logged in deletions.log in the data folder.")
                : string.Join("\n", results.Where(r => !r.Success).Take(6).Select(r => $"{r.Path}: {r.Message}")) + (bad > 6 ? $"\n... and {bad - 6} more (see deletions.log)" : "");
            ResultCard.Background = Palette.Frozen(bad == 0 ? "#223A2A" : "#4A3A16"); ResultCard.BorderBrush = Palette.Frozen(bad == 0 ? "#2F5F3C" : "#6B5420");
            if (!dry) await ScanAsync();
        }
        catch (Exception ex) { ResultTitle.Text = "Clean failed: " + ex.Message; Log.Write("clean: " + ex); }
        finally { ScanButton.IsEnabled = true; UpdateSummary(); }
    }
}
