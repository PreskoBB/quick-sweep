using System.Runtime.InteropServices;
using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed class AudioDevice
{
    public string Id = "", Name = "";
    public bool IsDefault;
}

public sealed class AudioSession
{
    public int Pid;
    public string Name = "";
    public string? ExePath;
    public float Volume;        // 0..1 (the app's own level in the Windows volume mixer)
    public bool Muted;
    public bool Active;         // currently making sound
    public bool IsSystemSounds;
    internal object? Control;   // ISimpleAudioVolume
    public bool Low => Muted || Volume < 0.90f;
}

/// <summary>Windows Core Audio: output devices, default-device switch and per-app mixer levels.</summary>
public sealed class AudioManager
{
    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid CLSID_PolicyConfig = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly Guid EventContext = new("2c4a6f59-0e3c-4b3a-9d1f-7a8e5f0a1b11");

    const int eRender = 0, eConsole = 0, eMultimedia = 1, eCommunications = 2;
    const uint DEVICE_STATE_ACTIVE = 1;

    public List<AudioDevice> Devices()
    {
        if (Demo.On) return Demo.AudioDevices();
        var list = new List<AudioDevice>();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        string? defId = null;
        try { en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var d); d.GetId(out defId); } catch { }
        en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var coll);
        coll.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            coll.Item(i, out var dev);
            dev.GetId(out string id);
            list.Add(new AudioDevice { Id = id, Name = FriendlyName(dev), IsDefault = id == defId });
        }
        return list;
    }

    static string FriendlyName(IMMDevice dev)
    {
        try
        {
            dev.OpenPropertyStore(0, out var ps);
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            ps.GetValue(ref key, out var pv);
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "Audio device" : "Audio device"; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return "Audio device"; }
    }

    public bool SetDefault(string deviceId)
    {
        try
        {
            var t = Type.GetTypeFromCLSID(CLSID_PolicyConfig)!;
            var pc = (IPolicyConfig)Activator.CreateInstance(t)!;
            foreach (int role in new[] { eConsole, eMultimedia, eCommunications }) pc.SetDefaultEndpoint(deviceId, role);
            Log.Write("default audio output changed");
            return true;
        }
        catch (Exception e) { Log.Write("set default audio failed: " + e.Message); return false; }
    }

    public (float level, bool muted)? MasterVolume()
    {
        if (Demo.On) return (0.65f, false);
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev);
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, 23, 0, out var o);
            var ev = (IAudioEndpointVolume)o;
            ev.GetMasterVolumeLevelScalar(out float lvl);
            ev.GetMute(out bool m);
            return (lvl, m);
        }
        catch { return null; }
    }

    public void SetMasterVolume(float level)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev);
            var iid = IID_IAudioEndpointVolume;
            dev.Activate(ref iid, 23, 0, out var o);
            var g = EventContext;
            ((IAudioEndpointVolume)o).SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref g);
        }
        catch (Exception e) { Log.Write("set master volume failed: " + e.Message); }
    }

    /// <summary>Per-app sessions on the default output device.</summary>
    public List<AudioSession> Sessions()
    {
        if (Demo.On) return Demo.AudioSessions();
        var list = new List<AudioSession>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev);
            var iid = IID_IAudioSessionManager2;
            dev.Activate(ref iid, 23, 0, out var o);
            var mgr = (IAudioSessionManager2)o;
            mgr.GetSessionEnumerator(out var se);
            se.GetCount(out int n);
            var seen = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                se.GetSession(i, out var ctl);
                var c2 = (IAudioSessionControl2)ctl;
                c2.GetProcessId(out uint pid);
                bool sys = c2.IsSystemSoundsSession() == 0;
                var vol = (ISimpleAudioVolume)ctl;
                vol.GetMasterVolume(out float v);
                vol.GetMute(out bool muted);
                ctl.GetState(out int state);
                if (state == 2) continue; // expired
                string name = "";
                string? path = pid != 0 ? Win.ImagePath((int)pid) : null;
                if (sys) name = "System sounds";
                else if (path != null) name = ProcessInsight.Describe(path);
                else { ctl.GetDisplayName(out name); if (string.IsNullOrWhiteSpace(name)) name = $"Process {pid}"; }
                // many sessions per app (browser tabs): merge by app name, keep the lowest level
                var key = $"{name}|{sys}";
                var existing = list.FirstOrDefault(s => s.Name == name && s.IsSystemSounds == sys);
                if (existing != null)
                {
                    existing.Active |= state == 1;
                    if (v < existing.Volume) { existing.Volume = v; existing.Muted = muted; existing.Control = vol; }
                    continue;
                }
                list.Add(new AudioSession { Pid = (int)pid, Name = name, ExePath = path, Volume = v, Muted = muted, Active = state == 1, IsSystemSounds = sys, Control = vol });
            }
        }
        catch (Exception e) { Log.Write("audio sessions: " + e.Message); }
        return list.OrderByDescending(s => s.Active).ThenBy(s => s.Name).ToList();
    }

    public void SetSessionVolume(AudioSession s, float level, bool unmute = false)
    {
        try
        {
            var g = EventContext;
            var ctl = s.Control as ISimpleAudioVolume;
            ctl?.SetMasterVolume(Math.Clamp(level, 0f, 1f), ref g);
            if (unmute) ctl?.SetMute(false, ref g);
            s.Volume = level; if (unmute) s.Muted = false;
        }
        catch (Exception e) { Log.Write("set session volume failed: " + e.Message); }
    }

    // ---- COM declarations --------------------------------------------------------
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public nint p, p2; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(nint client);
        void UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(nint notify);
        void UnregisterControlChangeNotify(nint notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float db, ref Guid ctx);
        void SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        void GetMasterVolumeLevel(out float db);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        void SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        void GetChannelVolumeLevel(uint ch, out float db);
        void GetChannelVolumeLevelScalar(uint ch, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        void GetAudioSessionControl(nint groupId, uint flags, out IAudioSessionControl ctl);
        void GetSimpleAudioVolume(nint groupId, uint flags, out ISimpleAudioVolume vol);
        void GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl ctl);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        void GetGroupingParam(out Guid g);
        void SetGroupingParam(ref Guid g, ref Guid ctx);
        void RegisterAudioSessionNotification(nint n);
        void UnregisterAudioSessionNotification(nint n);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        void GetState(out int state);
        void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        void GetGroupingParam(out Guid g);
        void SetGroupingParam(ref Guid g, ref Guid ctx);
        void RegisterAudioSessionNotification(nint n);
        void UnregisterAudioSessionNotification(nint n);
        // IAudioSessionControl2
        void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        void SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool opt);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, ref Guid ctx);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // undocumented but stable since Windows 7; used by every default-device switcher
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat(nint a, nint b);
        void GetDeviceFormat(nint a, int b, nint c);
        void ResetDeviceFormat(nint a);
        void SetDeviceFormat(nint a, nint b, nint c);
        void GetProcessingPeriod(nint a, int b, nint c, nint d);
        void SetProcessingPeriod(nint a, nint b);
        void GetShareMode(nint a, nint b);
        void SetShareMode(nint a, nint b);
        void GetPropertyValue(nint a, nint b, nint c);
        void SetPropertyValue(nint a, nint b, nint c);
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        void SetEndpointVisibility(nint a, int b);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }
}
