using System.Windows.Controls;
using QuickSweep.Core;

namespace QuickSweep.Ui;

/// <summary>Every page of the main window, in navigation order.</summary>
internal static class PageCatalog
{
    public static List<(string key, string title, Func<UserControl> create)> All(SystemMonitor mon) => new()
    {
        ("sessions", "Sessions", () => new SessionsPage(mon)),
        ("memory", "Memory", () => new MemoryPage(mon)),
        ("startup", "Startup apps", () => new StartupPage(mon)),
        ("audio", "Audio", () => new AudioPage(mon)),
        ("disk", "Disk cleaner", () => new DiskPage(mon)),
        ("health", "Drive health", () => new HealthPage(mon)),
        ("network", "Network", () => new NetworkPage(mon)),
        ("settings", "Settings", () => new SettingsPage(mon)),
    };
}
