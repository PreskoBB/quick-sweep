using QuickSweep.Core;
using Xunit;

namespace QuickSweep.Tests;

public class PingReportTests
{
    static readonly DateTime T0 = new(2026, 10, 20, 21, 10, 0, DateTimeKind.Local);
    static PingSample S(int sec, double? router, double? net) => new(T0.AddSeconds(sec), router, net);

    // ---- nearest-sample lookup ----
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.9, 0)]          // dx = 4 (width 40, 11 samples): 1.9 is nearer to index 0
    [InlineData(2.1, 1)]
    [InlineData(20, 5)]
    [InlineData(39.9, 10)]
    [InlineData(40, 10)]
    [InlineData(-30, 0)]          // pointer left of the chart: clamped
    [InlineData(500, 10)]         // right of it: clamped
    public void NearestIndexSnapsToTheClosestSample(double x, int expected)
        => Assert.Equal(expected, PingReport.NearestIndex(x, 40, 11));

    [Fact]
    public void NearestIndexHandlesDegenerateCharts()
    {
        Assert.Equal(-1, PingReport.NearestIndex(10, 100, 0));
        Assert.Equal(0, PingReport.NearestIndex(10, 100, 1));
        Assert.Equal(-1, PingReport.NearestIndex(10, 0, 5));
        Assert.Equal(-1, PingReport.NearestIndex(double.NaN, 100, 5));
    }

    // ---- clipped values ----
    [Fact]
    public void CeilingIsBetween120And500()
    {
        Assert.Equal(120, PingReport.Ceiling(new double?[] { 3, null, 20 }));
        Assert.Equal(120, PingReport.Ceiling(new double?[] { null, null }));
        Assert.Equal(230, PingReport.Ceiling(new double?[] { 200, 10 }), 6);
        Assert.Equal(500, PingReport.Ceiling(new double?[] { 2000, 10 }));
    }

    [Fact]
    public void OnlyRepliesAboveTheCeilingAreClipped()
    {
        double top = PingReport.Ceiling(new double?[] { 2000, 18 });
        Assert.True(PingReport.IsClipped(510, top));
        Assert.True(PingReport.IsClipped(2000, top));
        Assert.False(PingReport.IsClipped(500, top));
        Assert.False(PingReport.IsClipped(412, top));
        Assert.False(PingReport.IsClipped(18, PingReport.Ceiling(new double?[] { 18 })));
    }

    [Fact]
    public void TooltipShowsTheTrueValueEvenWhenClipped()
    {
        var plain = PingReport.Tooltip(T0.AddSeconds(5), 18, 500);
        Assert.Equal(new[] { "21:10:05  ·  18 ms" }, plain);
        var clipped = PingReport.Tooltip(T0.AddSeconds(9), 2000, 500);
        Assert.Equal("21:10:09  ·  2,000 ms", clipped[0]);
        Assert.Contains("above the chart", clipped[1]);
        var lost = PingReport.Tooltip(T0, null, 500);
        Assert.Equal(new[] { "21:10:00  ·  lost" }, lost);
        Assert.Equal("<1 ms", PingReport.Ms(0));
    }

    // ---- peak / loss ----
    [Fact]
    public void SummarizeFindsPeakTimeLastReadingAndLoss()
    {
        var hist = new[] { S(0, 2, 18), S(3, 2, 412), S(6, null, 20), S(9, 3, null), S(12, 2, 18) };
        var (r, n) = PingMonitor.Summarize(hist);
        Assert.Equal(412, n.Max);
        Assert.Equal(T0.AddSeconds(3), n.MaxAt);
        Assert.Equal(1, n.Lost);
        Assert.Equal(18, n.LastMs);
        Assert.Equal(3, r.Max);
        Assert.Equal(T0.AddSeconds(9), r.MaxAt);
        Assert.Equal(1, r.Lost);
        Assert.Equal(20, r.LossPct, 6);
    }

    [Fact]
    public void StatsLineHasNowPeakAndOnlyMentionsLossWhenThereIsSome()
    {
        var (_, n) = PingMonitor.Summarize(new[] { S(0, 2, 18), S(60 * 4 + 5, 2, 412), S(60 * 5, 2, 18) });
        string line = PingReport.StatsLine(n);
        Assert.StartsWith("now 18 ms  ·  peak 412 ms at 21:14", line);
        Assert.DoesNotContain("loss", line);

        var (_, lossy) = PingMonitor.Summarize(new[] { S(0, 2, 18), S(3, 2, null), S(6, 2, null), S(9, 2, 20) });
        Assert.Contains("loss 50% (2 lost)", PingReport.StatsLine(lossy));
        var (_, lastLost) = PingMonitor.Summarize(new[] { S(0, 2, 18), S(3, 2, null) });
        Assert.StartsWith("now lost", PingReport.StatsLine(lastLost));
        Assert.Equal("no data yet", PingReport.StatsLine(new PingStats()));
        var (_, allLost) = PingMonitor.Summarize(new[] { S(0, 2, null), S(3, 2, null) });
        Assert.DoesNotContain("peak", PingReport.StatsLine(allLost));
    }

    [Fact]
    public void HistoryKeepsTimestampsAndStaysBounded()
    {
        var m = new PingMonitor();
        for (int i = 0; i < PingMonitor.HistoryCap * 3; i++) m.Record(S(i * 3, 2, 18));
        var h = m.History(T0);
        Assert.InRange(h.Count, PingMonitor.HistoryCap - 600, PingMonitor.HistoryCap);   // never above the cap
        Assert.Equal(T0.AddSeconds(PingMonitor.HistoryCap * 3 * 3 - 3), h[^1].T);        // newest kept, with its own time
        Assert.Equal(h[0].T.AddSeconds(3), h[1].T);                                      // every sample keeps its own stamp
    }

    // ---- session summary line ----
    static PingBrief Brief(double max, DateTime? at = null, int lost = 0, double lossPct = 0, int count = 1200)
        => new() { Max = max, MaxAt = at, Lost = lost, LossPct = lossPct, Count = count, Spikes = max >= 100 ? 1 : 0 };

    [Fact]
    public void SessionLineIsEmptyWithoutSpikeOrLoss()
        => Assert.Equal("", PingReport.SessionLine(Brief(9), Brief(64)));

    [Fact]
    public void SessionLineNamesTheWorstPingTimeAndSource()
    {
        string s = PingReport.SessionLine(Brief(40), Brief(310, T0.AddMinutes(32)));
        Assert.Equal("Worst ping 310 ms at 21:42 (internet)", s);
        Assert.Equal("Worst ping 2,310 ms at 21:10 (router)", PingReport.SessionLine(Brief(2310, T0), Brief(150, T0.AddMinutes(1))));
    }

    [Fact]
    public void SessionLineCountsLostPackets()
    {
        Assert.Equal("Worst ping 310 ms at 21:42 (internet)  ·  22 lost packets (internet)",
            PingReport.SessionLine(Brief(5), Brief(310, T0.AddMinutes(32), lost: 22)));
        Assert.Equal("1 lost packet (router)", PingReport.SessionLine(Brief(5, lost: 1), Brief(30)));
        Assert.Equal("3 lost packets (router 1, internet 2)", PingReport.SessionLine(Brief(5, lost: 1), Brief(30, lost: 2)));
    }

    [Fact]
    public void SessionLineWorksForEntriesSavedBefore104()
    {
        // no Lost count and no time: the count is estimated from the percentage, and the time is simply left out
        string s = PingReport.SessionLine(Brief(5), Brief(310, lossPct: 1.8));
        Assert.Equal("Worst ping 310 ms (internet)  ·  22 lost packets (internet)", s);
    }

    [Fact]
    public void SessionEntryRoundTripsTheNewFields()
    {
        var e = new SessionEntry { Internet = Brief(310, T0, lost: 4) };
        var json = System.Text.Json.JsonSerializer.Serialize(e);
        var back = System.Text.Json.JsonSerializer.Deserialize<SessionEntry>(json)!;
        Assert.Equal(4, back.Internet.Lost);
        Assert.Equal(T0, back.Internet.MaxAt);
        var old = System.Text.Json.JsonSerializer.Deserialize<SessionEntry>("{\"Internet\":{\"Max\":88,\"Count\":10}}")!;
        Assert.Null(old.Internet.MaxAt);
        Assert.Equal(0, old.Internet.Lost);
    }

    [Fact]
    public void SessionRecorderRecordsWorstPingAndLostPackets()
    {
        var m = new PingMonitor();
        var now = DateTime.Now;
        m.Seed(new[] { new PingSample(now.AddSeconds(-30), 2, 20), new PingSample(now.AddSeconds(-20), 2, 940), new PingSample(now.AddSeconds(-10), null, 20) });
        var rec = new SessionRecorder();
        rec.Begin(new GameInfo { Name = "Test" });
        typeof(SessionRecorder).GetField("_n", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(rec, 5);
        typeof(SessionRecorder).GetField("_start", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(rec, now.AddMinutes(-5));
        var e = rec.End(m, 0)!;
        Assert.Equal(940, e.Internet.Max);
        Assert.Equal(now.AddSeconds(-20), e.Internet.MaxAt);
        Assert.Equal(1, e.Router.Lost);
        Assert.Contains("Worst ping 940 ms", PingReport.SessionLine(e.Router, e.Internet));
    }
}
