using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickSweep.Native;

namespace QuickSweep.Ui;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public static class Palette
{
    public static readonly Brush Good = Frozen("#6CCB5F");
    public static readonly Brush Warn = Frozen("#FFC83D");
    public static readonly Brush Hot = Frozen("#FF9A4D");
    public static readonly Brush Bad = Frozen("#FF6B6B");
    public static readonly Brush Accent = Frozen("#60CDFF");
    public static readonly Brush Muted = Frozen("#A6A6B0");
    public static readonly Brush Text = Frozen("#F2F2F5");
    public static readonly Brush Track = Frozen("#3A3A42");

    public static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static Brush Heat(double? v, double warm, double hot, double crit)
        => v == null ? Muted : v >= crit ? Bad : v >= hot ? Hot : v >= warm ? Warn : Good;

    public static string Bytes(long b)
    {
        double g = b / 1073741824.0;
        if (g >= 100) return $"{g:0} GB";
        if (g >= 1) return $"{g:0.0} GB";
        return $"{b / 1048576.0:0} MB";
    }
}

/// <summary>Thin rounded progress bar drawn in one pass.</summary>
public sealed class BarMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(BarMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(BarMeter),
        new FrameworkPropertyMetadata(Palette.Accent, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public BarMeter() { Height = 6; MinHeight = 6; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        dc.DrawRoundedRectangle(Palette.Track, null, new Rect(0, 0, w, h), r, r);
        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0) return;
        double fw = Math.Max(h, w * v);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, fw, h), r, r);
    }
}

/// <summary>One horizontal bar split into coloured segments (physical memory breakdown).</summary>
public sealed class StackBar : FrameworkElement
{
    IReadOnlyList<(double value, Brush brush)> _seg = Array.Empty<(double, Brush)>();
    public StackBar() { Height = 18; }
    public void SetSegments(IReadOnlyList<(double value, Brush brush)> seg) { _seg = seg; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, total = _seg.Sum(s => Math.Max(0, s.value));
        dc.DrawRoundedRectangle(Palette.Track, null, new Rect(0, 0, w, h), 5, 5);
        if (total <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 5, 5));
        double x = 0;
        foreach (var (v, b) in _seg)
        {
            if (v <= 0) continue;
            double sw = w * v / total;
            dc.DrawRectangle(b, null, new Rect(x, 0, Math.Max(0, sw - 1.5), h));
            x += sw;
        }
        dc.Pop();
    }
}

/// <summary>Tiny line chart for a rolling series.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Palette.Accent, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Min { get; set; } = 20;
    public double Max { get; set; } = 100;
    IReadOnlyList<double>? _pts;

    public void SetData(IReadOnlyList<double> pts) { _pts = pts; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var p = _pts;
        double w = ActualWidth, h = ActualHeight;
        if (p == null || p.Count < 2 || w < 4) return;
        double lo = Math.Min(Min, p.Min()), hi = Math.Max(Max, p.Max());
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int i = 0; i < p.Count; i++)
            {
                double x = w * i / (p.Count - 1);
                double y = h - (p[i] - lo) / (hi - lo) * (h - 2) - 1;
                if (i == 0) c.BeginFigure(new Point(x, y), false, false); else c.LineTo(new Point(x, y), true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }
}

public static class IconCache
{
    static readonly string[] DemoColors = { "#4C8DFF", "#7C5CFF", "#2FB38A", "#E0803A", "#D8587A", "#3FA7C9", "#8F9B3A", "#B0679E" };

    /// <summary>Demo mode: a plain rounded square with the first letter instead of a real program icon.</summary>
    static ImageSource DemoIcon(string name)
    {
        int hash = 0; foreach (char ch in name) hash = hash * 31 + ch;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(Palette.Frozen(DemoColors[Math.Abs(hash) % DemoColors.Length]), null, new Rect(1, 1, 30, 30), 7, 7);
            var ft = new FormattedText(name[..1].ToUpperInvariant(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 17, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(16 - ft.Width / 2, 16 - ft.Height / 2));
        }
        var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var c)) return c;
        if (path.StartsWith("demo:", StringComparison.Ordinal)) return Cache[path] = DemoIcon(path[5..]);
        ImageSource? img = null;
        try
        {
            var fi = new Win.SHFILEINFO();
            // SHGFI_ICON | SHGFI_SMALLICON
            if (Win.SHGetFileInfoW(path, 0, ref fi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win.SHFILEINFO>(), 0x100 | 0x1) != 0 && fi.hIcon != 0)
            {
                img = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                Win.DestroyIcon(fi.hIcon);
            }
        }
        catch { }
        Cache[path] = img;
        return img;
    }
}
