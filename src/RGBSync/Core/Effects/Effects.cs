using System;

namespace RGBSync.Core.Effects;

public enum EffectKind
{
    Static,
    Breathing,
    ColorCycle,
    RainbowWave,
    MusicPulse,
    MusicRainbow,
    Clock,
    Off,
    // added in 0.2 (kept at the end so saved settings stay valid)
    Comet,
    RainbowComet,
    Custom,
}

public static class EffectInfo
{
    public static string DisplayName(EffectKind k) => k switch
    {
        EffectKind.Static => "Static",
        EffectKind.Breathing => "Breathing",
        EffectKind.ColorCycle => "Colour Cycle",
        EffectKind.RainbowWave => "Rainbow Wave",
        EffectKind.MusicPulse => "Music Pulse",
        EffectKind.MusicRainbow => "Music Rainbow",
        EffectKind.Clock => "Ring Clock",
        EffectKind.Off => "Off",
        EffectKind.Comet => "Comet",
        EffectKind.RainbowComet => "Rainbow Comet",
        EffectKind.Custom => "Multi-colour",
        _ => k.ToString(),
    };

    public static string Description(EffectKind k) => k switch
    {
        EffectKind.Static => "One solid colour.",
        EffectKind.Breathing => "Fades your colour in and out.",
        EffectKind.ColorCycle => "Slowly moves through every colour.",
        EffectKind.RainbowWave => "A rainbow that flows across the LEDs.",
        EffectKind.MusicPulse => "Party mode: your colour flashes on every kick of whatever's playing (YouTube, Spotify, games...).",
        EffectKind.MusicRainbow => "Jumps to the next of YOUR colours on every beat. Paint your colours below.",
        EffectKind.Clock => "Turns the DeepCool pump ring into a clock (set the ring up in Settings).",
        EffectKind.Off => "Lights off.",
        EffectKind.Comet => "A bright comet with a fading tail spins around every fan.",
        EffectKind.RainbowComet => "A comet that changes colour as it spins.",
        EffectKind.Custom => "Paint each part of the fan its own colour - tap the fan below.",
        _ => "",
    };

    public static bool UsesColour(EffectKind k) =>
        k is EffectKind.Static or EffectKind.Breathing or EffectKind.MusicPulse or EffectKind.Clock
            or EffectKind.Comet or EffectKind.Custom or EffectKind.MusicRainbow;

    public static bool IsMusic(EffectKind k) => k is EffectKind.MusicPulse or EffectKind.MusicRainbow;

    /// <summary>Effects that use the painted colour palette.</summary>
    public static bool UsesPalette(EffectKind k) => k is EffectKind.Custom or EffectKind.MusicRainbow;

    public static bool UsesSpeed(EffectKind k) =>
        k is EffectKind.Breathing or EffectKind.ColorCycle or EffectKind.RainbowWave or EffectKind.MusicRainbow
            or EffectKind.Comet or EffectKind.RainbowComet or EffectKind.Custom or EffectKind.MusicPulse;
}

/// <summary>Level/Bass 0..1, BeatPulse 1 right on a beat fading to 0, Beats counts beats so far.</summary>
public readonly record struct AudioLevels(double Level, double Bass, double BeatPulse, int Beats = 0)
{
    public static readonly AudioLevels Silent = new(0, 0, 0, 0);
}

/// <summary>Where the pump ring sits inside the PC's LED list.</summary>
public sealed class ClockRing
{
    /// <summary>Index of the ring's first LED in the flattened PC LED list.</summary>
    public int StartLed { get; set; }
    public int LedCount { get; set; } = 12;
    /// <summary>Which ring LED (0-based, relative to StartLed) sits at 12 o'clock.</summary>
    public int TopOffset { get; set; }
    public bool Clockwise { get; set; } = true;
    public bool ShowSeconds { get; set; } = true;
    public Rgb HourColor { get; set; } = new(255, 40, 40);
    public Rgb MinuteColor { get; set; } = new(40, 160, 255);
    public Rgb SecondColor { get; set; } = new(255, 255, 255);
}

/// <summary>All the per-device settings an effect needs.</summary>
public sealed class LightState
{
    public EffectKind Effect { get; set; } = EffectKind.Static;
    public Rgb Color { get; set; } = new(0, 200, 255);
    /// <summary>0.2 (slow) .. 3 (fast)</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>0 .. 1</summary>
    public double Brightness { get; set; } = 1.0;
    /// <summary>Painted colours for the Multi-colour effect, one per LED around a fan.</summary>
    public Rgb[] Custom { get; set; } = DefaultCustom(12);
    /// <summary>Multi-colour: rotate the painted pattern.</summary>
    public bool Spin { get; set; }

    public static Rgb[] DefaultCustom(int n)
    {
        var a = new Rgb[n];
        for (int i = 0; i < n; i++) a[i] = Rgb.FromHsv(200 + 110.0 * i / n, 0.85, 1); // blue -> pink, like your fans
        return a;
    }

    public LightState Clone()
    {
        var c = (LightState)MemberwiseClone();
        c.Custom = (Rgb[])Custom.Clone();
        return c;
    }

    /// <summary>Changes when anything visible changes - used to trigger smooth cross-fades.</summary>
    public string Signature()
    {
        int h = 17;
        foreach (var c in Custom) h = h * 31 + c.GetHashCode();
        return $"{Effect}|{Color.ToHex()}|{Brightness:0.000}|{h}|{Spin}";
    }
}

public static class EffectRenderer
{
    private static readonly Rgb[] DefaultCustomCache = LightState.DefaultCustom(12);

    /// <summary>Render one frame into leds. t is seconds since start.</summary>
    /// <param name="period">LEDs around one fan. Patterns repeat every <paramref name="period"/> LEDs, because
    /// fans on a splitter all receive the same data. 0 = use the whole strip.</param>
    public static void Render(LightState s, double t, AudioLevels audio, Span<Rgb> leds, ClockRing? ring, DateTime now, int period = 0)
    {
        int n = leds.Length;
        if (n == 0) return;
        int p = period > 0 ? period : Math.Max(n, 12);
        double speed = Math.Clamp(s.Speed, 0.1, 5);

        switch (s.Effect)
        {
            case EffectKind.Off:
                leds.Fill(Rgb.Black);
                break;

            case EffectKind.Static:
                leds.Fill(s.Color);
                break;

            case EffectKind.Breathing:
            {
                double phase = (Math.Sin(t * Math.PI * 0.5 * speed - Math.PI / 2) + 1) / 2; // 0..1
                leds.Fill(s.Color.Scale(0.05 + 0.95 * phase * phase));
                break;
            }

            case EffectKind.ColorCycle:
            {
                var c = Rgb.FromHsv(t * 20 * speed, 1, 1);
                leds.Fill(c);
                break;
            }

            case EffectKind.RainbowWave:
            {
                double baseHue = t * 60 * speed;
                for (int i = 0; i < n; i++)
                    leds[i] = Rgb.FromHsv(baseHue + (i % p) * 360.0 / p, 1, 1);
                break;
            }

            case EffectKind.MusicPulse:
            {
                // party flash: slams to full on every kick, bass keeps it glowing in between
                double kick = audio.BeatPulse * audio.BeatPulse;
                double lvl = Math.Clamp(Math.Max(kick, Math.Pow(audio.Bass, 1.6) * 0.75), 0, 1);
                var c = s.Color.Scale(0.03 + 0.97 * lvl);
                if (kick > 0.85) c = Rgb.Lerp(c, Rgb.White, (kick - 0.85) * 1.6); // white-hot tip on hard hits
                leds.Fill(c);
                break;
            }

            case EffectKind.MusicRainbow:
            {
                // jumps to the next of YOUR colours on every beat (paint them like Multi-colour)
                var pal = s.Custom.Length > 0 ? s.Custom : DefaultCustomCache;
                int m = pal.Length;
                int idx = ((audio.Beats % m) + m) % m;
                double kick = audio.BeatPulse * audio.BeatPulse;
                double lvl = Math.Clamp(Math.Max(kick, Math.Pow(audio.Bass, 1.4) * 0.8), 0, 1);
                for (int i = 0; i < n; i++)
                {
                    // fans get a little spread of the next colours so they look alive
                    int j = (idx + (i % p) * Math.Min(m, 3) / p) % m;
                    leds[i] = pal[j].Scale(0.04 + 0.96 * lvl);
                }
                break;
            }

            case EffectKind.Comet:
            case EffectKind.RainbowComet:
            {
                // head moves round the fan; tail fades behind it
                double head = (t * speed * 1.2 * p) % p;
                for (int i = 0; i < n; i++)
                {
                    int pos = i % p;
                    double d = (head - pos + p) % p;          // how far behind the head this LED is
                    double tail = Math.Exp(-d * 0.55);
                    double glow = Math.Max(tail, 0.04);
                    var baseCol = s.Effect == EffectKind.RainbowComet
                        ? Rgb.FromHsv(t * 40 * speed + pos * 360.0 / p, 1, 1)
                        : s.Color;
                    var c = baseCol.Scale(glow);
                    if (d < 0.6) c = Rgb.Lerp(c, Rgb.White, 0.35); // hot white tip
                    leds[i] = c;
                }
                break;
            }

            case EffectKind.Custom:
            {
                var pal = s.Custom.Length > 0 ? s.Custom : DefaultCustomCache;
                int m = pal.Length;
                double shift = s.Spin ? t * speed * 0.8 * m : 0;
                int whole = (int)Math.Floor(shift);
                double frac = shift - whole;
                for (int i = 0; i < n; i++)
                {
                    // map the painted pattern onto this fan's LEDs, smoothly rotating if Spin is on
                    double pos = (i % p) * (double)m / p;
                    int a = ((int)Math.Floor(pos) - whole) % m; if (a < 0) a += m;
                    int b = (a - 1 + m) % m;
                    leds[i] = s.Spin ? Rgb.Lerp(pal[a], pal[b], frac) : pal[a];
                }
                break;
            }

            case EffectKind.Clock:
            {
                leds.Fill(s.Color.Scale(0.6));
                if (ring != null) RenderClock(ring, s.Color, leds, now);
                break;
            }
        }

        if (s.Brightness < 0.999)
            for (int i = 0; i < n; i++) leds[i] = leds[i].Scale(s.Brightness);
    }

    private static void RenderClock(ClockRing ring, Rgb background, Span<Rgb> leds, DateTime now)
    {
        int count = ring.LedCount;
        if (count <= 0 || ring.StartLed < 0 || ring.StartLed + count > leds.Length) return;
        var face = leds.Slice(ring.StartLed, count);
        face.Fill(background.Scale(0.12));

        double sec = now.Second + now.Millisecond / 1000.0;
        double min = now.Minute + sec / 60.0;
        double hr = (now.Hour % 12) + min / 60.0;

        // hour hand drawn first, minute on top, seconds on top of everything
        Paint(face, ring, hr / 12.0, ring.HourColor, 1.0);
        Paint(face, ring, min / 60.0, ring.MinuteColor, 1.0);
        if (ring.ShowSeconds) Paint(face, ring, sec / 60.0, ring.SecondColor, 0.55);
    }

    /// <summary>Light a fractional position (0..1 around the ring), split across two LEDs for smoothness.</summary>
    private static void Paint(Span<Rgb> face, ClockRing ring, double fraction, Rgb color, double strength)
    {
        int n = face.Length;
        double pos = fraction * n;
        int a = (int)Math.Floor(pos) % n;
        int b = (a + 1) % n;
        double w = pos - Math.Floor(pos);
        Blend(face, ring, a, color, (1 - w) * strength);
        Blend(face, ring, b, color, w * strength);
    }

    private static void Blend(Span<Rgb> face, ClockRing ring, int logical, Rgb color, double weight)
    {
        int n = face.Length;
        int physical = ring.Clockwise ? (ring.TopOffset + logical) % n : ((ring.TopOffset - logical) % n + n) % n;
        face[physical] = Rgb.Lerp(face[physical], color, Math.Clamp(weight * 1.4, 0, 1));
    }
}
