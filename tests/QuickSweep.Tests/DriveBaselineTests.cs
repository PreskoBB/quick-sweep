using QuickSweep.Core;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>Stable damage is a warning, growing damage is critical, CRC errors point at the cable.</summary>
public class DriveBaselineTests
{
    static readonly DateTime Oct11 = new(2026, 10, 11, 17, 20, 0);

    static byte[] Sector(params (int id, int value, int worst, long raw)[] attrs)
    {
        var b = new byte[512];
        for (int i = 0; i < attrs.Length; i++)
        {
            int o = 2 + i * 12;
            b[o] = (byte)attrs[i].id; b[o + 3] = (byte)attrs[i].value; b[o + 4] = (byte)attrs[i].worst;
            for (int k = 0; k < 6; k++) b[o + 5 + k] = (byte)(attrs[i].raw >> (8 * k));
        }
        return b;
    }

    static DriveReport Read(byte[] data, string serial = "S", bool ssd = false)
    {
        var d = new DriveReport { IsSsd = ssd, Model = "Test", Serial = serial };
        d.Attrs = DriveHealth.ParseAttributes(data, null);
        DriveHealth.Summarize(d);
        return d;
    }

    static DriveReport Judge(byte[] data, DriveBaseline? b, bool ssd = false)
    {
        var d = Read(data, ssd: ssd);
        DriveHealth.Evaluate(d, b);
        return d;
    }

    [Fact]
    public void StableReallocatedCountIsOnlyAWarningWithOldDamageWording()
    {
        var d = Judge(Sector((5, 100, 100, 296)), new DriveBaseline { Reallocated = 296, Since = Oct11 });
        Assert.Equal(HealthState.Warning, d.State);
        Assert.False(d.Degrading);
        Assert.Equal("has old damage", d.Headline);
        Assert.Contains("296 reallocated sectors, unchanged since 11 Oct. Keep a backup of anything important on it.", d.Findings);
    }

    [Fact]
    public void GrowingReallocatedCountIsCritical()
    {
        var d = Judge(Sector((5, 100, 100, 300)), new DriveBaseline { Reallocated = 296, Since = Oct11 });
        Assert.Equal(HealthState.Critical, d.State);
        Assert.True(d.Degrading);
        Assert.Equal("is getting worse", d.Headline);
        Assert.Contains("296", d.Findings[0]);
        Assert.Contains("300", d.Findings[0]);
    }

    [Fact]
    public void FirstSightOfReallocatedSectorsIsAWarningNotAnAlarm()
    {
        var d = Judge(Sector((5, 100, 100, 296)), null);
        Assert.Equal(HealthState.Warning, d.State);
        Assert.False(d.Degrading);
    }

    [Theory]
    [InlineData(197)]
    [InlineData(198)]
    public void AnyPendingOrUncorrectableSectorIsCriticalEvenWhenStable(int id)
    {
        var b = new DriveBaseline { Pending = 1, Uncorrectable = 1, Since = Oct11 };
        var d = Judge(Sector((id, 100, 100, 1)), b);
        Assert.Equal(HealthState.Critical, d.State);
        Assert.False(d.Degrading);
        Assert.Equal("is failing", d.Headline);
    }

    [Fact]
    public void GrowingPendingSectorsAreGettingWorse()
    {
        var d = Judge(Sector((197, 100, 100, 4)), new DriveBaseline { Pending = 1, Since = Oct11 });
        Assert.Equal(HealthState.Critical, d.State);
        Assert.True(d.Degrading);
        Assert.Equal("is getting worse", d.Headline);
    }

    [Fact]
    public void ReallocatedPlusPendingStaysCriticalAndFailing()
    {
        var d = Judge(Sector((5, 100, 100, 296), (197, 100, 100, 8)), new DriveBaseline { Reallocated = 296, Pending = 8, Since = Oct11 });
        Assert.Equal(HealthState.Critical, d.State);
        Assert.Equal("is failing", d.Headline);                    // the headline follows the worst finding
        Assert.Equal(2, d.Findings.Count);
    }

    [Fact]
    public void ManyCrcErrorsAreACableWarningNotAFailingDrive()
    {
        var d = Judge(Sector((199, 100, 100, 786451)), new DriveBaseline { CrcErrors = 786451, CrcSince = Oct11, Since = Oct11 }, ssd: true);
        Assert.Equal(HealthState.Warning, d.State);
        Assert.False(d.Degrading);
        Assert.Equal("has a cable problem", d.Headline);
        Assert.Contains("786,451", d.Findings[0]);
        Assert.Contains("reseat or replace the SATA cable", d.Findings[0]);
    }

    [Fact]
    public void RisingCrcErrorsEscalateToCritical()
    {
        var d = Judge(Sector((199, 100, 100, 786500)), new DriveBaseline { CrcErrors = 786451, CrcSince = Oct11, Since = Oct11 }, ssd: true);
        Assert.Equal(HealthState.Critical, d.State);
        Assert.True(d.Degrading);
        Assert.Contains("cable", d.Headline);
        Assert.Contains("786,451", d.Findings[0]);
    }

    [Fact]
    public void CrcErrorsUnknownInTheOldBaselineAreNotCalledRising()
    {
        var b = new DriveBaseline { Since = Oct11 };            // as loaded from a 1.0.2 file: CrcErrors == null
        var d = Judge(Sector((199, 100, 100, 786451)), b, ssd: true);
        Assert.Equal(HealthState.Warning, d.State);
        Assert.False(d.Degrading);
    }

    [Fact]
    public void FewCrcErrorsAreIgnored()
        => Assert.Equal(HealthState.Good, Judge(Sector((199, 100, 100, 12)), null, ssd: true).State);

    [Fact]
    public void BaselineStoresCrcAndFillsItInForOldFiles()
    {
        var hddDrive = Read(Sector((5, 100, 100, 296)), "A");
        var ssdDrive = Read(Sector((199, 100, 100, 786451)), "B", ssd: true);
        var store = new BaselineStore();
        store.Drives["A"] = new DriveBaseline { Reallocated = 296, Since = Oct11 };     // an old file, no CRC
        var now = new DateTime(2026, 10, 18, 9, 0, 0);

        Assert.True(DriveHealth.ApplyBaseline(new() { hddDrive, ssdDrive }, store, now));
        Assert.Equal(HealthState.Warning, hddDrive.State);                                // stable: old damage
        Assert.Equal(0, store.Drives["A"].CrcErrors ?? 0);
        Assert.Equal(786451, store.Drives["B"].CrcErrors);                               // a new drive remembers CRC
        Assert.Equal(now, store.Drives["B"].Since);

        var again = Read(Sector((199, 100, 100, 786999)), "B", ssd: true);               // next check: the count went up
        DriveHealth.ApplyBaseline(new() { again }, store, now.AddHours(1));
        Assert.Equal(HealthState.Critical, again.State);
        Assert.True(again.Degrading);
    }

    [Fact]
    public void TheDriveFromTheBugReportIsAnOldDamageWarning()
    {
        // 296 reallocated sectors, the same number in the stored baseline since 11 Oct: a warning, not "failing"
        var hdd = new DriveReport { Model = "Example HDD 2TB", Serial = "SN-EXAMPLE-1" };
        hdd.Attrs = DriveHealth.ParseAttributes(Sector((5, 100, 100, 296), (197, 100, 100, 0), (198, 100, 100, 0)), null);
        DriveHealth.Summarize(hdd);
        var store = new BaselineStore { Drives = { ["SN-EXAMPLE-1"] = new DriveBaseline { Reallocated = 296, Since = Oct11 } } };
        DriveHealth.ApplyBaseline(new() { hdd }, store, new DateTime(2026, 10, 18));
        Assert.Equal(HealthState.Warning, hdd.State);
        var (on, title, detail, level, quiet) = SystemMonitor.HealthAlert(hdd);
        Assert.True(on);
        Assert.True(quiet);
        Assert.Equal(AlertLevel.Warning, level);
        Assert.Equal("Example HDD 2TB has old damage", title);
        Assert.StartsWith("296 reallocated sectors, unchanged since 11 Oct.", detail);
    }

    [Fact]
    public void CounterThatWentDownLowersTheBaselineSoALaterRiseIsSeen()
    {
        var store = new BaselineStore { Drives = { ["S"] = new DriveBaseline { Pending = 5, Since = Oct11 } } };
        DriveHealth.ApplyBaseline(new() { Read(Sector((197, 100, 100, 0))) }, store, DateTime.Now);
        Assert.Equal(0, store.Drives["S"].Pending);
        var later = Read(Sector((197, 100, 100, 3)));
        DriveHealth.ApplyBaseline(new() { later }, store, DateTime.Now);
        Assert.True(later.Degrading);
    }

    [Fact]
    public void BaselineFileFromVersion102StillLoads()
    {
        const string json = "{\"Drives\":{\"SN-EXAMPLE-1\":{\"Reallocated\":296,\"Pending\":0,\"Uncorrectable\":0,\"LifeLeftPct\":null,\"Since\":\"2026-10-11T17:20:57.7405445+03:00\"}}}";
        var store = System.Text.Json.JsonSerializer.Deserialize<BaselineStore>(json)!;
        Assert.Equal(296, store.Drives["SN-EXAMPLE-1"].Reallocated);
        Assert.Null(store.Drives["SN-EXAMPLE-1"].CrcErrors);
    }
}
