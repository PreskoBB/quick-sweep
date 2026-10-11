using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickSweep.Core.Cleaner;

namespace QuickSweep.Ui;

public sealed class ConfirmRow
{
    public string Path { get; set; } = "";
    public string Size { get; set; } = "";
    public string How { get; set; } = "";
    public string Note { get; set; } = "";
    public Brush NoteBrush { get; set; } = Palette.Warn;
    public Visibility NoteVis => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>The one gate before anything is deleted: lists the exact paths, the total, and asks for a typed DELETE on C:.</summary>
public partial class CleanConfirmWindow : Window
{
    const string Phrase = "DELETE";
    readonly bool _dry, _needsTyped;
    public bool ForceRecycle => RecycleAll.IsChecked == true;

    public CleanConfirmWindow(List<CleanItem> items, bool dryRun, PathGuard guard)
    {
        InitializeComponent();
        _dry = dryRun;
        long total = items.Sum(i => i.Bytes);
        int onC = items.Count(i => guard.IsOnSystemDrive(i.Path));
        int recent = items.Count(i => i.Recent);
        int links = items.Count(i => i.IsLink || i.LinksInside > 0);
        _needsTyped = !dryRun && onC > 0;

        HeadText.Text = dryRun ? $"Dry run: {items.Count} item{(items.Count == 1 ? "" : "s")}, {Palette.Bytes(total)}"
                            : $"Delete {items.Count} item{(items.Count == 1 ? "" : "s")} ({Palette.Bytes(total)})?";
        Sub.Text = dryRun ? "Nothing will be deleted. This runs every safety check and writes what would happen to the log."
                          : "These exact paths will be removed. Anything locked by Windows is skipped. Every action is logged.";
        var warn = new List<string>();
        if (recent > 0) warn.Add($"{recent} item(s) were modified in the last 7 days.");
        if (links > 0) warn.Add($"{links} item(s) contain or are junctions/symlinks: only the link is removed, never what it points to.");
        int sys = items.Count(i => AdminRoots.IsAdminClass(i.Path));
        if (sys > 0 && !dryRun) warn.Add($"{sys} item(s) are in system locations (Windows Temp, NVIDIA and error-report folders) and are always removed permanently, even if you send everything else to the Recycle Bin.");
        if (onC > 0 && !dryRun) warn.Add($"{onC} item(s) are on the system drive and are removed permanently (the Recycle Bin would not free that space).");
        WarnBox.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = string.Join("\n", warn);

        PathList.ItemsSource = items.OrderByDescending(i => i.Bytes).Select(i => new ConfirmRow
        {
            Path = i.Path, Size = Palette.Bytes(i.Bytes),
            How = i.IsLink ? "link only" : AdminRoots.IsAdminClass(i.Path) ? "permanent (system location)" : i.Mode == DeleteMode.Recycle ? "Recycle Bin" : "permanent",
            Note = i.Recent ? "modified in the last 7 days" : i.IsLink ? "link: the target is kept" : ""
        }).ToList();

        if (_needsTyped)
        {
            TypeBox.Visibility = Visibility.Visible;
            TypeLabel.Text = $"{onC} item(s) are on the system drive (C:). Type {Phrase} (capital letters) to enable the button.";
        }
        Confirm.Content = dryRun ? "Run dry run" : $"Delete {items.Count} item{(items.Count == 1 ? "" : "s")}";
        Confirm.Style = (Style)FindResource(dryRun ? "AccentButton" : "DangerButton");
        Confirm.IsEnabled = !_needsTyped;
    }

    void OnTypeChanged(object sender, TextChangedEventArgs e) => Confirm.IsEnabled = !_needsTyped || TypeInput.Text == Phrase;
    void OnConfirm(object sender, RoutedEventArgs e) { if (!_needsTyped || TypeInput.Text == Phrase) DialogResult = true; }
    void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
