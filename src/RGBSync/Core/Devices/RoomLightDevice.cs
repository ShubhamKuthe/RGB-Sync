using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

public enum TuyaDpsProfile
{
    /// <summary>Newer lights/strips: 20 switch, 21 mode, 22 brightness, 24 colour (HHHHSSSSVVVV).</summary>
    Standard,
    /// <summary>Older lights: 1 switch, 2 mode, 3 brightness, 5 colour (rrggbb0hhhssvv).</summary>
    Legacy,
}

/// <summary>
/// The room LED strip (Calex Smart = Tuya).
/// ONE background thread owns the connection for the whole app lifetime: it connects, sends the newest colour
/// (never a backlog), keeps the link alive with heartbeats, and if the strip drops the link it reconnects
/// quietly in the background. The strip only allows one local session, so we never open two at once.
/// </summary>
public sealed class RoomLightDevice : IRgbDevice
{
    // hard ceiling - Tuya Wi-Fi strips start dropping the link above ~10-12 commands a second
    private const int HardMaxFps = 12;

    private TuyaClient? _client;
    private volatile DeviceStatus _status = DeviceStatus.Disconnected;
    private string _statusText = "Not set up yet";
    private Thread? _worker;
    private volatile bool _wanted;          // should we be connected?
    private volatile bool _broken;          // current link died - worker will replace it
    private volatile bool _forceReconnect;
    private string _connectedSig = "";
    private readonly object _workerLock = new();
    private readonly AutoResetEvent _wake = new(false);

    private Rgb _pending;
    private bool _hasPending;
    private readonly object _pendingLock = new();
    private Rgb? _lastSent;
    private bool _modeSet;
    private string _modeName = "";
    private TaskCompletionSource<bool>? _attempt;

    public string DisplayName { get; set; } = "Room Light";
    public string Ip { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string LocalKey { get; set; } = "";
    public string Version { get; set; } = "3.5";
    public TuyaDpsProfile Profile { get; set; } = TuyaDpsProfile.Standard;
    public int UpdatesPerSecond { get; set; } = 8;

    /// <summary>
    /// True while a music effect is playing: the strip is put in its own music mode and each colour is applied
    /// instantly (no built-in fade), so the real light keeps up with the beat like the preview does.
    /// </summary>
    public bool MusicMode
    {
        get => _musicMode;
        set { if (_musicMode == value) return; _musicMode = value; _lastSent = null; _wake.Set(); }
    }
    private volatile bool _musicMode;

    public string Name => DisplayName;
    public DeviceStatus Status => _status;
    public string StatusText => _statusText;
    public int LedCount => 1;
    public int MaxFps => Math.Clamp(UpdatesPerSecond, 1, HardMaxFps);
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Ip) && !string.IsNullOrWhiteSpace(DeviceId) && LocalKey.Trim().Length == 16;

    /// <summary>Failed connection attempts in a row (used to go looking for the strip by MAC).</summary>
    public int Failures { get; private set; }

    public event Action? StatusChanged;
    /// <summary>Raw status JSON from the light (for the settings "Read status" helper).</summary>
    public event Action<string>? RawStatus;

    private string Sig => $"{Ip.Trim()}|{DeviceId.Trim()}|{LocalKey.Trim()}|{Version}";

    private static void Log(string msg) => RGBSync.Services.AppLog.Append("room.log", msg);

    private void SetStatus(DeviceStatus s, string text)
    {
        if (_status != s || _statusText != text) Log($"[{s}] {text}");
        if (_status == s && _statusText == text) return;
        _status = s;
        _statusText = text;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Make sure we're connected. Safe to call any time, from anywhere, as often as you like:
    /// if we're already connected with the same settings nothing is dropped.
    /// </summary>
    public Task ConnectAsync() => EnsureConnectedAsync(false);

    /// <summary>Drop the link and connect fresh (the Reconnect button).</summary>
    public Task ReconnectAsync() => EnsureConnectedAsync(true);

    private async Task EnsureConnectedAsync(bool force)
    {
        if (!IsConfigured)
        {
            _wanted = false;
            DropClient();
            SetStatus(DeviceStatus.Disconnected, "Not set up yet - open Settings and enter IP, Device ID and Local Key");
            return;
        }

        _wanted = true;
        bool alreadyGood = _status == DeviceStatus.Connected && !_broken && _client?.IsConnected == true && _connectedSig == Sig;
        if (alreadyGood && !force)
        {
            _modeSet = false; // re-assert colour mode on the next send, harmless
            return;
        }
        if (force) _forceReconnect = true;

        TaskCompletionSource<bool> tcs;
        lock (_workerLock)
        {
            _attempt ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _attempt;
            if (_worker == null || !_worker.IsAlive)
            {
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Room light" };
                _worker.Start();
            }
        }
        _wake.Set();
        await Task.WhenAny(tcs.Task, Task.Delay(12000)).ConfigureAwait(false);
    }

    public void Send(ReadOnlySpan<Rgb> leds)
    {
        if (leds.Length == 0) return;
        Rgb c;
        if (leds.Length == 1) c = leds[0];
        else
        {
            long r = 0, g = 0, b = 0;
            foreach (var l in leds) { r += l.R; g += l.G; b += l.B; }
            c = new Rgb((byte)(r / leds.Length), (byte)(g / leds.Length), (byte)(b / leds.Length));
        }
        bool kick;
        lock (_pendingLock)
        {
            if (_segMode) { _segMode = false; _lastSent = null; }
            kick = MusicMode && _lastSent is Rgb ls && Distance(ls, c) > 150;
            _pending = c;
            _hasPending = true;
        }
        if (kick) _wake.Set();
    }

    public void RequestStatus()
    {
        try { _client?.QueryStatus(); } catch { }
    }

    private void CompleteAttempt(bool ok)
    {
        TaskCompletionSource<bool>? t;
        lock (_workerLock) { t = _attempt; _attempt = null; }
        t?.TrySetResult(ok);
    }

    private void WorkerLoop()
    {
        var lastBeat = DateTime.UtcNow;
        var lastSend = DateTime.UtcNow.AddSeconds(-5);
        var lastSegRefresh = DateTime.UtcNow;
        DateTime lastGood = DateTime.MinValue;
        int backoffMs = 0;

        while (true)
        {
            if (!_wanted)
            {
                DropClient();
                CompleteAttempt(false);
                _wake.WaitOne(1000);
                continue;
            }

            // ---- (re)connect when needed ----
            var client = _client;
            bool needConnect = client == null || _broken || !client.IsConnected || _forceReconnect || _connectedSig != Sig;
            if (needConnect)
            {
                if (backoffMs > 0) { _wake.WaitOne(backoffMs); if (!_wanted) continue; }
                _forceReconnect = false;
                DropClient();

                // brief hiccup while we were happily connected? stay "Connected" so the UI doesn't flicker
                bool silent = _status == DeviceStatus.Connected && (DateTime.UtcNow - lastGood).TotalSeconds < 30 && Failures == 0;
                if (!silent) SetStatus(DeviceStatus.Connecting, $"Connecting to {Ip}...");

                string sig = Sig;
                Log($"connect attempt -> {Ip} v{Version} (failures {Failures})");
                TuyaClient? fresh = null;
                try
                {
                    fresh = new TuyaClient(Ip, DeviceId, LocalKey, Version);
                    var me = fresh;
                    fresh.Error += m => { Log("link error: " + m); if (ReferenceEquals(_client, me)) { _broken = true; _wake.Set(); } };
                    fresh.StatusReceived += json => RawStatus?.Invoke(json);
                    fresh.ConnectAsync().GetAwaiter().GetResult();

                    _client = fresh;
                    _connectedSig = sig;
                    _broken = false;
                    _modeSet = false;
                    _lastSent = null;
                    _segActual = null;
                    Failures = 0;
                    backoffMs = 0;
                    lastBeat = DateTime.UtcNow;
                    lastGood = DateTime.UtcNow;
                    lock (_pendingLock) { if (!_hasPending && _lastSentEver is Rgb last) { _pending = last; _hasPending = true; } }
                    SetStatus(DeviceStatus.Connected, $"Connected ({Ip}, v{Version})");
                    CompleteAttempt(true);
                }
                catch (Exception ex)
                {
                    fresh?.Dispose();
                    Failures++;
                    backoffMs = Math.Min(10000, 800 * (1 << Math.Min(Failures, 4)));
                    SetStatus(DeviceStatus.Error, Failures >= 3
                        ? $"Can't reach the strip ({ex.Message}). Still trying..."
                        : $"Reconnecting... ({ex.Message})");
                    CompleteAttempt(false);
                }
                continue;
            }

            // ---- send the newest colour, paced ----
            int interval = 1000 / MaxFps;
            double sinceSend = Math.Min(10000, (DateTime.UtcNow - lastSend).TotalMilliseconds);
            if (sinceSend < interval)
            {
                // music: a big jump (a kick hitting, or the light cutting out) goes out right away, not on the next tick
                bool urgent = false;
                if (MusicMode && !_segMode && sinceSend >= 45)
                    lock (_pendingLock) urgent = _hasPending && _lastSent is Rgb ls && Distance(ls, _pending) > 150;
                if (!urgent) { _wake.WaitOne(Math.Max(1, Math.Min(10, (int)(interval - sinceSend)))); continue; }
            }

            try
            {
                if (_segMode)
                {
                    // the strip needs a moment between paint commands or it drops them
                    if ((DateTime.UtcNow - lastSend).TotalMilliseconds < 220) { Thread.Sleep(10); continue; }

                    // every few seconds repaint the odd-one-out sections, in case the strip missed one
                    if ((DateTime.UtcNow - lastSegRefresh).TotalSeconds > 4)
                    {
                        lastSegRefresh = DateTime.UtcNow;
                        RefreshSegmentBelief();
                    }

                    var cmd = NextSegmentCommand();
                    if (cmd != null)
                    {
                        client!.SetDps(cmd);
                        if (cmd.TryGetValue("61", out var p61)) Log("paint " + Convert.ToHexString(Convert.FromBase64String((string)p61)));
                        lastSend = DateTime.UtcNow;
                        lastBeat = lastSend;
                        lastGood = lastSend;
                    }
                    else
                    {
                        if ((DateTime.UtcNow - lastBeat).TotalSeconds > 8) { client!.Heartbeat(); lastBeat = DateTime.UtcNow; }
                        Thread.Sleep(15);
                    }
                    continue;
                }

                Rgb c;
                bool has;
                lock (_pendingLock) { c = _pending; has = _hasPending; _hasPending = false; }

                if (has && (_lastSent == null || Distance(_lastSent.Value, c) > 3))
                {
                    client!.SetDps(BuildDps(c));
                    _lastSent = c;
                    _lastSentEver = c;
                    lastSend = DateTime.UtcNow;
                    lastBeat = lastSend;
                    lastGood = lastSend;
                }
                else if (MusicMode && _lastSent is Rgb keep && (DateTime.UtcNow - lastSend).TotalSeconds > 2)
                {
                    // live mode: refresh the held colour now and then so the strip never drifts back
                    client!.SetDps(BuildDps(keep));
                    lastSend = DateTime.UtcNow;
                    lastBeat = lastSend;
                }
                else
                {
                    if ((DateTime.UtcNow - lastBeat).TotalSeconds > 8)
                    {
                        client!.Heartbeat();
                        lastBeat = DateTime.UtcNow;
                    }
                    Thread.Sleep(15);
                }
            }
            catch
            {
                // the link died mid-send: keep the colour and reconnect straight away
                Log("send failed - reconnecting");
                _broken = true;
                lock (_pendingLock) { if (!_hasPending) { _pending = c0(); _hasPending = true; } }
            }
        }

        Rgb c0() => _lastSentEver ?? _pending;
    }

    private Rgb? _lastSentEver;

    // ---- per-section painting (dp 61) ----
    /// <summary>How many paintable sections the strip has (it reports 20).</summary>
    public const int StripSections = 20;
    private Rgb[]? _segDesired;
    private Rgb[]? _segActual;          // what we believe the strip shows right now (null = unknown)
    private volatile bool _segMode;
    public bool SegmentMode => _segMode;

    /// <summary>Show a different colour on each section of the strip (one entry per section).</summary>
    public void SendSegments(Rgb[] sections)
    {
        if (sections.Length == 0) return;
        lock (_pendingLock)
        {
            if (_segDesired == null || _segDesired.Length != sections.Length) _segDesired = new Rgb[sections.Length];
            Array.Copy(sections, _segDesired, sections.Length);
            if (!_segMode) { _segMode = true; _segActual = null; }
        }
    }

    /// <summary>Pretend the strip only has its main colour, so the other sections get painted again (no flash).</summary>
    private void RefreshSegmentBelief()
    {
        Rgb[] want;
        lock (_pendingLock)
        {
            if (_segDesired == null || _segActual == null) return;
            want = (Rgb[])_segDesired.Clone();
        }
        Rgb best = want[0];
        int bestCount = 0;
        foreach (var c in want)
        {
            int k = 0;
            foreach (var d in want) if (Distance(c, d) <= 8) k++;
            if (k > bestCount) { bestCount = k; best = c; }
        }
        if (bestCount == want.Length) return;
        var a = new Rgb[want.Length];
        for (int i = 0; i < a.Length; i++) a[i] = Distance(want[i], best) <= 8 ? want[i] : best;
        _segActual = a;
    }

    /// <summary>Next dp-61 command needed to bring the strip to the wanted section colours (null = nothing to do).</summary>
    private Dictionary<string, object>? NextSegmentCommand()
    {
        Rgb[] want;
        lock (_pendingLock)
        {
            if (_segDesired == null) return null;
            want = (Rgb[])_segDesired.Clone();
        }
        int n = want.Length;
        const int same = 8;
        var dps = new Dictionary<string, object>();
        if (!_modeSet || _modeName != "colour") { dps["20"] = true; dps["21"] = "colour"; _modeSet = true; _modeName = "colour"; }

        if (_segActual == null || _segActual.Length != n)
        {
            // unknown state: fill the whole strip with the most common colour first, then fix the rest
            Rgb best = want[0];
            int bestCount = 0;
            foreach (var c in want)
            {
                int k = 0;
                foreach (var d in want) if (Distance(c, d) <= same) k++;
                if (k > bestCount) { bestCount = k; best = c; }
            }
            dps["61"] = PaintCommand(best, null, n);
            _segActual = new Rgb[n];
            for (int i = 0; i < n; i++) _segActual[i] = best;
            return dps;
        }

        int first = -1;
        for (int i = 0; i < n; i++) if (Distance(_segActual[i], want[i]) > same) { first = i; break; }
        if (first < 0) return dps.Count > 0 ? dps : null;

        // one command paints every changed section that wants (roughly) the same colour
        var col = want[first];
        var idx = new List<int>();
        for (int i = 0; i < n; i++)
            if (Distance(_segActual[i], want[i]) > same && Distance(want[i], col) <= same) idx.Add(i);
        dps["61"] = idx.Count == n ? PaintCommand(col, null, n) : PaintCommand(col, idx, n);
        foreach (int i in idx) _segActual[i] = want[i];
        return dps;
    }

    /// <summary>
    /// Builds the strip's paint command: 00 01 00 [sections] [0=all / 1=listed] HHHH SSSS VVVV [0x80|count] [section numbers, 1-based].
    /// (Worked out from what the Smart Life app sends when you paint.)
    /// </summary>
    private static string PaintCommand(Rgb c, List<int>? sections, int total)
    {
        var (h, s, v) = c.ToHsv();
        int hh = (int)Math.Round(h) % 360;
        int ss = Math.Clamp((int)Math.Round(s * 1000), 0, 1000);
        int vv = Math.Clamp((int)Math.Round(v * 1000), 0, 1000);
        var b = new List<byte>
        {
            0x00, 0x01, 0x00, (byte)total, (byte)(sections == null ? 0 : 1),
            (byte)(hh >> 8), (byte)hh, (byte)(ss >> 8), (byte)ss, (byte)(vv >> 8), (byte)vv,
        };
        if (sections != null)
        {
            b.Add((byte)(0x80 | sections.Count));
            foreach (int i in sections) b.Add((byte)(i + 1));
        }
        return Convert.ToBase64String(b.ToArray());
    }

    private static int Distance(Rgb a, Rgb b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private Dictionary<string, object> BuildDps(Rgb c)
    {
        var (h, s, v) = c.ToHsv();
        var dps = new Dictionary<string, object>();
        if (Profile == TuyaDpsProfile.Standard)
        {
            bool music = MusicMode;
            string want = music ? "music" : "colour";
            if (!_modeSet || _modeName != want) { dps["20"] = true; dps["21"] = want; _modeSet = true; _modeName = want; }
            int hh = (int)Math.Round(h) % 360;
            int ss = (int)Math.Round(s * 1000);
            if (music)
            {
                // music_data: "0" = jump straight to the colour (no fade), then H S V, brightness, temperature.
                // V can go all the way to 0 so the strip really cuts out between beats.
                int vm = (int)Math.Round(v * 1000);
                dps["27"] = $"0{hh:x4}{ss:x4}{vm:x4}00000000";
            }
            else
            {
                int vv = Math.Max(10, (int)Math.Round(v * 1000));
                dps["24"] = $"{hh:x4}{ss:x4}{vv:x4}";
            }
        }
        else
        {
            if (!_modeSet) { dps["1"] = true; dps["2"] = "colour"; _modeSet = true; }
            var full = Rgb.FromHsv(h, s, 1);
            int hh = (int)Math.Round(h) % 360;
            int ss = (int)Math.Round(s * 255);
            int vv = Math.Max(25, (int)Math.Round(v * 255));
            dps["5"] = $"{full.R:x2}{full.G:x2}{full.B:x2}{hh:x4}{ss:x2}{vv:x2}";
        }
        return dps;
    }

    private void DropClient()
    {
        var c = _client;
        _client = null;
        _connectedSig = "";
        c?.Dispose();
    }

    public void Disconnect()
    {
        _wanted = false;
        _wake.Set();
        DropClient();
        SetStatus(DeviceStatus.Disconnected, IsConfigured ? "Not connected" : "Not set up yet");
    }

    public void Dispose()
    {
        // leaving the app: hand the strip back in normal colour mode (showing the last colour),
        // so the phone app shows it normally
        try
        {
            if (_client?.IsConnected == true && _lastSentEver is Rgb last)
            {
                _musicMode = false;
                _modeSet = false;
                _client.SetDps(BuildDps(last));
                Thread.Sleep(150);
            }
        }
        catch { }
        Disconnect();
    }
}
