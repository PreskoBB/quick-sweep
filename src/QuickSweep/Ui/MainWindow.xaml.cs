using System.Windows;
using System.Windows.Controls;
using QuickSweep.Core;
using QuickSweep.Native;

namespace QuickSweep.Ui;

public interface IPage
{
    void OnShown();
    void OnHidden();
}

public partial class MainWindow : Window
{
    sealed record NavEntry(string Key, string Title, Func<UserControl> Create);

    readonly List<NavEntry> _nav;
    readonly Dictionary<string, UserControl> _pages = new();
    UserControl? _current;

    public MainWindow()
    {
        InitializeComponent();
        var mon = App.Mon;
        _nav = PageCatalog.All(mon).Select(p => new NavEntry(p.key, p.title, p.create)).ToList();
        foreach (var n in _nav) AddNav(n.Key, n.Title);
        ElevText.Text = Elevation.IsElevated ? "Running with admin rights" : "No admin rights: CPU temperature and drive health need the logon task (install.ps1)";
        SourceInitialized += (_, _) => WindowEffects.Apply(this, false);
        Closed += (_, _) => { if (_current is IPage p0) p0.OnHidden(); };
    }

    void AddNav(string key, string title)
    {
        var rb = new RadioButton { Content = title, Style = (Style)FindResource("NavItem"), Tag = key, GroupName = "nav" };
        rb.Checked += (_, _) => Navigate(key);
        NavPanel.Children.Add(rb);
    }

    public void Navigate(string key)
    {
        var entry = _nav.FirstOrDefault(n => n.Key == key) ?? _nav[0];
        if (_current is IPage old) old.OnHidden();
        if (!_pages.TryGetValue(entry.Key, out var page)) _pages[entry.Key] = page = entry.Create();
        PageHost.Content = page;
        _current = page;
        foreach (RadioButton r in NavPanel.Children.OfType<RadioButton>())
            if ((string)r.Tag == entry.Key && r.IsChecked != true) r.IsChecked = true;
        if (page is IPage p) p.OnShown();
    }

    public UserControl? Current => _current;
    public IEnumerable<string> PageKeys => _nav.Select(n => n.Key);
}
