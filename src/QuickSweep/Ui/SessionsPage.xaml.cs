using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickSweep.Core;

namespace QuickSweep.Ui;

public sealed class SessionVM
{
    public string Game { get; set; } = "";
    public string When { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string CpuTemp { get; set; } = "";
    public string GpuTemp { get; set; } = "";
    public string Loads { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Memory { get; set; } = "";
    public string Throttle { get; set; } = "";
    public string RouterLine { get; set; } = "";
    public string NetLine { get; set; } = "";
    public string Extra { get; set; } = "";
    public Brush VerdictBrush { get; set; } = Palette.Good;
    public Visibility ExtraVis => string.IsNullOrEmpty(Extra) ? Visibility.Collapsed : Visibility.Visible;

    public static SessionVM From(SessionEntry e)
    {
        var v = new SessionVM
        {
            Game = e.Game,
            When = FormatWhen(e.Start),
            Duration = FormatDuration(e.DurationSec),
            CpuTemp = e.CpuTempPeak is double cp ? $"{cp:0} °C peak · {e.CpuTempAvg:0} avg" : "not measured (no admin driver)",
            GpuTemp = $"{e.GpuTempPeak:0} °C peak · {e.GpuTempAvg:0} avg",
            Loads = $"CPU {e.CpuLoadPeak:0}% · GPU {e.GpuLoadPeak:0}%",
            Memory = $"RAM {e.RamPeakGb:0.0} GB ({e.RamPeakPct:0}%)" + (e.VramPeakGb > 0 ? $" · VRAM {e.VramPeakGb:0.0} GB" : ""),
            Throttle = (e.CpuThrottled || e.GpuThrottled)
                ? string.Join(", ", new[] { e.CpuThrottled ? "CPU hit the 95 °C limit" : null, e.GpuThrottled ? $"GPU slowed by heat ({e.GpuThrottleSec} s)" : null }.Where(s => s != null))
                : "None",
            RouterLine = Ping(e.Router),
            NetLine = Ping(e.Internet)
        };
        bool hot = (e.CpuTempPeak ?? 0) >= 90 || e.GpuTempPeak >= 83;
        if (e.CpuThrottled || e.GpuThrottled) { v.Verdict = "Thermal throttling detected: the PC slowed itself down to stay cool."; v.VerdictBrush = Palette.Bad; }
        else if (hot) { v.Verdict = "Ran hot, but did not throttle."; v.VerdictBrush = Palette.Warn; }
        else if (e.Internet.Spikes >= 10 || e.Internet.LossPct >= 2) { v.Verdict = "PC was fine; the connection had trouble."; v.VerdictBrush = Palette.Warn; }
        else { v.Verdict = "Healthy: cool, no throttling, steady connection."; v.VerdictBrush = Palette.Good; }
        var extra = new List<string>();
        if (e.Alerts.Count > 0) extra.Add("Alerts during play: " + string.Join("; ", e.Alerts));
        if (e.GameModeLog.Count > 0) extra.Add("Game Mode (priority only): " + string.Join("; ", e.GameModeLog) + ".");
        else if (e.BackgroundLowered > 0) extra.Add($"Game Mode slowed {e.BackgroundLowered} background job{(e.BackgroundLowered == 1 ? "" : "s")} (priority only).");
        v.Extra = string.Join("\n", extra);
        return v;
    }

    static string Ping(PingBrief p)
        => p.Count == 0 ? "no data" : $"avg {p.Avg:0} ms · max {p.Max:0} ms · loss {p.LossPct:0.#}% · {p.Spikes} spike{(p.Spikes == 1 ? "" : "s")}";

    static string FormatDuration(int sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min {t.Seconds} s";
    }

    static string FormatWhen(DateTime d)
    {
        if (d.Date == DateTime.Today) return "Today " + d.ToString("HH:mm");
        if (d.Date == DateTime.Today.AddDays(-1)) return "Yesterday " + d.ToString("HH:mm");
        return d.ToString("ddd d MMM HH:mm");
    }
}

public partial class SessionsPage : UserControl, IPage
{
    readonly SystemMonitor _mon;
    bool _pending;

    public SessionsPage(SystemMonitor mon)
    {
        InitializeComponent();
        _mon = mon;
        Load();
    }

    public void OnShown() { Load(); _mon.Sampled += OnSampled; _mon.SessionSaved += OnSaved; UpdateLive(); }
    public void OnHidden() { _mon.Sampled -= OnSampled; _mon.SessionSaved -= OnSaved; }

    void OnSaved(SessionEntry e) => Dispatcher.BeginInvoke(Load);
    void OnSampled(Telemetry t)
    {
        if (_pending) return; _pending = true;
        Dispatcher.BeginInvoke(() => { _pending = false; UpdateLive(); });
    }

    void UpdateLive()
    {
        if (_mon.Recorder.Recording)
        {
            LiveCard.Visibility = Visibility.Visible;
            var el = DateTime.Now - _mon.Recorder.Started;
            LiveTitle.Text = $"Recording: {_mon.Recorder.GameName}";
            LiveLine.Text = $"{(int)el.TotalMinutes} min so far. A summary is saved when the game closes; nothing pops up while you play.";
        }
        else LiveCard.Visibility = Visibility.Collapsed;
    }

    void Load()
    {
        var items = SessionRecorder.Load().Take(20).Select(SessionVM.From).ToList();
        List.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
