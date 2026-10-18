using QuickSweep.Core;
using Xunit;

namespace QuickSweep.Tests;

public class DriveHealthTests
{
    /// <summary>Builds a SMART attribute sector: (id, value, worst, raw) per entry.</summary>
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

    static byte[] Thresholds(params (int id, int thr)[] t)
    {
        var b = new byte[512];
        for (int i = 0; i < t.Length; i++) { b[2 + i * 12] = (byte)t[i].id; b[2 + i * 12 + 1] = (byte)t[i].thr; }
        return b;
    }

    static DriveReport Report(bool ssd, byte[] data, byte[]? thr = null)
    {
        var d = new DriveReport { IsSsd = ssd, Model = "Test" };
        d.Attrs = DriveHealth.ParseAttributes(data, thr);
        DriveHealth.Summarize(d);
        DriveHealth.Evaluate(d);
        return d;
    }

    [Fact]
    public void ParsesAttributesAndRawValues()
    {
        var d = Report(true, Sector((5, 100, 100, 0), (9, 98, 98, 12345), (194, 70, 60, 0x0023_001C), (231, 97, 97, 0)));
        Assert.Equal(4, d.Attrs.Count);
        Assert.Equal(12345, d.PowerOnHours);
        Assert.Equal(0x1C, (int)d.TempC!.Value);      // temperature = low byte of the raw value
        Assert.Equal(97, d.LifeLeftPct);
        Assert.Equal(HealthState.Good, d.State);
    }

    [Fact]
    public void HealthyDriveHasNoFindings()
    {
        var d = Report(false, Sector((5, 100, 100, 0), (197, 100, 100, 0), (198, 100, 100, 0), (194, 30, 40, 30)));
        Assert.Equal(HealthState.Good, d.State);
        Assert.Empty(d.Findings);
    }

    [Theory]
    [InlineData(5, 3, HealthState.Warning)]
    [InlineData(5, 80, HealthState.Warning)]      // without history a reallocated count is a warning; growth makes it critical (DriveBaselineTests)
    [InlineData(197, 1, HealthState.Critical)]
    [InlineData(197, 40, HealthState.Critical)]
    [InlineData(198, 2, HealthState.Critical)]
    public void SectorCountersAreJudged(int id, long raw, HealthState expected)
    {
        var d = Report(false, Sector((id, 100, 100, raw)));
        Assert.Equal(expected, d.State);
    }

    [Theory]
    [InlineData(90, HealthState.Good)]
    [InlineData(20, HealthState.Warning)]
    [InlineData(8, HealthState.Critical)]
    public void SsdWearIsJudged(int lifeLeft, HealthState expected)
    {
        var d = Report(true, Sector((231, lifeLeft, lifeLeft, 0)));
        Assert.Equal(expected, d.State);
    }

    [Fact]
    public void FailedPreFailThresholdIsCritical()
    {
        var d = Report(false, Sector((3, 20, 20, 0)), Thresholds((3, 21)));
        Assert.Equal(HealthState.Critical, d.State);
    }

    [Fact]
    public void CrucialLifetimeAttributeIsUsedForWear()
    {
        var d = Report(true, Sector((202, 88, 88, 12)));
        Assert.Equal(88, d.LifeLeftPct);
        Assert.Contains("202", d.LifeSource);
    }
}
