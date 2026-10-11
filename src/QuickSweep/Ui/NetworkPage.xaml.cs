using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickSweep.Core;

namespace QuickSweep.Ui;

/// <summary>Latency line with a spike threshold and red marks for lost packets.</summary>
public sealed class LatencyChart : FrameworkElement
{
    IReadOnlyList<double?> _v = Array.Empty<double?>();
    public void SetData(IReadOnlyList<double?> v) { _v = v; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Palette.Frozen("#1A1B20"), null, new Rect(0, 0, w, h), 6, 6);
        if (_v.Count < 2) return;
        double top = Math.Max(120, Math.Min(500, _v.Where(x => x != null).Select(x => x!.Value).DefaultIfEmpty(0).Max() * 1.15));
        double Y(double ms) => h - 6 - Math.Min(ms, top) / top * (h - 12);
        double spikeY = Y(PingMonitor.SpikeMs);
        var dash = new Pen(Palette.Frozen("#5A4A1E"), 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(dash, new Point(0, spikeY), new Point(w, spikeY));
        var ft = new FormattedText("100 ms", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Palette.Frozen("#8A7A44"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(6, spikeY - 13));
        int n = _v.Count;
        double dx = w / (n - 1);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < n; i++)
            {
                if (_v[i] is not double ms) { open = false; continue; }
                var p = new Point(i * dx, Y(ms));
                if (!open) { c.BeginFigure(p, false, false); open = true; } else c.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Palette.Accent, 1.6) { LineJoin = PenLineJoin.Round }, geo);
        for (int i = 0; i < n; i++)
        {
            if (_v[i] is double ms)
            {
                if (ms >= PingMonitor.SpikeMs) dc.DrawEllipse(Palette.Warn, null, new Point(i * dx, Y(ms)), 2.6, 2.6);
            }
            else dc.DrawRectangle(Palette.Bad, null, new Rect(i * dx - 1.5, h - 10, 3, 8));
        }
    }
}

public partial class NetworkPage : UserControl, IPage
{
    readonly SystemMonitor _mon;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public NetworkPage(SystemMonitor mon)
    {
        InitializeComponent();
        _mon = mon;
        _timer.Tick += (_, _) => Update();
    }

    public void OnShown() { Update(); _timer.Start(); }
    public void OnHidden() => _timer.Stop();

    static string Line(PingStats s) => s.Count == 0 ? "no data yet"
        : $"avg {s.Avg:0} ms  ·  max {s.Max:0} ms  ·  loss {s.LossPct:0.#}%  ·  {s.Spikes} spike{(s.Spikes == 1 ? "" : "s")}";

    void Update()
    {
        var hist = _mon.Ping.History(DateTime.Now.AddMinutes(-10));
        RouterChart.SetData(hist.Select(s => s.RouterMs).ToList());
        NetChart.SetData(hist.Select(s => s.NetMs).ToList());
        var (r, n) = PingMonitor.Summarize(hist);
        string rt = string.IsNullOrEmpty(_mon.Ping.RouterAddress) ? "Router" : $"Router ({_mon.Ping.RouterAddress})";
        RouterTitle.Text = rt;
        RouterStats.Text = Line(r); NetStats.Text = Line(n);
        if (!_mon.Settings.PingEnabled) { Verdict.Text = "Ping monitor is switched off (Settings)."; Verdict.Foreground = Palette.Muted; VerdictSub.Text = ""; return; }
        if (hist.Count < 5) { Verdict.Text = "Collecting data..."; Verdict.Foreground = Palette.Muted; VerdictSub.Text = ""; return; }
        bool routerBad = r.Spikes >= 3 || r.LossPct >= 2, netBad = n.Spikes >= 3 || n.LossPct >= 2;
        if (!routerBad && !netBad) { Verdict.Text = "The connection looks steady."; Verdict.Foreground = Palette.Good; VerdictSub.Text = "No lag spikes or packet loss in the last 10 minutes. If a game still lags, look at the PC (Sessions page) or the game servers."; }
        else if (!routerBad) { Verdict.Text = "Your network is fine; the internet side is struggling."; Verdict.Foreground = Palette.Warn; VerdictSub.Text = "Replies from your router are steady, but 1.1.1.1 spikes. That points to your provider or the route, not your PC or Wi-Fi."; }
        else { Verdict.Text = "The trouble starts inside your own network."; Verdict.Foreground = Palette.Bad; VerdictSub.Text = "Even your router answers late or drops packets. Check the cable or Wi-Fi, the router, or something saturating the line (downloads, torrents, cloud sync)."; }
    }
}
