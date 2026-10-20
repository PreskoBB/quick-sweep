using System.Globalization;

namespace QuickSweep.Core;

/// <summary>Pure helpers behind the Network page charts and the session summary: scale, clipping, hover lookup, text.</summary>
public static class PingReport
{
    /// <summary>The chart never draws above this latency; slower replies are clipped to the top edge.</summary>
    public const double ChartCapMs = 500;
    public const double ChartMinTopMs = 120;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Top of the vertical scale for a set of readings: 15 % above the slowest, between 120 and 500 ms.</summary>
    public static double Ceiling(double? slowestMs)
        => Math.Max(ChartMinTopMs, Math.Min(ChartCapMs, (slowestMs ?? 0) * 1.15));

    public static double Ceiling(IEnumerable<double?> values)
        => Ceiling(values.Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(0).Max());

    /// <summary>True when the reading is above the top of the chart, so the line is drawn flattened at the edge.</summary>
    public static bool IsClipped(double ms, double ceiling) => ms > ceiling + 0.0001;

    /// <summary>Index of the sample whose dot is nearest to the horizontal position <paramref name="x"/>, or -1 with no data. Samples are spread evenly over <paramref name="width"/>.</summary>
    public static int NearestIndex(double x, double width, int count)
    {
        if (count <= 0 || width <= 0 || double.IsNaN(x)) return -1;
        if (count == 1) return 0;
        double dx = width / (count - 1);
        return Math.Clamp((int)Math.Round(x / dx), 0, count - 1);
    }

    public static string Ms(double ms) => ms < 1 ? "<1 ms" : $"{ms.ToString("#,##0", Inv)} ms";

    /// <summary>Tooltip text for one sample: local time with seconds and the true latency (or "lost").</summary>
    public static string[] Tooltip(DateTime t, double? ms, double ceiling)
    {
        string head = t.ToString("HH:mm:ss", Inv);
        if (ms is not double v) return new[] { head + "  ·  lost" };
        return IsClipped(v, ceiling)
            ? new[] { head + "  ·  " + Ms(v), "above the chart (drawn at the " + ceiling.ToString("0", Inv) + " ms edge)" }
            : new[] { head + "  ·  " + Ms(v) };
    }

    /// <summary>Stats text above a chart, for example "now 18 ms  ·  peak 412 ms at 21:14  ·  avg 17 ms  ·  loss 2.1% (4 lost)  ·  3 spikes".</summary>
    public static string StatsLine(PingStats s)
    {
        if (s.Count == 0) return "no data yet";
        var parts = new List<string> { s.LastMs is double n ? "now " + Ms(n) : "now lost" };
        if (s.Max > 0) parts.Add("peak " + Ms(s.Max) + (s.MaxAt is DateTime at ? " at " + at.ToString("HH:mm", Inv) : ""));
        parts.Add("avg " + Ms(s.Avg));
        if (s.Lost > 0) parts.Add($"loss {s.LossPct.ToString("0.#", Inv)}% ({s.Lost} lost)");
        parts.Add($"{s.Spikes} spike{(s.Spikes == 1 ? "" : "s")}");
        return string.Join("  ·  ", parts);
    }

    /// <summary>Packets lost in a session. Entries saved before 1.0.4 have only the percentage, so the count is estimated from it.</summary>
    public static int LostCount(PingBrief b)
        => b.Lost > 0 ? b.Lost : b.LossPct > 0 && b.Count > 0 ? (int)Math.Max(1, Math.Round(b.LossPct * b.Count / 100.0)) : 0;

    /// <summary>
    /// One line for the Sessions page: the worst reply (value, time, which target) and the lost packets, for example
    /// "Worst ping 310 ms at 21:42 (internet)  ·  3 lost packets (internet)". Empty when the game had no spike and no loss.
    /// </summary>
    public static string SessionLine(PingBrief router, PingBrief internet)
    {
        var worst = internet.Max >= router.Max ? (b: internet, who: "internet") : (b: router, who: "router");
        int lr = LostCount(router), li = LostCount(internet);
        var parts = new List<string>();
        if (worst.b.Max >= PingMonitor.SpikeMs)
            parts.Add("Worst ping " + Ms(worst.b.Max) + (worst.b.MaxAt is DateTime at ? " at " + at.ToString("HH:mm", Inv) : "") + " (" + worst.who + ")");
        int lost = lr + li;
        if (lost > 0)
        {
            string who = lr > 0 && li > 0 ? $"router {lr}, internet {li}" : lr > 0 ? "router" : "internet";
            parts.Add($"{lost} lost packet{(lost == 1 ? "" : "s")} ({who})");
        }
        return string.Join("  ·  ", parts);
    }
}
