using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

/// <summary>
/// "My PC": every RGB controller OpenRGB can see (Gigabyte RGB Fusion headers, cooler, etc.)
/// flattened into one long LED list. Fan speed is never touched - colours only.
/// </summary>
public sealed class PcDevice : IRgbDevice
{
    private readonly object _lock = new();
    private OpenRgbClient? _client;
    private List<OpenRgbController> _active = new();
    private DeviceStatus _status = DeviceStatus.Disconnected;
    private string _statusText = "Not connected";

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    /// <summary>Controller names the user switched off (e.g. RAM sticks they want left alone).</summary>
    public HashSet<string> ExcludedControllers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "My PC";
    public DeviceStatus Status => _status;
    public string StatusText => _statusText;
    public int MaxFps => 30;
    public int LedCount { get; private set; }

    public IReadOnlyList<OpenRgbController> AllControllers => _client?.Controllers ?? new List<OpenRgbController>();
    public IReadOnlyList<OpenRgbController> ActiveControllers => _active;

    public event Action? StatusChanged;

    /// <summary>Offset of a controller's first LED inside the flattened list, or -1 if not active.</summary>
    public int GetControllerOffset(int controllerIndex)
    {
        int off = 0;
        foreach (var c in _active)
        {
            if (c.Index == controllerIndex) return off;
            off += c.LedCount;
        }
        return -1;
    }

    private void SetStatus(DeviceStatus s, string text)
    {
        _status = s;
        _statusText = text;
        StatusChanged?.Invoke();
    }

    private readonly System.Threading.SemaphoreSlim _connectGate = new(1, 1);
    private int _reloadQueued;

    private static void Log(string msg) => RGBSync.Services.AppLog.Append("pc.log", msg);

    /// <summary>
    /// Connect to OpenRGB. Only one connect runs at a time; calls made while one is running just wait for it.
    /// Events from an old, replaced connection are ignored, so a reconnect can never trigger another reconnect.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (!await _connectGate.WaitAsync(0).ConfigureAwait(false))
        {
            // someone is already connecting - wait for that instead of starting a second one
            await _connectGate.WaitAsync().ConfigureAwait(false);
            _connectGate.Release();
            return;
        }
        try { await ConnectCoreAsync().ConfigureAwait(false); }
        finally { _connectGate.Release(); }
    }

    private async Task ConnectCoreAsync()
    {
        Log("connect");
        DropClient();
        SetStatus(DeviceStatus.Connecting, "Connecting to OpenRGB...");
        var client = new OpenRgbClient();
        try
        {
            await client.ConnectAsync(Host, Port, "RGB Sync").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            Log("connect failed: " + ex.Message);
            SetStatus(DeviceStatus.Error,
                "Can't reach OpenRGB. Open OpenRGB and start the SDK Server (SDK Server tab > Start Server). " +
                $"({ex.Message})");
            return;
        }

        client.Disconnected += () =>
        {
            if (!ReferenceEquals(_client, client)) return; // an old connection closing - not our problem
            Log("lost connection");
            SetStatus(DeviceStatus.Error, "Lost connection to OpenRGB");
        };
        client.DeviceListChanged += () =>
        {
            if (!ReferenceEquals(_client, client)) return;
            // OpenRGB sends a burst of these while it detects hardware - reload once, after it settles
            if (System.Threading.Interlocked.Exchange(ref _reloadQueued, 1) == 1) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                System.Threading.Interlocked.Exchange(ref _reloadQueued, 0);
                Log("device list changed - reloading");
                await ConnectAsync().ConfigureAwait(false);
            });
        };

        lock (_lock)
        {
            _client = client;
            RebuildActive();
            foreach (var c in _active)
            {
                try { client.SetCustomMode(c.Index); } catch { }
            }
        }

        if (client.Controllers.Count == 0)
        {
            Log("no devices");
            SetStatus(DeviceStatus.Error, "OpenRGB is running but found no RGB devices. Run OpenRGB as administrator once.");
        }
        else
        {
            Log($"connected: {_active.Count} device(s), {LedCount} LEDs");
            SetStatus(DeviceStatus.Connected, $"{_active.Count} device(s), {LedCount} LEDs");
        }
    }

    private void DropClient()
    {
        OpenRgbClient? old;
        lock (_lock)
        {
            old = _client;
            _client = null;   // cleared first, so the old client's "disconnected" event is ignored
            _active = new();
            LedCount = 0;
        }
        old?.Dispose();
    }

    /// <summary>Resize an ARGB header zone, then reload the device list.</summary>
    public async Task ResizeZoneAsync(int controllerIndex, int zoneIndex, int newSize)
    {
        lock (_lock)
        {
            _client?.ResizeZone(controllerIndex, zoneIndex, newSize);
        }
        // OpenRGB tells us the device list changed and we reload once (see DeviceListChanged above);
        // reload here too in case that message doesn't come
        await Task.Delay(600).ConfigureAwait(false);
        if (_reloadQueued == 0) await ConnectAsync().ConfigureAwait(false);
    }

    /// <summary>Call after changing ExcludedControllers.</summary>
    public void RefreshSelection()
    {
        lock (_lock)
        {
            if (_client == null) return;
            RebuildActive();
            foreach (var c in _active)
            {
                try { _client.SetCustomMode(c.Index); } catch { }
            }
        }
        if (_status == DeviceStatus.Connected)
            SetStatus(DeviceStatus.Connected, $"{_active.Count} device(s), {LedCount} LEDs");
    }

    private void RebuildActive()
    {
        _active = (_client?.Controllers ?? new List<OpenRgbController>())
            .Where(c => c.LedCount > 0 && !ExcludedControllers.Contains(c.Name))
            .ToList();
        LedCount = _active.Sum(c => c.LedCount);
    }

    public void Send(ReadOnlySpan<Rgb> leds)
    {
        lock (_lock)
        {
            if (_client == null || _status != DeviceStatus.Connected) return;
            int off = 0;
            try
            {
                foreach (var c in _active)
                {
                    if (off + c.LedCount > leds.Length) break;
                    _client.UpdateLeds(c.Index, leds.Slice(off, c.LedCount));
                    off += c.LedCount;
                }
            }
            catch (Exception ex)
            {
                var dead = _client;
                _client = null;
                dead.Dispose();
                Log("send failed: " + ex.Message);
                SetStatus(DeviceStatus.Error, "Lost connection to OpenRGB: " + ex.Message);
            }
        }
    }

    public void Disconnect()
    {
        DropClient();
        if (_status != DeviceStatus.Disconnected) SetStatus(DeviceStatus.Disconnected, "Not connected");
    }

    public void Dispose() => Disconnect();
}
