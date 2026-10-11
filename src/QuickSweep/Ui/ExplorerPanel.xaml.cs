using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickSweep.Core;
using QuickSweep.Core.Cleaner;

namespace QuickSweep.Ui;

public sealed class TreeRow
{
    public DirNode Node { get; init; } = null!;
    public int Depth { get; init; }
    public bool Expanded { get; set; }
    public double Pct { get; init; }
    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Toggle => Node.Children.Count == 0 ? "" : Expanded ? "▼" : "▶";
    public string Name => Node.IsLink ? Node.Name + "  (link, not followed)" : Node.Name;
    public string Path => Node.FullPath;
    public string SizeText => Palette.Bytes(Node.Size);
    public string PctText => Pct >= 0.1 ? $"{Pct:0}%" : "";
    public string FilesText => Node.Files.ToString("N0");
}

public partial class ExplorerPanel : UserControl
{
    sealed record RootChoice(string Label, string? Path);

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(300) };
    DirNode? _root, _view;
    CancellationTokenSource? _cts;
    ScanProgress? _prog;
    readonly HashSet<DirNode> _expanded = new();
    string _sortKey = "Size";
    bool _desc = true, _scanning, _loadingRoots;
    string? _customPath;

    public ExplorerPanel()
    {
        InitializeComponent();
        _poll.Tick += (_, _) =>
        {
            if (_prog != null) Status.Text = $"Scanning... {_prog.Files:N0} files, {Palette.Bytes(_prog.Bytes)}  ·  {Shorten(_prog.Current)}";
        };
        Map.Drill += n => { ShowNode(n); };
        LoadRoots();
    }

    static string Shorten(string p) => p.Length > 70 ? "..." + p[^67..] : p;

    void LoadRoots()
    {
        _loadingRoots = true;
        var list = new List<RootChoice>();
        if (Demo.On)
        {
            foreach (var label in Demo.DriveChoices()) list.Add(new RootChoice(label, label[..3]));
            list.Add(new RootChoice("Choose a folder...", null));
            RootBox.ItemsSource = list; RootBox.DisplayMemberPath = "Label"; RootBox.SelectedIndex = 1;
            _loadingRoots = false;
            return;
        }
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(new RootChoice($"{d.Name}   ({Palette.Bytes(d.TotalSize - d.AvailableFreeSpace)} used of {Palette.Bytes(d.TotalSize)})", d.Name)); } catch { }
        }
        if (_customPath != null) list.Add(new RootChoice(_customPath, _customPath));
        list.Add(new RootChoice("Choose a folder...", null));
        RootBox.ItemsSource = list;
        RootBox.DisplayMemberPath = "Label";
        RootBox.SelectedIndex = Math.Max(0, list.FindIndex(r => r.Path?.StartsWith("C", StringComparison.OrdinalIgnoreCase) == true));
        _loadingRoots = false;
    }

    void OnRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRoots || RootBox.SelectedItem is not RootChoice rc || rc.Path != null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to analyse" };
        if (dlg.ShowDialog() == true) { _customPath = dlg.FolderName; LoadRoots(); RootBox.SelectedIndex = RootBox.Items.Count - 2; }
        else RootBox.SelectedIndex = 0;
    }

    async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_scanning) { _cts?.Cancel(); return; }
        if (RootBox.SelectedItem is not RootChoice { Path: { } path }) return;
        await RunScan(path);
    }

    public Task DemoScan(string path) => RunScan(path);

    async Task RunScan(string path)
    {
        _scanning = true; ScanButton.Content = "Cancel"; RootBox.IsEnabled = false;
        _cts = new CancellationTokenSource();
        _prog = new ScanProgress();
        _poll.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            var token = _cts.Token; var prog = _prog;
            var root = Demo.On ? Demo.DiskTree() : await Task.Run(() => DiskScanner.Scan(path, prog, token), token);
            _root = root; _expanded.Clear(); _expanded.Add(root);
            Status.Text = $"{Palette.Bytes(root.Size)} in {root.Files:N0} files, {root.Dirs:N0} folders  ·  scanned in {sw.Elapsed.TotalSeconds:0} s";
            ShowNode(root);
            Rebuild();
        }
        catch (OperationCanceledException) { Status.Text = "Scan cancelled."; }
        catch (Exception ex) { Status.Text = "Scan failed: " + ex.Message; Log.Write("explorer scan: " + ex); }
        finally { _poll.Stop(); _prog = null; _scanning = false; ScanButton.Content = "Scan"; RootBox.IsEnabled = true; }
    }

    // ---- tree list ---------------------------------------------------------------
    void Rebuild()
    {
        if (_root == null) { Tree.ItemsSource = null; return; }
        var rows = new List<TreeRow>();
        void Add(DirNode n, int depth)
        {
            bool open = _expanded.Contains(n);
            rows.Add(new TreeRow { Node = n, Depth = depth, Expanded = open, Pct = n.Parent == null || n.Parent.Size == 0 ? 100 : 100.0 * n.Size / n.Parent.Size });
            if (!open) return;
            foreach (var c in Sorted(n.Children)) Add(c, depth + 1);
        }
        Add(_root, 0);
        var keep = (Tree.SelectedItem as TreeRow)?.Node;
        Tree.ItemsSource = rows;
        if (keep != null) Tree.SelectedItem = rows.FirstOrDefault(r => r.Node == keep);
    }

    IEnumerable<DirNode> Sorted(List<DirNode> l) => (_sortKey, _desc) switch
    {
        ("Name", false) => l.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
        ("Name", true) => l.OrderByDescending(n => n.Name, StringComparer.OrdinalIgnoreCase),
        ("Files", false) => l.OrderBy(n => n.Files),
        ("Files", true) => l.OrderByDescending(n => n.Files),
        (_, false) => l.OrderBy(n => n.Size),
        _ => l.OrderByDescending(n => n.Size),
    };

    void OnSort(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        if (key == _sortKey) _desc = !_desc; else { _sortKey = key; _desc = key != "Name"; }
        Rebuild();
    }

    void Toggle(TreeRow r)
    {
        if (r.Node.Children.Count == 0) return;
        if (!_expanded.Remove(r.Node)) _expanded.Add(r.Node);
        Rebuild();
    }

    void OnToggleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TreeRow r) { Toggle(r); e.Handled = true; }
    }

    void OnTreeDouble(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Tree.SelectedItem is TreeRow r) Toggle(r);
    }

    void OnTreeSelect(object sender, SelectionChangedEventArgs e)
    {
        if (Tree.SelectedItem is TreeRow r) ShowNode(r.Node, fromTree: true);
    }

    // ---- treemap ------------------------------------------------------------------
    void ShowNode(DirNode n, bool fromTree = false)
    {
        _view = n;
        Map.SetNode(n);
        Crumb.Text = $"{n.FullPath}  ·  {Palette.Bytes(n.Size)}";
        UpButton.IsEnabled = n.Parent != null;
        if (!fromTree && _root != null)
        {
            // open the path to it in the tree and select it
            for (var p = n.Parent; p != null; p = p.Parent) _expanded.Add(p);
            Rebuild();
            Tree.SelectedItem = (Tree.ItemsSource as List<TreeRow>)?.FirstOrDefault(r => r.Node == n);
            if (Tree.SelectedItem != null) Tree.ScrollIntoView(Tree.SelectedItem);
        }
    }

    void OnUp(object sender, RoutedEventArgs e) { if (_view?.Parent != null) ShowNode(_view.Parent); }

    void OnShow(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_view.FullPath}\"") { UseShellExecute = false, CreateNoWindow = true }); } catch { }
    }

    public void Release() { _cts?.Cancel(); _root = null; _view = null; Tree.ItemsSource = null; Map.SetNode(null); _expanded.Clear(); }
}
