using System.Runtime.InteropServices;
using QuickSweep.Ui;
using Xunit;

namespace QuickSweep.Tests;

public class SingleInstanceTests
{
    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(nint handle, int objectType, uint secInfo, out nint owner, out nint group, out nint dacl, out nint sacl, out nint sd);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint sd, uint rev, uint info, out nint str, out uint len);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint h);

    static string LabelOf(EventWaitHandle ev)
    {
        // LABEL_SECURITY_INFORMATION (0x10) on a kernel object (6)
        Assert.Equal(0u, GetSecurityInfo(ev.SafeWaitHandle.DangerousGetHandle(), 6, 0x10, out _, out _, out _, out _, out nint sd));
        try
        {
            Assert.True(ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, 0x10, out nint s, out _));
            try { return Marshal.PtrToStringUni(s)!; } finally { LocalFree(s); }
        }
        finally { LocalFree(sd); }
    }

    [Fact]
    public void OpenEventCarriesAMediumMandatoryLabel()
    {
        // An elevated creator would otherwise stamp a High label and a medium-integrity launcher could not signal it.
        SingleInstance.EventName = @"Local\QuickSweep.Test." + Guid.NewGuid().ToString("N");
        using var ev = SingleInstance.Create(out bool created);
        Assert.NotNull(ev);
        Assert.True(created);
        var label = LabelOf(ev!);
        Assert.Contains("ML;;NW;;;ME", label);
    }

    [Fact]
    public void SecondCreateReportsTheInstanceAlreadyExists()
    {
        SingleInstance.EventName = @"Local\QuickSweep.Test." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.Create(out bool created1);
        using var second = SingleInstance.Create(out bool created2);
        Assert.True(created1);
        Assert.False(created2);              // a second elevated start must give up instead of adding a tray icon
    }

    [Fact]
    public void LauncherCanOpenAndSignalTheRunningInstance()
    {
        SingleInstance.EventName = @"Local\QuickSweep.Test." + Guid.NewGuid().ToString("N");
        using var owner = SingleInstance.Create(out _);
        using var launcher = SingleInstance.TryOpen(out bool denied);
        Assert.NotNull(launcher);
        Assert.False(denied);
        launcher!.Set();
        Assert.True(owner!.WaitOne(1000));   // the instance wakes up
    }

    [Fact]
    public void NoInstanceMeansNothingToOpen()
    {
        SingleInstance.EventName = @"Local\QuickSweep.Test." + Guid.NewGuid().ToString("N");
        Assert.Null(SingleInstance.TryOpen(out bool denied));
        Assert.False(denied);
    }

    [Fact]
    public void SddlGrantsTheUserAndAdministratorsOnly()
    {
        var s = SingleInstance.Sddl("S-1-5-21-1-2-3-1001");
        Assert.Contains("(A;;GA;;;S-1-5-21-1-2-3-1001)", s);
        Assert.Contains("(A;;GA;;;SY)", s);
        Assert.Contains("(A;;GA;;;BA)", s);
        Assert.DoesNotContain(";;;WD)", s);   // not Everyone
    }
}
