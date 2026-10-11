using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuickSweep.Core.Cleaner;

namespace QuickSweep.Ui;

/// <summary>Squarified treemap of one folder: sub-folders as tiles, big loose files as their own tiles.</summary>
public sealed class TreemapView : FrameworkElement
{
    sealed record Tile(Rect Rect, DirNode? Node, string Label, long Size, string Path);

    List<Tile> _tiles = new();
    DirNode? _node;
    int _hover = -1;
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public event Action<DirNode>? Drill;

    public TreemapView() { ClipToBounds = true; Cursor = Cursors.Arrow; }

    public void SetNode(DirNode? n) { _node = n; Relayout(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Relayout(); }

    void Relayout()
    {
        _tiles = new();
        _hover = -1;
        if (_node != null && ActualWidth > 20 && ActualHeight > 20)
        {
            var items = new List<(double size, Tile seed)>();
            foreach (var c in _node.Children.Where(c => c.Size > 0))
                items.Add((c.Size, new Tile(default, c, c.Name, c.Size, c.FullPath)));
            long listed = 0;
            foreach (var (name, size) in _node.BigFiles.Take(12))
            {
                if (size < _node.Size / 150) continue;
                listed += size;
                items.Add((size, new Tile(default, null, name, size, System.IO.Path.Combine(_node.FullPath, name))));
            }
            long rest = _node.OwnBytes - listed;
            if (rest > _node.Size / 200) items.Add((rest, new Tile(default, null, "(files in this folder)", rest, _node.FullPath)));
            items.Sort((a, b) => b.size.CompareTo(a.size));
            Squarify(items, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        InvalidateVisual();
    }

    void Squarify(List<(double size, Tile seed)> items, Rect r)
    {
        double total = items.Sum(i => i.size);
        if (total <= 0) return;
        double scale = r.Width * r.Height / total;
        int i = 0;
        while (i < items.Count)
        {
            double side = Math.Min(r.Width, r.Height);
            var row = new List<int>();
            double sum = 0, rmin = double.MaxValue, rmax = 0, worst = double.MaxValue;
            int j = i;
            while (j < items.Count)
            {
                double a = items[j].size * scale;
                double s2 = sum + a, mn = Math.Min(rmin, a), mx = Math.Max(rmax, a);
                double w = Math.Max(side * side * mx / (s2 * s2), s2 * s2 / (side * side * mn));
                if (row.Count > 0 && w > worst) break;
                row.Add(j); sum = s2; rmin = mn; rmax = mx; worst = w; j++;
            }
            // lay the row along the short side
            if (r.Width >= r.Height)
            {
                double colW = sum / r.Height, y = r.Top;
                foreach (var k in row) { double h = items[k].size * scale / colW; Add(items[k].seed, new Rect(r.Left, y, colW, h)); y += h; }
                r = new Rect(r.Left + colW, r.Top, Math.Max(0, r.Width - colW), r.Height);
            }
            else
            {
                double rowH = sum / r.Width, x = r.Left;
                foreach (var k in row) { double w = items[k].size * scale / rowH; Add(items[k].seed, new Rect(x, r.Top, w, rowH)); x += w; }
                r = new Rect(r.Left, r.Top + rowH, r.Width, Math.Max(0, r.Height - rowH));
            }
            i = j;
        }
    }

    void Add(Tile seed, Rect rect) => _tiles.Add(seed with { Rect = rect });

    static Color ColorFor(Tile t, bool hover)
    {
        int h = (t.Label.GetHashCode() & 0x7FFFFFFF) % 360;
        if (t.Node == null) h = 215;           // loose files: steel blue
        double s = t.Node == null ? 0.28 : 0.42, l = hover ? 0.46 : 0.36;
        return FromHsl(h, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Palette.Frozen("#1A1B20"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_tiles.Count == 0)
        {
            Text(dc, _node == null ? "Scan a drive or folder to see where the space went." : "Nothing here.", 12, Palette.Muted, new Point(14, 14), ActualWidth - 28);
            return;
        }
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            var rc = new Rect(t.Rect.X + 1, t.Rect.Y + 1, Math.Max(0, t.Rect.Width - 2), Math.Max(0, t.Rect.Height - 2));
            if (rc.Width < 1 || rc.Height < 1) continue;
            dc.DrawRoundedRectangle(new SolidColorBrush(ColorFor(t, i == _hover)), null, rc, 3, 3);
            if (rc.Width > 64 && rc.Height > 30)
            {
                Text(dc, t.Label, 12, Brushes.White, new Point(rc.X + 6, rc.Y + 4), rc.Width - 12);
                if (rc.Height > 46) Text(dc, Palette.Bytes(t.Size), 11, Palette.Frozen("#D4D4DC"), new Point(rc.X + 6, rc.Y + 21), rc.Width - 12);
            }
        }
    }

    void Text(DrawingContext dc, string s, double size, Brush b, Point p, double maxWidth)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(10, maxWidth), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, p);
    }

    int HitTest(Point p) { for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].Rect.Contains(p)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.GetPosition(this));
        if (h != _hover)
        {
            _hover = h;
            if (h >= 0) { var t = _tiles[h]; ToolTip = $"{t.Path}\n{Palette.Bytes(t.Size)}" + (t.Node != null ? $"  ·  {t.Node.Files:N0} files" : ""); Cursor = t.Node is { Children.Count: > 0 } ? Cursors.Hand : Cursors.Arrow; }
            else ToolTip = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int h = HitTest(e.GetPosition(this));
        if (h >= 0 && _tiles[h].Node is { Children.Count: > 0 } n) Drill?.Invoke(n);
    }
}
