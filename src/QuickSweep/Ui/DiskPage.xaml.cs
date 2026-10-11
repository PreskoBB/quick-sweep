using System.Windows;
using System.Windows.Controls;
using QuickSweep.Core;

namespace QuickSweep.Ui;

public partial class DiskPage : UserControl, IPage
{
    readonly SystemMonitor _mon;
    CleanerPanel? _clean;
    ExplorerPanel? _explore;

    public DiskPage(SystemMonitor mon)
    {
        InitializeComponent();
        _mon = mon;
        ShowTab();
    }

    public void OnShown() { }
    public void OnHidden() { _explore?.Release(); _explore = null; }

    // screenshot helpers (dev)
    public async Task DemoCleanAsync()
    {
        TabClean.IsChecked = true; ShowTab();
        await _clean!.ScanAsync();
        _clean.ExpandTop(3);
    }
    public async Task DemoExploreAsync(string root)
    {
        TabExplore.IsChecked = true; ShowTab();
        await _explore!.DemoScan(root);
    }

    void OnTab(object sender, RoutedEventArgs e) { if (IsLoaded || Host != null) ShowTab(); }

    void ShowTab()
    {
        if (TabExplore.IsChecked == true) { _explore ??= new ExplorerPanel(); Host.Content = _explore; }
        else { _clean ??= new CleanerPanel(_mon); Host.Content = _clean; }
    }
}
