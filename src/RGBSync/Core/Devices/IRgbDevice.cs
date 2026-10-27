using System;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

public enum DeviceStatus { Disconnected, Connecting, Connected, Error }

/// <summary>Anything we can push colours to: the PC (via OpenRGB) or a room light (via Tuya).</summary>
public interface IRgbDevice : IDisposable
{
    string Name { get; }
    DeviceStatus Status { get; }
    string StatusText { get; }

    /// <summary>Number of individually addressable LEDs (1 for a single-colour light).</summary>
    int LedCount { get; }

    /// <summary>How many frames per second this device can realistically take.</summary>
    int MaxFps { get; }

    Task ConnectAsync();
    void Disconnect();

    /// <summary>Push a frame. Length should equal LedCount. Must be safe to call from the engine thread.</summary>
    void Send(ReadOnlySpan<Rgb> leds);

    event Action? StatusChanged;
}
