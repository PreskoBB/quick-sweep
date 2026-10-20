using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickSweep.Core;

namespace QuickSweep.Ui;

/// <summary>
/// Latency line with a spike threshold, red marks for lost packets, up-arrows for replies above the chart, and a hover
/// readout (time and true latency of the nearest sample) that is drawn inside the chart so it can never leave the card.
/// </summary>
public sealed class LatencyChart : FrameworkElement
{
    static readonly Typeface Face = new("Segoe UI");
    static readonly Brush TipBg = Palette.Frozen("#2A2C34"), TipBorder = Palette.Frozen("#4A4E5A"), MarkerBrush = Palette.Frozen("#80F2F2F5");
    static readonly Pen MarkerPen = Freeze(new Pen(MarkerBrush, 1)), TipPen = Freeze(new Pen(TipBorder, 1)), DotRing = Freeze(new Pen(Palette.Frozen("#F2F2F5"), 1.6));
    static Pen Freeze(Pen p) { p.Freeze(); return p; }

    IReadOnlyList<DateTime> _t = Array.Empty<DateTime>();
    IReadOnlyList<double?> _v = Array.Empty<double?>();
    (IReadOnlyList<DateTime>, IReadOnlyList<double?>)? _held;   // newer data that arrived while the pointer was on the chart
    int _hover = -1;
    bool _pinned;

    /// <summary>Index of the sample under the pointer (or the demo pin); -1 when none.</summary>
    public int HoverIndex => _hover;

    /// <summary>Replaces the data. While the pointer is on the chart the new data is held back, so the point being read stays put until the pointer leaves.</summary>
    public void SetData(IReadOnlyList<DateTime> times, IReadOnlyList<double?> values)
    {
        if (_hover >= 0) { _held = (times, values); return; }
        _t = times; _v = values; InvalidateVisual();
    }

    /// <summary>Screenshots: shows the readout for a sample as if the pointer were on it.</summary>
    public void PinHover(int index) { _pinned = true; SetHover(index >= 0 && index < _v.Count ? index : -1); }
    public int IndexOfMax() { int best = -1; double m = -1; for (int i = 0; i < _v.Count; i++) if (_v[i] is double d && d > m) { m = d; best = i; } return best; }
    public int IndexOfFirstLost() { for (int i = 0; i < _v.Count; i++) if (_v[i] == null) return i; return -1; }

    void SetHover(int i)
    {
        if (i == _hover) return;           // same sample: no redraw, so no flicker
        _hover = i;
        if (i < 0 && _held is { } h) { _t = h.Item1; _v = h.Item2; _held = null; }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_pinned) return;
        SetHover(PingReport.NearestIndex(e.GetPosition(this).X, ActualWidth, _v.Count));
    }
    protected override void OnMouseLeave(MouseEventArgs e) { if (!_pinned) SetHover(-1); }

    FormattedText Text(string s, double size, Brush b)
        => new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Palette.Frozen("#1A1B20"), null, new Rect(0, 0, w, h), 6, 6);   // also what the pointer hits
        if (_v.Count < 2) return;
        double top = PingReport.Ceiling(_v);
        double Y(double ms) => h - 6 - Math.Min(ms, top) / top * (h - 12);
        double spikeY = Y(PingMonitor.SpikeMs);
        var dash = new Pen(Palette.Frozen("#5A4A1E"), 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(dash, new Point(0, spikeY), new Point(w, spikeY));
        dc.DrawText(Text("100 ms", 10, Palette.Frozen("#8A7A44")), new Point(6, spikeY - 13));
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
        double lastLabelRight = -100;
        for (int i = 0; i < n; i++)
        {
            double x = i * dx;
            if (_v[i] is double ms)
            {
                if (PingReport.IsClipped(ms, top))
                {
                    // above the chart: an up-arrow at the top edge and the real value beside it (a 2,000 ms reply reads differently from a 510 ms one)
                    var tri = new StreamGeometry();
                    using (var c = tri.Open()) { c.BeginFigure(new Point(x, 1.5), true, true); c.LineTo(new Point(x + 4.5, 9), true, false); c.LineTo(new Point(x - 4.5, 9), true, false); }
                    tri.Freeze();
                    dc.DrawGeometry(Palette.Bad, null, tri);
                    var lab = Text(PingReport.Ms(ms), 10, Palette.Bad);
                    double lx = x + 7 + lab.Width > w - 2 ? x - 7 - lab.Width : x + 7;
                    if (lx > lastLabelRight + 4 && lx >= 2) { dc.DrawText(lab, new Point(lx, -0.5)); lastLabelRight = lx + lab.Width; }
                }
                else if (ms >= PingMonitor.SpikeMs) dc.DrawEllipse(Palette.Warn, null, new Point(x, Y(ms)), 2.6, 2.6);
            }
            else dc.DrawRectangle(Palette.Bad, null, new Rect(x - 1.5, h - 10, 3, 8));
        }
        if (_hover >= 0 && _hover < n && _hover < _t.Count) DrawHover(dc, _hover, dx * _hover, w, h, top, Y);
    }

    void DrawHover(DrawingContext dc, int i, double x, double w, double h, double top, Func<double, double> Y)
    {
        dc.DrawLine(MarkerPen, new Point(x, 0), new Point(x, h));
        double? ms = _v[i];
        double dotY = ms is double m ? Y(m) : h - 6;
        if (ms is double mm && PingReport.IsClipped(mm, top)) dotY = 15;      // just under the arrow
        dc.DrawEllipse(ms == null ? Palette.Bad : ms >= PingMonitor.SpikeMs ? Palette.Warn : Palette.Accent, DotRing, new Point(x, dotY), 4.2, 4.2);

        var lines = PingReport.Tooltip(_t[i], ms, top);
        var ft = lines.Select((l, k) => Text(l, k == 0 ? 12 : 10.5, k == 0 ? Palette.Text : Palette.Muted)).ToList();
        double tw = ft.Max(f => f.Width) + 16, th = ft.Sum(f => f.Height) + 10;
        double bx = x + 12 + tw <= w - 3 ? x + 12 : x - 12 - tw;                          // beside the marker, flipped at the right edge
        bx = Math.Clamp(bx, 3, Math.Max(3, w - tw - 3));
        double by = Math.Clamp(dotY - th / 2, 3, Math.Max(3, h - th - 3));
        dc.DrawRoundedRectangle(TipBg, TipPen, new Rect(bx, by, tw, th), 5, 5);
        double ty = by + 5;
        foreach (var f in ft) { dc.DrawText(f, new Point(bx + 8, ty)); ty += f.Height; }
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

    /// <summary>Screenshots: puts the hover readout on the first lost packet of the router chart and the worst reply of the internet chart.</summary>
    public void DemoHover()
    {
        Update();
        int lost = RouterChart.IndexOfFirstLost();
        RouterChart.PinHover(lost >= 0 ? lost : RouterChart.IndexOfMax());
        NetChart.PinHover(NetChart.IndexOfMax());
    }

    void Update()
    {
        var hist = _mon.Ping.History(DateTime.Now.AddMinutes(-10));
        var times = hist.Select(s => s.T).ToList();
        RouterChart.SetData(times, hist.Select(s => s.RouterMs).ToList());
        NetChart.SetData(times, hist.Select(s => s.NetMs).ToList());
        var (r, n) = PingMonitor.Summarize(hist);
        string rt = string.IsNullOrEmpty(_mon.Ping.RouterAddress) ? "Router" : $"Router ({_mon.Ping.RouterAddress})";
        RouterTitle.Text = rt;
        RouterStats.Text = PingReport.StatsLine(r); NetStats.Text = PingReport.StatsLine(n);
        if (!_mon.Settings.PingEnabled) { Verdict.Text = "Ping monitor is switched off (Settings)."; Verdict.Foreground = Palette.Muted; VerdictSub.Text = ""; return; }
        if (hist.Count < 5) { Verdict.Text = "Collecting data..."; Verdict.Foreground = Palette.Muted; VerdictSub.Text = ""; return; }
        bool routerBad = r.Spikes >= 3 || r.LossPct >= 2, netBad = n.Spikes >= 3 || n.LossPct >= 2;
        if (!routerBad && !netBad) { Verdict.Text = "The connection looks steady."; Verdict.Foreground = Palette.Good; VerdictSub.Text = "No lag spikes or packet loss in the last 10 minutes. If a game still lags, look at the PC (Sessions page) or the game servers."; }
        else if (!routerBad) { Verdict.Text = "Your network is fine; the internet side is struggling."; Verdict.Foreground = Palette.Warn; VerdictSub.Text = "Replies from your router are steady, but 1.1.1.1 spikes. That points to your provider or the route, not your PC or Wi-Fi."; }
        else { Verdict.Text = "The trouble starts inside your own network."; Verdict.Foreground = Palette.Bad; VerdictSub.Text = "Even your router answers late or drops packets. Check the cable or Wi-Fi, the router, or something saturating the line (downloads, torrents, cloud sync)."; }
    }
}
