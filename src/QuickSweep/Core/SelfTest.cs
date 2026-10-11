using Microsoft.Win32;

namespace QuickSweep.Core;

/// <summary>
/// Non-destructive end-to-end checks of the parts that touch Windows settings (run with --selftest=file):
/// startup toggling on a throw-away Run entry, audio session volume on an idle session, default-device call with the
/// current default. Everything it changes is restored before it returns.
/// </summary>
public static class SelfTest
{
    public static string Run()
    {
        var lines = new List<string>();
        void L(string s) => lines.Add(s);

        // --- startup toggle on a temporary entry --------------------------------
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string name = "QuickSweepSelfTestEntry";
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(runKey, true)!) k.SetValue(name, "\"" + Path.Combine(Environment.SystemDirectory, "notepad.exe") + "\" --quicksweep-selftest");
            var entry = StartupManager.Load(new()).First(e => e.Name == name);
            L($"startup: found test entry, enabled={entry.Enabled}, exe={entry.ExePath}");
            bool a = StartupManager.SetEnabled(entry, false);
            bool afterOff = StartupManager.ReadApproved(entry);
            using (var ak = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
                L($"startup: disable ok={a}, readback enabled={afterOff}, bytes={BitConverter.ToString((byte[])ak!.GetValue(name)!)}");
            bool b = StartupManager.SetEnabled(entry, true);
            L($"startup: enable ok={b}, readback enabled={StartupManager.ReadApproved(entry)}");
        }
        catch (Exception e) { L("startup: FAILED " + e.Message); }
        finally
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(runKey, true)!; k.DeleteValue(name, false);
                using var ak = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", true);
                ak?.DeleteValue(name, false);
                L("startup: temporary entry removed");
            }
            catch (Exception e) { L("startup cleanup: " + e.Message); }
        }

        // --- audio -----------------------------------------------------------------
        try
        {
            var audio = new AudioManager();
            var devs = audio.Devices();
            L("audio: devices = " + string.Join(" | ", devs.Select(d => d.Name + (d.IsDefault ? " [default]" : ""))));
            var def = devs.FirstOrDefault(d => d.IsDefault);
            if (def != null)
            {
                bool ok = audio.SetDefault(def.Id);   // same device: exercises the COM call without switching anything
                L($"audio: SetDefault(current default) ok={ok}, still default={audio.Devices().First(d => d.IsDefault).Id == def.Id}");
            }
            var m = audio.MasterVolume();
            L($"audio: master volume {(m is { } mv ? $"{mv.level * 100:0}% muted={mv.muted}" : "unavailable")}");
            var idle = audio.Sessions().FirstOrDefault(s => !s.Active && !s.IsSystemSounds && s.Volume > 0.95f);
            if (idle != null)
            {
                float before = idle.Volume;
                audio.SetSessionVolume(idle, 0.80f);
                var again = audio.Sessions().First(s => s.Name == idle.Name);
                L($"audio: '{idle.Name}' mixer {before * 100:0}% -> set 80% -> read back {again.Volume * 100:0}% low-flag={again.Low}");
                audio.SetSessionVolume(again, before);
                L($"audio: restored '{idle.Name}' to {audio.Sessions().First(s => s.Name == idle.Name).Volume * 100:0}%");
            }
            else L("audio: no idle session at 100% to test with");
        }
        catch (Exception e) { L("audio: FAILED " + e.Message); }

        return string.Join(Environment.NewLine, lines);
    }
}
