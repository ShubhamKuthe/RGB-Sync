using System;
using System.Diagnostics;
using NAudio.Wave;
using RGBSync.Core.Effects;

namespace RGBSync.Services;

/// <summary>
/// Listens to whatever Windows is playing (WASAPI loopback - YouTube, Spotify, games, anything; no microphone)
/// and turns it into levels + beats for the music effects.
/// Beat detection works on ~6 ms slices of the kick-drum band, comparing each slice's energy with the
/// recent average - fast enough to hit the kick, with a sensitivity knob for quiet or busy songs.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly object _lock = new();
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    // two-pole low-pass for the kick band (~40-150 Hz)
    private double _lp1, _lp2;
    private double _level, _bass;
    private double _bassAvg = 1e-4;      // ~0.5 s average of kick energy
    private double _bassVar = 1e-8;      // its variance
    private double _peak = 0.05;         // auto gain
    private double _bassPeak = 0.02;
    private double _lastBeatTime = -10;
    private double _lastUpdate;
    private int _beats;
    private double _prevSliceEnergy;

    public bool IsRunning => _capture != null;
    public string? LastError { get; private set; }

    /// <summary>0.3 (only big hits) .. 3 (reacts to everything). Default 1.5.</summary>
    public double Sensitivity { get; set; } = 1.5;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var cap = new WasapiLoopbackCapture();
            cap.DataAvailable += OnData;
            cap.RecordingStopped += (_, e) =>
            {
                LastError = e.Exception?.Message;
                Stop(); // output device changed or went away; restarted on the next tick
            };
            cap.StartRecording();
            _capture = cap;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _capture = null;
        }
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
        lock (_lock) { _level = 0; _bass = 0; }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        if (cap == null || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * channels);
        if (frames == 0) return;

        double rate = fmt.SampleRate;
        double a = 1 - Math.Exp(-2 * Math.PI * 150 / rate);   // kick band cut-off
        int slice = Math.Max(64, (int)(rate * 0.006));        // ~6 ms slices
        double sens = Math.Clamp(Sensitivity, 0.3, 3);

        double sumSq = 0, sliceBass = 0;
        int sliceCount = 0;
        double lp1 = _lp1, lp2 = _lp2;

        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                double mono = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int idx = (f * channels + ch) * bytesPerSample;
                    mono += isFloat
                        ? BitConverter.ToSingle(e.Buffer, idx)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, idx) / 32768.0 : 0;
                }
                mono /= channels;
                sumSq += mono * mono;
                lp1 += a * (mono - lp1);
                lp2 += a * (lp1 - lp2);
                sliceBass += lp2 * lp2;
                sliceCount++;

                if (sliceCount >= slice)
                {
                    ProcessSlice(sliceBass / sliceCount, sens);
                    sliceBass = 0;
                    sliceCount = 0;
                }
            }

            double rms = Math.Sqrt(sumSq / frames);
            _peak = Math.Max(rms * 1.1, _peak * 0.9993);
            _peak = Math.Max(_peak, 0.01);
            double lvl = Math.Clamp(rms / _peak * (0.6 + 0.4 * sens), 0, 1);
            _level = lvl > _level ? lvl : _level * 0.8 + lvl * 0.2;
            _lastUpdate = _sw.Elapsed.TotalSeconds;
        }
        _lp1 = lp1;
        _lp2 = lp2;
    }

    private void ProcessSlice(double energy, double sens)
    {
        double now = _sw.Elapsed.TotalSeconds;
        double rms = Math.Sqrt(energy);

        // bass level with auto gain (fast attack, quick release - punchy)
        _bassPeak = Math.Max(rms * 1.05, _bassPeak * 0.9985);
        _bassPeak = Math.Max(_bassPeak, 0.004);
        double b = Math.Clamp(rms / _bassPeak * (0.55 + 0.45 * sens), 0, 1);
        _bass = b > _bass ? b : _bass * 0.86 + b * 0.14;

        // beat = sudden jump in kick energy above the recent average (+ variance), with a short refractory time
        double std = Math.Sqrt(Math.Max(_bassVar, 0));
        double threshold = _bassAvg + std * (2.2 / sens);
        bool rising = energy > _prevSliceEnergy * (1.0 + 0.25 / sens);
        if (energy > threshold && rising && energy > 1e-6 && now - _lastBeatTime > 0.11)
        {
            _lastBeatTime = now;
            _beats++;
        }
        _prevSliceEnergy = energy;

        // ~0.5 s running mean/variance of slice energy
        const double k = 0.012;
        double d = energy - _bassAvg;
        _bassAvg += k * d;
        _bassVar = (1 - k) * (_bassVar + k * d * d);
    }

    public AudioLevels Current
    {
        get
        {
            lock (_lock)
            {
                double now = _sw.Elapsed.TotalSeconds;
                if (now - _lastUpdate > 0.3) { _level *= 0.8; _bass *= 0.8; } // silence
                double pulse = Math.Exp(-(now - _lastBeatTime) * 9);       // 1 on the kick, gone in ~0.25 s
                return new AudioLevels(_level, _bass, pulse, _beats);
            }
        }
    }

    public void Dispose() => Stop();
}
