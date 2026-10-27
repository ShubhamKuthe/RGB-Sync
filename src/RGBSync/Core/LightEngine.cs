using System;
using System.Diagnostics;
using System.Threading;
using RGBSync.Core.Devices;
using RGBSync.Core.Effects;

namespace RGBSync.Core;

/// <summary>
/// The heart of the app: one loop (~30 fps) renders the selected effect for each device and pushes it out.
/// With Sync on, the room light follows the PC's effect so everything moves together.
/// </summary>
public sealed class LightEngine : IDisposable
{
    private Thread? _thread;
    private volatile bool _running;
    private readonly Stopwatch _clock = new();
    private DateTime _lastReconnectTry = DateTime.MinValue;

    public PcDevice Pc { get; }
    public RoomLightDevice Room { get; }
    public LightState PcState { get; set; } = new();
    public LightState RoomState { get; set; } = new();
    public ClockRing Ring { get; set; } = new();
    public bool Sync { get; set; } = true;
    /// <summary>LEDs around one fan - patterns repeat every this many LEDs.</summary>
    public int FanLeds { get; set; } = 12;

    // smooth cross-fade when the look changes
    private string _pcSig = "", _roomSig = "";
    private Rgb[] _pcLast = Array.Empty<Rgb>(), _pcFadeFrom = Array.Empty<Rgb>();
    private double _pcFadeStart = -10, _roomFadeStart = -10;
    private Rgb _roomLast, _roomFadeFrom;
    private const double FadeTime = 0.45;
    private static double Ease(double x) { x = Math.Clamp(x, 0, 1); return 1 - Math.Pow(1 - x, 3); }
    public bool RoomEnabled { get; set; } = true;
    /// <summary>How many reconnect attempts in a row have failed (the UI re-finds the strip by MAC after a few).</summary>
    public int RoomFailures { get; set; }

    /// <summary>Supplies current audio levels (music effects). Null = silence.</summary>
    public Func<AudioLevels>? Audio { get; set; }

    /// <summary>Pump ring with its own colour/effect (separate from the fans).</summary>
    public LightState RingState { get; set; } = new() { Effect = EffectKind.Clock };
    public bool RingSeparate { get; set; }
    /// <summary>Last frame of just the ring LEDs, for its preview.</summary>
    public Rgb[] RingPreview { get; private set; } = Array.Empty<Rgb>();

    /// <summary>Light every header/zone in its own colour so you can see what is plugged where.</summary>
    public bool IdentifyZones { get; set; }
    public static readonly Rgb[] IdentifyColors =
    {
        new(255, 0, 0), new(0, 80, 255), new(0, 255, 0), new(255, 200, 0), new(200, 0, 255), new(0, 255, 255), new(255, 255, 255),
    };

    /// <summary>Block test: from Start, colour LEDs in blocks of Size using IdentifyColors (rest off).</summary>
    public (int Start, int Size)? BlockTest { get; set; }

    /// <summary>When set, the PC shows only this LED lit (white) - used by the "LED finder" in settings.</summary>
    public int? FinderLed { get; set; }

    /// <summary>When set, the room light shows this colour instead of the effect (used by "Test").</summary>
    public Rgb? RoomOverride { get; set; }

    /// <summary>Latest frames, for the on-screen previews.</summary>
    public Rgb[] PcPreview { get; private set; } = Array.Empty<Rgb>();
    public Rgb RoomPreview { get; private set; }
    /// <summary>One fan's worth of LEDs, for the animated fan on screen.</summary>
    public Rgb[] FanPreview { get; private set; } = Array.Empty<Rgb>();
    private Rgb[] _fanLast = Array.Empty<Rgb>(), _fanFadeFrom = Array.Empty<Rgb>();
    private double _fanFadeStamp = -100;

    public bool NeedsAudio =>
        PcState.Effect is EffectKind.MusicPulse or EffectKind.MusicRainbow ||
        (RingSeparate && RingState.Effect is EffectKind.MusicPulse or EffectKind.MusicRainbow) ||
        (!Sync && RoomState.Effect is EffectKind.MusicPulse or EffectKind.MusicRainbow);

    public LightEngine(PcDevice pc, RoomLightDevice room)
    {
        Pc = pc;
        Room = room;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _clock.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Light engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        const int fps = 50;
        var frame = TimeSpan.FromMilliseconds(1000.0 / fps);
        Rgb[] pcBuf = Array.Empty<Rgb>();
        var roomBuf = new Rgb[1];
        var syncedRoomBuf = new Rgb[16];

        while (_running)
        {
            var start = _clock.Elapsed;
            double t = start.TotalSeconds;
            var now = DateTime.Now;
            var audio = Audio?.Invoke() ?? AudioLevels.Silent;

            try
            {
                // ---- PC ----
                int pcCount = Pc.Status == DeviceStatus.Connected && Pc.LedCount > 0 ? Pc.LedCount : 24;
                if (pcBuf.Length != pcCount) pcBuf = new Rgb[pcCount];

                if (FinderLed is int f)
                {
                    pcBuf.AsSpan().Fill(Rgb.Black);
                    if (f >= 0 && f < pcBuf.Length) pcBuf[f] = Rgb.White;
                }
                else if (BlockTest is (int bs, int bz) && bz > 0)
                {
                    pcBuf.AsSpan().Fill(Rgb.Black);
                    for (int i = Math.Max(0, bs); i < pcBuf.Length; i++)
                        pcBuf[i] = IdentifyColors[((i - bs) / bz) % IdentifyColors.Length];
                }
                else if (IdentifyZones)
                {
                    PaintZones(pcBuf);
                }
                else
                {
                    bool ringOk = RingSeparate && Ring.LedCount > 0 && Ring.StartLed >= 0 && Ring.StartLed + Ring.LedCount <= pcBuf.Length;
                    // fans/everything else: the PC effect (its clock only makes sense when the ring isn't separate)
                    var pcState = ringOk && PcState.Effect == EffectKind.Clock ? WithEffect(PcState, EffectKind.Static) : PcState;
                    EffectRenderer.Render(pcState, t, audio, pcBuf, ringOk ? null : Ring, now, FanLeds);

                    if (ringOk)
                    {
                        var slice = pcBuf.AsSpan(Ring.StartLed, Ring.LedCount);
                        EffectRenderer.Render(RingState, t, audio, slice, RingAtZero(), now, FanLeds);
                        RingPreview = slice.ToArray();
                    }
                }
                // cross-fade from the previous look whenever the effect/colour changes
                if (FinderLed == null && BlockTest == null && !IdentifyZones)
                {
                    string sig = PcState.Signature() + (RingSeparate ? RingState.Signature() : "");
                    if (sig != _pcSig)
                    {
                        if (_pcSig != "" && _pcLast.Length == pcBuf.Length)
                        {
                            _pcFadeFrom = (Rgb[])_pcLast.Clone();
                            _pcFadeStart = t;
                        }
                        _pcSig = sig;
                    }
                    double k = (t - _pcFadeStart) / FadeTime;
                    if (k < 1 && _pcFadeFrom.Length == pcBuf.Length)
                    {
                        double e = Ease(k);
                        for (int i = 0; i < pcBuf.Length; i++) pcBuf[i] = Rgb.Lerp(_pcFadeFrom[i], pcBuf[i], e);
                    }
                }
                _pcLast = (Rgb[])pcBuf.Clone();

                // what one fan looks like (for the on-screen fan)
                var fan = new Rgb[Math.Max(1, FanLeds)];
                var fanState = PcState.Effect == EffectKind.Clock && RingSeparate ? WithEffect(PcState, EffectKind.Static) : PcState;
                var fanRing = new ClockRing
                {
                    StartLed = 0, LedCount = fan.Length, TopOffset = Ring.TopOffset, Clockwise = Ring.Clockwise, ShowSeconds = Ring.ShowSeconds,
                    HourColor = Ring.HourColor, MinuteColor = Ring.MinuteColor, SecondColor = Ring.SecondColor,
                };
                EffectRenderer.Render(fanState, t, audio, fan, fanRing, now, fan.Length);
                if (_pcFadeFrom.Length > 0 && (t - _pcFadeStart) < FadeTime && _fanLast.Length == fan.Length)
                {
                    if (_fanFadeFrom.Length != fan.Length || _fanFadeStamp != _pcFadeStart) { _fanFadeFrom = (Rgb[])_fanLast.Clone(); _fanFadeStamp = _pcFadeStart; }
                    double e2 = Ease((t - _pcFadeStart) / FadeTime);
                    for (int i = 0; i < fan.Length; i++) fan[i] = Rgb.Lerp(_fanFadeFrom[i], fan[i], e2);
                }
                _fanLast = fan;
                FanPreview = fan;

                if (Pc.Status == DeviceStatus.Connected) Pc.Send(pcBuf);
                PcPreview = (Rgb[])pcBuf.Clone();

                // ---- Room ----
                Rgb roomColor;
                {
                    // The room strip shows one colour at a time, so render the effect on a virtual fan and
                    // take the first LED: comets pass by as pulses, waves and multi-colour patterns flow through.
                    var src = Sync ? PcState : RoomState;
                    // stream every effect straight to the strip (no built-in fade) so it changes together with the PC
                    Room.MusicMode = RoomOverride == null;
                    var st = src;
                    if (st.Effect == EffectKind.Clock) st = WithEffect(st, EffectKind.Static);
                    if (st.Effect == EffectKind.Custom && !st.Spin)
                    {
                        // pattern isn't spinning: the strip holds still too, on the pattern's first colour
                        st = WithEffect(st, EffectKind.Static);
                        if (st.Custom.Length > 0) st.Color = st.Custom[0];
                    }
                    EffectRenderer.Render(st, t, audio, syncedRoomBuf, null, now, FanLeds);
                    roomColor = syncedRoomBuf[0];
                }
                {
                    string rsig = Sync ? PcState.Signature() : RoomState.Signature();
                    if (rsig != _roomSig) { if (_roomSig != "") { _roomFadeFrom = _roomLast; _roomFadeStart = t; } _roomSig = rsig; }
                    double rk = (t - _roomFadeStart) / FadeTime;
                    if (rk < 1 && !EffectInfo.IsMusic((Sync ? PcState : RoomState).Effect)) roomColor = Rgb.Lerp(_roomFadeFrom, roomColor, Ease(rk));
                    _roomLast = roomColor;
                }
                if (RoomOverride is Rgb ov) roomColor = ov;
                RoomPreview = roomColor;

                // painted pattern (Multi-colour): every section of the strip gets its own colour
                var roomSrc = Sync ? PcState : RoomState;
                Rgb[]? sections = null;
                if (RoomOverride == null && roomSrc.Effect == EffectKind.Custom && roomSrc.Custom.Length > 0)
                    sections = BuildSections(roomSrc, t);
                RoomSegmentsPreview = sections;

                if (RoomEnabled && Room.Status == DeviceStatus.Connected)
                {
                    if (sections != null)
                    {
                        Room.MusicMode = false;
                        Room.SendSegments(sections);
                    }
                    else
                    {
                        roomBuf[0] = roomColor;
                        Room.Send(roomBuf);
                    }
                }

                AutoReconnect();
            }
            catch
            {
                // never let one bad frame kill the loop
            }

            var elapsed = _clock.Elapsed - start;
            var wait = frame - elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
    }

    /// <summary>Live per-section colours of the room strip when a painted pattern is showing (else null).</summary>
    public Rgb[]? RoomSegmentsPreview { get; private set; }

    /// <summary>Lays the painted pattern along the strip's sections; with Spin on it slides along the strip.</summary>
    private static Rgb[] BuildSections(LightState st, double t)
    {
        int n = RoomLightDevice.StripSections;
        var pat = st.Custom;
        var outp = new Rgb[n];
        // ~3 sections a second at normal speed: the strip repaints by colour group, so keep it steady rather than blurry
        int shift = st.Spin ? (int)(t * 3 * Math.Max(0.2, st.Speed)) % n : 0;
        double bri = Math.Clamp(st.Brightness, 0, 1);
        for (int i = 0; i < n; i++)
        {
            int src = ((i + shift) % n) * pat.Length / n;
            outp[i] = Rgb.Lerp(Rgb.Black, pat[src], bri);
        }
        return outp;
    }

    private ClockRing RingAtZero() => new()
    {
        StartLed = 0, LedCount = Ring.LedCount, TopOffset = Ring.TopOffset, Clockwise = Ring.Clockwise,
        ShowSeconds = Ring.ShowSeconds, HourColor = Ring.HourColor, MinuteColor = Ring.MinuteColor, SecondColor = Ring.SecondColor,
    };

    private void PaintZones(Rgb[] buf)
    {
        buf.AsSpan().Fill(Rgb.Black);
        int off = 0, n = 0;
        foreach (var c in Pc.ActiveControllers)
        {
            foreach (var z in c.Zones)
            {
                var col = IdentifyColors[n++ % IdentifyColors.Length];
                for (int i = 0; i < z.LedCount && off + z.StartLed + i < buf.Length; i++)
                    buf[off + z.StartLed + i] = col;
            }
            off += c.LedCount;
        }
    }

    private static LightState WithEffect(LightState s, EffectKind k)
    {
        var c = s.Clone();
        c.Effect = k;
        return c;
    }

    private void AutoReconnect()
    {
        if ((DateTime.UtcNow - _lastReconnectTry).TotalSeconds < 4) return;
        _lastReconnectTry = DateTime.UtcNow;
        if (Pc.Status is DeviceStatus.Error)
            _ = Pc.ConnectAsync();
        // the room light heals itself in its own thread; we only (re)start it if it was never started
        if (RoomEnabled && Room.IsConfigured && Room.Status == DeviceStatus.Disconnected)
            _ = Room.ConnectAsync();
        RoomFailures = RoomEnabled ? Room.Failures : 0;
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
    }

    public void Dispose()
    {
        Stop();
        Pc.Dispose();
        Room.Dispose();
    }
}
