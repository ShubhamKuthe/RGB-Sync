using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RGBSync.Core;

namespace RGBSync.Controls;

/// <summary>
/// The room LED strip, drawn the way it is laid out in Smart Life: a zig-zag strip with rounded bends,
/// split into sections along its length. Each section glows in its own colour and lights up the "wall"
/// behind it. Colours glide to their new value instead of jumping, and tapping a section paints it with a ripple.
/// </summary>
public sealed class StripView : FrameworkElement
{
    private Rgb[] _target = { new(242, 242, 239) };
    private double[] _r = Array.Empty<double>(), _g = Array.Empty<double>(), _b = Array.Empty<double>();
    private double[] _pop = Array.Empty<double>();          // per-section "just painted" bump, decays to 0
    private int _segments = 20;
    private int _hover = -1;
    private bool _painting;
    private TimeSpan _last;
    private bool _first = true;

    // geometry cache
    private Size _builtFor;
    private int _builtSegs;
    private Point[] _pts = Array.Empty<Point>();            // centre line, sampled every ~2px
    private double[] _dist = Array.Empty<double>();
    private Geometry _whole = Geometry.Empty;
    private Geometry[] _segGeo = Array.Empty<Geometry>();
    private Point[][] _segPts = Array.Empty<Point[]>();
    private Point[] _segMid = Array.Empty<Point>();
    private double _tube = 20, _rowGap = 60;

    private readonly List<(Point at, double age)> _ripples = new();

    public event Action<int>? SegmentPainted;
    public bool Editable { get; set; }

    public int Segments
    {
        get => _segments;
        set
        {
            int v = Math.Clamp(value, 1, 64);
            if (v == _segments) return;
            _segments = v;
            InvalidateVisual();
        }
    }

    public StripView()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        ClipToBounds = false;
    }

    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return;
        if (_target.Length != colors.Count) _target = new Rgb[colors.Count];
        for (int i = 0; i < colors.Count; i++) _target[i] = colors[i];
    }

    private Rgb TargetFor(int i) => _target[(i * _target.Length / Math.Max(1, _segments)) % _target.Length];

    // ------------------------------------------------------------------ animation

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 0.016 : Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;

        if (_r.Length != _segments)
        {
            Array.Resize(ref _r, _segments);
            Array.Resize(ref _g, _segments);
            Array.Resize(ref _b, _segments);
            Array.Resize(ref _pop, _segments);
            _first = true;
        }

        // colours glide towards their target (~70 ms) - smooth, but still keeps up with the beat
        double k = _first ? 1 : 1 - Math.Exp(-dt * 15);
        _first = false;
        for (int i = 0; i < _segments; i++)
        {
            var t = TargetFor(i);
            _r[i] += (t.R - _r[i]) * k;
            _g[i] += (t.G - _g[i]) * k;
            _b[i] += (t.B - _b[i]) * k;
            _pop[i] *= Math.Exp(-dt * 7);
        }
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var (at, age) = _ripples[i];
            age += dt;
            if (age > 0.6) _ripples.RemoveAt(i);
            else _ripples[i] = (at, age);
        }
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ layout

    private static int RowsFor(int n) => n >= 16 ? 4 : n >= 8 ? 3 : n >= 4 ? 2 : 1;

    private void Build()
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size == _builtFor && _builtSegs == _segments) return;
        _builtFor = size;
        _builtSegs = _segments;

        int rows = RowsFor(_segments);
        double m = 26;
        double h = Math.Max(40, size.Height - 2 * m);
        _rowGap = rows > 1 ? Math.Min(h / (rows - 1), 92) : 0;
        double top = (size.Height - _rowGap * (rows - 1)) / 2;
        double rad = _rowGap / 2;
        double x0 = m + rad, x1 = Math.Max(x0 + 40, size.Width - m - rad);
        _tube = Math.Clamp(_rowGap * 0.4, 14, 30);
        if (rows == 1) _tube = Math.Clamp(size.Height * 0.12, 14, 26);

        // centre line: straight runs joined by half-circle bends, sampled finely
        var pts = new List<Point>();
        void Line(Point a, Point b)
        {
            double len = (b - a).Length;
            int n = Math.Max(2, (int)(len / 2));
            for (int i = 0; i < n; i++) pts.Add(a + (b - a) * (i / (double)n));
        }
        void Bend(Point centre, double r, bool right)
        {
            int n = Math.Max(8, (int)(Math.PI * r / 2));
            for (int i = 0; i < n; i++)
            {
                double a = -Math.PI / 2 + Math.PI * i / n;      // top -> bottom
                double x = right ? centre.X + r * Math.Cos(a) : centre.X - r * Math.Cos(a);
                pts.Add(new Point(x, centre.Y + r * Math.Sin(a)));
            }
        }
        for (int row = 0; row < rows; row++)
        {
            double y = top + row * _rowGap;
            bool ltr = row % 2 == 0;
            Line(new Point(ltr ? x0 : x1, y), new Point(ltr ? x1 : x0, y));
            if (row < rows - 1) Bend(new Point(ltr ? x1 : x0, y + rad), rad, ltr);
        }
        pts.Add(new Point(rows % 2 == 1 ? x1 : x0, top + (rows - 1) * _rowGap));

        _pts = pts.ToArray();
        _dist = new double[_pts.Length];
        for (int i = 1; i < _pts.Length; i++) _dist[i] = _dist[i - 1] + (_pts[i] - _pts[i - 1]).Length;
        double total = _dist[^1];

        _whole = Poly(0, _pts.Length - 1);
        _segGeo = new Geometry[_segments];
        _segPts = new Point[_segments][];
        _segMid = new Point[_segments];
        double gap = Math.Min(3.5, total / _segments * 0.08);
        for (int s = 0; s < _segments; s++)
        {
            double a = total * s / _segments + (s == 0 ? 0 : gap);
            double b = total * (s + 1) / _segments - (s == _segments - 1 ? 0 : gap);
            int ia = Index(a), ib = Math.Max(ia + 1, Index(b));
            _segGeo[s] = Poly(ia, ib);
            var list = new List<Point>();
            for (int i = ia; i <= ib; i += 3) list.Add(_pts[i]);
            _segPts[s] = list.ToArray();
            _segMid[s] = _pts[Index((a + b) / 2)];
        }
    }

    private int Index(double d)
    {
        int lo = 0, hi = _dist.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_dist[mid] < d) lo = mid + 1; else hi = mid;
        }
        return Math.Clamp(lo, 0, _pts.Length - 1);
    }

    private Geometry Poly(int from, int to)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(_pts[from], false, false);
            var list = new List<Point>(to - from);
            for (int i = from + 1; i <= to; i++) list.Add(_pts[i]);
            ctx.PolyLineTo(list, true, true);
        }
        g.Freeze();
        return g;
    }

    // ------------------------------------------------------------------ drawing

    private static Pen RoundPen(Brush b, double t)
    {
        var p = new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static SolidColorBrush Solid(byte a, double r, double g, double b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255)));
        br.Freeze();
        return br;
    }

    private static readonly Pen TubePen = RoundPen(Frozen(new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0C))), 1);
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 60 || ActualHeight < 40 || _r.Length != _segments) return;
        Build();
        double t = _tube;

        // what each section looks like to the eye: dimmed LEDs keep their colour, they just glow less
        var R = new double[_segments]; var G = new double[_segments]; var B = new double[_segments]; var L = new double[_segments];
        for (int i = 0; i < _segments; i++)
        {
            double mx = Math.Max(_r[i], Math.Max(_g[i], _b[i]));
            double lum = mx / 255.0;
            L[i] = Math.Sqrt(lum);
            double scale = mx < 1 ? 0 : (0.62 + 0.38 * lum) * 255 / mx;
            R[i] = _r[i] * scale; G[i] = _g[i] * scale; B[i] = _b[i] * scale;
        }

        // 1) light spilling onto the wall behind the strip
        for (int i = 0; i < _segments; i++)
        {
            if (L[i] < 0.1) continue;
            var glow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(38 * L[i]), (byte)R[i], (byte)G[i], (byte)B[i]), 0),
                    new GradientStop(Color.FromArgb((byte)(16 * L[i]), (byte)R[i], (byte)G[i], (byte)B[i]), 0.5),
                    new GradientStop(Color.FromArgb(0, (byte)R[i], (byte)G[i], (byte)B[i]), 1),
                },
            };
            glow.Freeze();
            double rr = Math.Max(_rowGap * 1.15, t * 4.2);
            dc.DrawEllipse(glow, null, _segMid[i], rr, rr);
        }

        // 2) the strip itself: a dark flexible tube
        dc.DrawGeometry(null, RoundPen(TubePen.Brush, t + 6), _whole);
        dc.DrawGeometry(null, RoundPen(Frozen(new SolidColorBrush(Color.FromArgb(22, 242, 242, 239))), t + 6.8), _whole);
        dc.DrawGeometry(null, RoundPen(TubePen.Brush, t + 5), _whole);

        // 3) each section: soft bloom, then the lit body, then a hot core
        for (int i = 0; i < _segments; i++)
        {
            double r = R[i], g = G[i], b = B[i];
            double lum = L[i];
            double bump = _pop[i] * 5;
            if (lum > 0.02)
            {
                dc.DrawGeometry(null, RoundPen(Solid((byte)(26 * lum), r, g, b), t * 2.6 + bump), _segGeo[i]);
                dc.DrawGeometry(null, RoundPen(Solid((byte)(48 * lum), r, g, b), t * 1.7 + bump), _segGeo[i]);
            }
            if (Editable && i == _hover)
                dc.DrawGeometry(null, RoundPen(Solid(210, 242, 242, 239), t + 3 + bump), _segGeo[i]);

            // unlit sections stay faintly visible so you can still see where they are
            double br = Math.Max(r, 26), bg = Math.Max(g, 26), bb = Math.Max(b, 26);
            if (lum < 0.1) { br = 26 + r * 0.6; bg = 26 + g * 0.6; bb = 26 + b * 0.6; }
            dc.DrawGeometry(null, RoundPen(Solid(255, br, bg, bb), t - 3 + bump), _segGeo[i]);
            if (lum > 0.05)
                dc.DrawGeometry(null, RoundPen(Solid((byte)(150 * lum), r + (255 - r) * 0.6, g + (255 - g) * 0.6, b + (255 - b) * 0.6), (t - 3) * 0.32), _segGeo[i]);
        }

        // 4) paint ripples
        foreach (var (at, age) in _ripples)
        {
            double p = age / 0.6;
            double ease = 1 - Math.Pow(1 - p, 3);
            var pen = new Pen(Solid((byte)(150 * (1 - p)), 242, 242, 239), 1.5);
            pen.Freeze();
            dc.DrawEllipse(null, pen, at, t * 0.6 + ease * t * 2.2, t * 0.6 + ease * t * 2.2);
        }
    }

    // ------------------------------------------------------------------ painting

    private int Hit(Point p)
    {
        if (_segPts.Length != _segments) return -1;
        int best = -1;
        double bestD = (_tube + 12) * (_tube + 12);
        for (int s = 0; s < _segPts.Length; s++)
            foreach (var q in _segPts[s])
            {
                double d = (q - p).LengthSquared;
                if (d < bestD) { bestD = d; best = s; }
            }
        return best;
    }

    private void Paint(int s)
    {
        if (s < 0 || s >= _segments) return;
        if (s < _pop.Length) _pop[s] = 1;
        if (s < _segMid.Length) _ripples.Add((_segMid[s], 0));
        SegmentPainted?.Invoke(s);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Editable) { _hover = -1; Cursor = Cursors.Arrow; return; }
        int h = Hit(e.GetPosition(this));
        if (_painting && h >= 0 && h != _hover) Paint(h);
        _hover = h;
        Cursor = _hover >= 0 ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Editable) return;
        int s = Hit(e.GetPosition(this));
        if (s < 0) return;
        _painting = true;
        CaptureMouse();
        Paint(s);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _painting = false;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _painting = false;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => new PointHitTestResult(this, p.HitPoint);
}
