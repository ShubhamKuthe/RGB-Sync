using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using RGBSync.Core;

namespace RGBSync.Controls;

/// <summary>
/// An animated picture of a 120mm ARGB fan: spinning translucent blades, a glowing LED ring around the hub.
/// In edit mode every LED segment can be tapped (or dragged across) to paint it.
/// </summary>
public sealed class FanView : FrameworkElement
{
    private Rgb[] _colors = { new(43, 217, 255) };
    private int _segments = 12;
    private double _angle;
    private TimeSpan _lastRender;
    private int _hover = -1;
    private bool _painting;
    private double _size;
    private Geometry[] _segGeo = Array.Empty<Geometry>();
    private Geometry? _bladeGeo;

    /// <summary>Raised when the user taps/drags over a segment in edit mode.</summary>
    public event Action<int>? SegmentPainted;

    public bool Editable { get; set; }

    /// <summary>Blade spin speed in degrees per second.</summary>
    public double SpinSpeed { get; set; } = 70;

    public int Segments
    {
        get => _segments;
        set
        {
            int v = Math.Clamp(value, 1, 64);
            if (v == _segments) return;
            _segments = v;
            _size = 0; // rebuild geometry
            InvalidateVisual();
        }
    }

    public FanView()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        Cursor = Cursors.Arrow;
    }

    /// <summary>Colours for the LED ring. If shorter than Segments it repeats.</summary>
    public void SetColors(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return;
        if (_colors.Length != colors.Count) _colors = new Rgb[colors.Count];
        for (int i = 0; i < colors.Count; i++) _colors[i] = colors[i];
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastRender == TimeSpan.Zero ? 0 : (now - _lastRender).TotalSeconds;
        _lastRender = now;
        if (dt > 0.2) dt = 0.016;
        _angle = (_angle + SpinSpeed * dt) % 360;
        InvalidateVisual();
    }

    private Rgb SegColor(int i) => _colors[(i * _colors.Length / Math.Max(1, _segments)) % _colors.Length];

    // ------------------------------------------------------------------ geometry

    private void Build(double size)
    {
        _size = size;
        var c = new Point(size / 2, size / 2);
        double rIn = size * 0.175, rOut = size * 0.235;
        double step = 360.0 / _segments;
        double gap = Math.Min(3.0, step * 0.12);
        _segGeo = new Geometry[_segments];
        for (int i = 0; i < _segments; i++)
            _segGeo[i] = Annulus(c, rIn, rOut, i * step + gap / 2, (i + 1) * step - gap / 2);

        // one swept fan blade (drawn 9 times, rotated)
        double r0 = size * 0.25, r1 = size * 0.445;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(c, r0, -14), true, true);
            ctx.BezierTo(Polar(c, r0 + (r1 - r0) * 0.35, -4), Polar(c, r0 + (r1 - r0) * 0.75, 6), Polar(c, r1, 18), true, true);
            ctx.ArcTo(Polar(c, r1, 46), new Size(r1, r1), 0, false, SweepDirection.Clockwise, true, true);
            ctx.BezierTo(Polar(c, r0 + (r1 - r0) * 0.7, 30), Polar(c, r0 + (r1 - r0) * 0.3, 18), Polar(c, r0, 14), true, true);
        }
        g.Freeze();
        _bladeGeo = g;
    }

    /// <summary>Angle 0 = 12 o'clock, clockwise.</summary>
    private static Point Polar(Point c, double r, double deg)
    {
        double a = (deg - 90) * Math.PI / 180;
        return new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
    }

    private static Geometry Annulus(Point c, double rIn, double rOut, double a0, double a1)
    {
        var g = new StreamGeometry();
        bool large = a1 - a0 > 180;
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(c, rOut, a0), true, true);
            ctx.ArcTo(Polar(c, rOut, a1), new Size(rOut, rOut), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(Polar(c, rIn, a1), true, true);
            ctx.ArcTo(Polar(c, rIn, a0), new Size(rIn, rIn), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }

    // ------------------------------------------------------------------ drawing

    private static readonly Brush FrameBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)));
    private static readonly Pen FramePen = FrozenPen(Color.FromArgb(0x26, 0xF2, 0xF2, 0xEF), 1);
    private static readonly Brush HubBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05)));
    private static readonly Pen HoverPen = FrozenPen(Color.FromRgb(0xF2, 0xF2, 0xEF), 2);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;
        if (Math.Abs(size - _size) > 0.5 || _segGeo.Length != _segments) Build(size);

        // centre the square drawing
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        var c = new Point(size / 2, size / 2);
        var avg = Average();
        var avgCol = Color.FromRgb(avg.R, avg.G, avg.B);

        // fan frame (rounded square) with screw holes
        dc.DrawRoundedRectangle(FrameBrush, FramePen, new Rect(size * 0.02, size * 0.02, size * 0.96, size * 0.96), size * 0.12, size * 0.12);
        foreach (var (x, y) in new[] { (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.9, 0.9) })
            dc.DrawEllipse(HubBrush, null, new Point(size * x, size * y), size * 0.028, size * 0.028);

        // blade bowl lit by the LEDs (like light through the clear blades)
        var bowl = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, avgCol.R, avgCol.G, avgCol.B), 0.35),
                new GradientStop(Color.FromArgb(55, avgCol.R, avgCol.G, avgCol.B), 0.75),
                new GradientStop(Color.FromArgb(10, avgCol.R, avgCol.G, avgCol.B), 1.0),
            },
        };
        bowl.Freeze();
        dc.DrawEllipse(bowl, null, c, size * 0.45, size * 0.45);

        // per-segment colour spill onto the blades (so multi-colour patterns show on the blades too)
        double step = 360.0 / _segments;
        for (int i = 0; i < _segments; i++)
        {
            var sc = SegColor(i);
            var wedge = Annulus(c, size * 0.24, size * 0.44, i * step, (i + 1) * step + 0.6);
            dc.DrawGeometry(Frozen(new SolidColorBrush(Color.FromArgb(70, sc.R, sc.G, sc.B))), null, wedge);
        }

        // spinning blades
        if (_bladeGeo != null)
        {
            var bladeBrush = Frozen(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)));
            var bladePen = FrozenPen(Color.FromArgb(40, 255, 255, 255), 1);
            for (int b = 0; b < 9; b++)
            {
                dc.PushTransform(new RotateTransform(_angle + b * 40, c.X, c.Y));
                dc.DrawGeometry(bladeBrush, bladePen, _bladeGeo);
                dc.Pop();
            }
        }

        // hub
        dc.DrawEllipse(HubBrush, FramePen, c, size * 0.245, size * 0.245);

        // LED ring: soft glow first, then the segments
        for (int i = 0; i < _segGeo.Length; i++)
        {
            var sc = SegColor(i);
            var glow = FrozenPen(Color.FromArgb(70, sc.R, sc.G, sc.B), size * 0.035);
            dc.DrawGeometry(null, glow, _segGeo[i]);
        }
        for (int i = 0; i < _segGeo.Length; i++)
        {
            var sc = SegColor(i);
            dc.DrawGeometry(Frozen(new SolidColorBrush(Color.FromRgb(sc.R, sc.G, sc.B))), null, _segGeo[i]);
            if (Editable && i == _hover) dc.DrawGeometry(null, HoverPen, _segGeo[i]);
        }

        // inner cap
        dc.DrawEllipse(HubBrush, null, c, size * 0.15, size * 0.15);
        dc.Pop();
    }

    private Rgb Average()
    {
        long r = 0, g = 0, b = 0;
        foreach (var c in _colors) { r += c.R; g += c.G; b += c.B; }
        int n = Math.Max(1, _colors.Length);
        return new Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    // ------------------------------------------------------------------ input

    private int HitSegment(Point p)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double dx = p.X - ActualWidth / 2, dy = p.Y - ActualHeight / 2;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r > size * 0.47 || r < size * 0.05) return -1;
        double deg = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360; // 0 at top, clockwise
        return (int)(deg / (360.0 / _segments)) % _segments;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Editable) { _hover = -1; return; }
        int seg = HitSegment(e.GetPosition(this));
        _hover = seg;
        Cursor = seg >= 0 ? Cursors.Hand : Cursors.Arrow;
        if (_painting && seg >= 0) SegmentPainted?.Invoke(seg);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Editable) return;
        int seg = HitSegment(e.GetPosition(this));
        if (seg < 0) return;
        _painting = true;
        CaptureMouse();
        SegmentPainted?.Invoke(seg);
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
}
