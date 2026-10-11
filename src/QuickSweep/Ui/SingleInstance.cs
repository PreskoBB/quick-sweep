using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace QuickSweep.Ui;

/// <summary>
/// One tray instance per session, reachable from a non-elevated launcher (the Start-menu shortcut) while the running
/// instance is elevated. The "open" event carries an explicit Medium mandatory label: an object created by an elevated
/// process otherwise gets a High label, and Windows would refuse write access (EVENT_MODIFY_STATE) to the
/// medium-integrity launcher even though the DACL allows the user.
/// </summary>
internal static class SingleInstance
{
    public static string EventName = @"Local\QuickSweep.Open";

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint rev, out nint sd, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint h);

    /// <summary>The user, SYSTEM and Administrators may use it; mandatory label Medium, no-write-up.</summary>
    public static string Sddl(string userSid) => $"D:(A;;GA;;;{userSid})(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";

    /// <summary>Opens the running instance's event. inaccessible = an event exists but this process may not use it.</summary>
    /// <summary>Second event of the same kind: "write your readings to your own dump file". It carries no data.</summary>
    public static string DumpEventName => EventName + ".Dump";

    public static EventWaitHandle? TryOpen(out bool inaccessible, string? name = null)
    {
        inaccessible = false;
        try { return EventWaitHandle.OpenExisting(name ?? EventName); }
        catch (WaitHandleCannotBeOpenedException) { return null; }
        catch (UnauthorizedAccessException) { inaccessible = true; return null; }
        catch { return null; }
    }

    /// <summary>Creates the event (this process becomes the instance). createdNew = false: another instance owns it.</summary>
    public static EventWaitHandle? Create(out bool createdNew, string? sddlOverride = null, string? name = null)
    {
        createdNew = false;
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddlOverride ?? Sddl(sid), 1, out nint sd, out _)) return null;
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            nint h = CreateEventW(ref sa, false, false, name ?? EventName);
            int err = Marshal.GetLastWin32Error();
            if (h == 0) return null;
            createdNew = err != 183; // ERROR_ALREADY_EXISTS
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset);
            ev.SafeWaitHandle = new SafeWaitHandle(h, true);
            return ev;
        }
        finally { LocalFree(sd); }
    }
}
