using Microsoft.Win32;
using QuickSweep.Core;

namespace QuickSweep.Ui;

/// <summary>
/// Windows 11 hides new tray icons in the overflow flyout, which would hide the temperature numbers. On the very first
/// run this sets the per-icon "show in taskbar" switch for Quick Sweep's own icon (the same switch as
/// Settings > Personalization > Taskbar > Other system tray icons). It is done once; later changes by the user stick.
/// </summary>
internal static class TrayPromotion
{
    public static void TryPromoteOnce(AppSettings settings)
    {
        try
        {
            string? me = Environment.ProcessPath;
            if (me == null || string.Equals(settings.TrayPromotedFor, me, StringComparison.OrdinalIgnoreCase)) return;
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, true);
                if (k?.GetValue("ExecutablePath") is not string path || !path.Equals(me, StringComparison.OrdinalIgnoreCase)) continue;
                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                settings.TrayPromotedFor = me;
                settings.Save();
                Log.Write("tray icon set to show in the taskbar (first run)");
                return;
            }
        }
        catch (Exception e) { Log.Write("tray promotion: " + e.Message); }
    }
}
