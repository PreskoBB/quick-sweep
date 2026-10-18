using System.Reflection;
using System.Text.RegularExpressions;
using QuickSweep.Core;
using QuickSweep.Ui;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>
/// The flyout banner was blank because <see cref="Alert"/> exposed public fields, and a WPF binding only sees properties.
/// These tests read the real XAML and fail if any binding in the alert template points at something that is not a property.
/// </summary>
public class AlertBannerTests
{
    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QuickSweep.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static string AlertTemplate()
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "src", "QuickSweep", "Ui", "FlyoutWindow.xaml"));
        int start = xaml.IndexOf("x:Name=\"AlertList\"", StringComparison.Ordinal);
        Assert.True(start > 0, "AlertList not found in FlyoutWindow.xaml");
        int end = xaml.IndexOf("</ItemsControl>", start, StringComparison.Ordinal);
        return xaml[start..end];
    }

    [Fact]
    public void AlertHasNoPublicFields()
        => Assert.Empty(typeof(Alert).GetFields(BindingFlags.Public | BindingFlags.Instance));

    [Fact]
    public void EveryBindingInTheBannerTemplateIsAReadableAlertProperty()
    {
        var names = Regex.Matches(AlertTemplate(), @"\{Binding\s+(?:Path=)?(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("Title", names);
        Assert.Contains("Detail", names);
        foreach (var n in names)
        {
            var p = typeof(Alert).GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(p != null && p.CanRead, $"the banner binds to '{n}', which is not a public property of Alert");
        }
    }

    [Fact]
    public void BannerClickOpensTheAlertsPage()
    {
        var t = AlertTemplate();
        Assert.Contains("MouseLeftButtonUp=\"OnAlertClick\"", t);
        var code = File.ReadAllText(Path.Combine(Root(), "src", "QuickSweep", "Ui", "FlyoutWindow.xaml.cs"));
        Assert.Matches(@"void OnAlertClick[\s\S]*?_openMain\(a\.Page\)", code);
    }

    [Fact]
    public void DriveAlertOpensDriveHealthAndShowsTheDriveName()
    {
        var d = new DriveReport { Model = "Example HDD 2TB", Serial = "S1", State = HealthState.Warning, Headline = "has old damage" };
        d.Findings.Add("296 reallocated sectors, unchanged since 11 Oct. Keep a backup of anything important on it.");
        var (on, title, detail, level, quiet) = SystemMonitor.HealthAlert(d);
        Assert.True(on);
        Assert.Equal("Example HDD 2TB has old damage", title);
        Assert.Contains("296 reallocated sectors", detail);
        Assert.Equal(AlertLevel.Warning, level);
        Assert.True(quiet);

        var eng = new AlertEngine(new AppSettings());
        eng.Raise("drive:S1", on, title, detail, "health", level, quiet);
        var a = Assert.Single(eng.Active);
        Assert.Equal("health", a.Page);
        Assert.Contains("health", OpenRequest.PageKeys);
        Assert.False(string.IsNullOrWhiteSpace(a.Title));
        Assert.False(string.IsNullOrWhiteSpace(a.Detail));
    }

    [Fact]
    public void FailingDriveIsCriticalAndNotQuiet()
    {
        var d = new DriveReport { Model = "Disk", State = HealthState.Critical, Headline = "is getting worse", Degrading = true };
        d.Findings.Add("Reallocated sectors rose from 296 to 300.");
        var (_, title, _, level, quiet) = SystemMonitor.HealthAlert(d);
        Assert.Equal("Disk is getting worse", title);
        Assert.Equal(AlertLevel.Critical, level);
        Assert.False(quiet);
    }

    [Fact]
    public void HealthyDriveRaisesNothing()
        => Assert.False(SystemMonitor.HealthAlert(new DriveReport { Model = "Disk", State = HealthState.Good }).on);
}
