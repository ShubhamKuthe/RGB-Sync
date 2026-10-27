using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RGBSync.Core;

namespace RGBSync.Controls;

/// <summary>Hue/saturation colour wheel. Brightness is handled by a separate slider.</summary>
public sealed class ColorWheel : Grid
{
    private readonly Image _image = new();
    private readonly Canvas _overlay = new();
    private readonly Ellipse _thumb;
    private bool _dragging;
    private int _size;

    public double Hue { get; private set; }
    public double Saturation { get; private set; } = 1;

    /// <summary>Raised while the user picks a colour (hue 0-360, saturation 0-1).</summary>
    public event Action<double, double>? ColorPicked;

    public ColorWheel()
    {
        Children.Add(_image);
        Children.Add(_overlay);
        _thumb = new Ellipse
        {
            Width = 22, Height = 22, Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7 },
        };
        _overlay.Children.Add(_thumb);
        Cursor = Cursors.Cross;
        Background = Brushes.Transparent;

        SizeChanged += (_, _) => Rebuild();
        MouseLeftButtonDown += (_, e) => { _dragging = true; CaptureMouse(); Pick(e.GetPosition(this)); };
        MouseMove += (_, e) => { if (_dragging) Pick(e.GetPosition(this)); };
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
    }

    public void SetColor(Rgb c)
    {
        var (h, s, _) = c.ToHsv();
        // keep the old hue for greys so the thumb doesn't jump to red
        if (s > 0.01) Hue = h;
        Saturation = s;
        PlaceThumb();
    }

    private void Rebuild()
    {
        int size = (int)Math.Max(10, Math.Min(ActualWidth, ActualHeight));
        if (size == _size) { PlaceThumb(); return; }
        _size = size;

        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[size * size * 4];
        double r = size / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - r, dy = y + 0.5 - r;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int i = (y * size + x) * 4;
                if (dist > r) continue;
                double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 450) % 360; // 0 at top
                var c = Rgb.FromHsv(hue, dist / r, 1);
                double edge = Math.Clamp(r - dist, 0, 1); // anti-alias the rim
                px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = (byte)(255 * edge);
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), px, size * 4, 0);
        _image.Source = bmp;
        _image.Width = size;
        _image.Height = size;
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        PlaceThumb();
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    private void Pick(Point p)
    {
        double r = _size / 2.0;
        if (r <= 0) return;
        double dx = p.X - Center.X, dy = p.Y - Center.Y;
        double dist = Math.Min(Math.Sqrt(dx * dx + dy * dy), r);
        Hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 450) % 360;
        Saturation = dist / r;
        PlaceThumb();
        ColorPicked?.Invoke(Hue, Saturation);
    }

    private void PlaceThumb()
    {
        double r = _size / 2.0;
        double a = (Hue - 90) * Math.PI / 180;
        double x = Center.X + Math.Cos(a) * Saturation * r;
        double y = Center.Y + Math.Sin(a) * Saturation * r;
        Canvas.SetLeft(_thumb, x - _thumb.Width / 2);
        Canvas.SetTop(_thumb, y - _thumb.Height / 2);
        var c = Rgb.FromHsv(Hue, Saturation, 1);
        _thumb.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }
}
