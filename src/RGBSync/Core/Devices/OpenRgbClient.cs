using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

/// <summary>One RGB controller reported by the OpenRGB SDK server (motherboard, GPU, RAM, cooler...).</summary>
public sealed class OpenRgbController
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public int LedCount { get; init; }
    public List<OpenRgbZone> Zones { get; } = new();
    public override string ToString() => $"{Name} ({LedCount} LEDs)";
}

public sealed class OpenRgbZone
{
    public string Name { get; init; } = "";
    public int Index { get; init; }
    public int LedCount { get; init; }
    public int LedsMin { get; init; }
    public int LedsMax { get; init; }
    /// <summary>ARGB headers can be resized to the number of LEDs actually plugged in.</summary>
    public bool Resizable => LedsMax > LedsMin;
    /// <summary>Index of this zone's first LED inside its controller.</summary>
    public int StartLed { get; init; }
}

/// <summary>
/// Minimal client for the OpenRGB SDK server (TCP, default port 6742), protocol version 0.
/// OpenRGB must be running with "SDK Server" started.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private const uint PktRequestControllerCount = 0;
    private const uint PktRequestControllerData = 1;
    private const uint PktDeviceListUpdated = 100;
    private const uint PktSetClientName = 50;
    private const uint PktResizeZone = 1000;
    private const uint PktUpdateLeds = 1050;
    private const uint PktSetCustomMode = 1100;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly object _writeLock = new();
    private Thread? _drainThread;
    private volatile bool _setupDone;

    public bool IsConnected => _tcp?.Connected == true;
    public List<OpenRgbController> Controllers { get; } = new();

    /// <summary>Raised when OpenRGB reports its device list changed (e.g. you resized a zone).</summary>
    public event Action? DeviceListChanged;
    public event Action? Disconnected;

    public async Task ConnectAsync(string host, int port, string clientName, int timeoutMs = 3000)
    {
        Close();
        var tcp = new TcpClient { NoDelay = true };
        using (var cts = new CancellationTokenSource(timeoutMs))
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _stream.ReadTimeout = timeoutMs;
        _setupDone = false;

        SendPacket(0, PktSetClientName, Encoding.ASCII.GetBytes(clientName + "\0"));
        await LoadControllersAsync().ConfigureAwait(false);

        // After setup we only write; a background thread drains anything the server pushes.
        _stream.ReadTimeout = Timeout.Infinite;
        _setupDone = true;
        _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "OpenRGB reader" };
        _drainThread.Start();
    }

    private Task LoadControllersAsync() => Task.Run(() =>
    {
        Controllers.Clear();
        SendPacket(0, PktRequestControllerCount, Array.Empty<byte>());
        var countData = ReadPacket(PktRequestControllerCount);
        int count = (int)BitConverter.ToUInt32(countData, 0);
        for (int i = 0; i < count; i++)
        {
            SendPacket((uint)i, PktRequestControllerData, Array.Empty<byte>());
            var data = ReadPacket(PktRequestControllerData);
            Controllers.Add(ParseController(i, data));
        }
    });

    /// <summary>Puts every controller into "Direct/Custom" mode so it accepts live colours.</summary>
    public void SetCustomMode(int controllerIndex) =>
        SendPacket((uint)controllerIndex, PktSetCustomMode, Array.Empty<byte>());

    /// <summary>Tell OpenRGB how many LEDs are plugged into an ARGB header.</summary>
    public void ResizeZone(int controllerIndex, int zoneIndex, int newSize)
    {
        var data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), zoneIndex);
        BitConverter.TryWriteBytes(data.AsSpan(4, 4), newSize);
        SendPacket((uint)controllerIndex, PktResizeZone, data);
    }

    public void UpdateLeds(int controllerIndex, ReadOnlySpan<Rgb> colors)
    {
        // data_size(u32) + num_colors(u16) + colors(u32 each: R,G,B,0)
        int size = 4 + 2 + colors.Length * 4;
        var buf = new byte[size];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), (uint)size);
        BitConverter.TryWriteBytes(buf.AsSpan(4, 2), (ushort)colors.Length);
        int p = 6;
        foreach (var c in colors)
        {
            buf[p++] = c.R; buf[p++] = c.G; buf[p++] = c.B; buf[p++] = 0;
        }
        SendPacket((uint)controllerIndex, PktUpdateLeds, buf);
    }

    private void SendPacket(uint devIdx, uint pktId, byte[] data)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected to OpenRGB");
        var header = new byte[16];
        header[0] = (byte)'O'; header[1] = (byte)'R'; header[2] = (byte)'G'; header[3] = (byte)'B';
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), devIdx);
        BitConverter.TryWriteBytes(header.AsSpan(8, 4), pktId);
        BitConverter.TryWriteBytes(header.AsSpan(12, 4), (uint)data.Length);
        lock (_writeLock)
        {
            s.Write(header);
            if (data.Length > 0) s.Write(data);
        }
    }

    private byte[] ReadPacket(uint expectedId)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected");
        while (true)
        {
            var header = ReadExact(s, 16);
            if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
                throw new IOException("Bad packet from OpenRGB");
            uint id = BitConverter.ToUInt32(header, 8);
            int len = (int)BitConverter.ToUInt32(header, 12);
            var data = len > 0 ? ReadExact(s, len) : Array.Empty<byte>();
            if (id == expectedId) return data;
            // ignore anything else (e.g. device-list-updated notifications)
        }
    }

    private static byte[] ReadExact(Stream s, int len)
    {
        var buf = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = s.Read(buf, read, len - read);
            if (n <= 0) throw new IOException("OpenRGB closed the connection");
            read += n;
        }
        return buf;
    }

    private void DrainLoop()
    {
        try
        {
            var s = _stream;
            while (s != null && _setupDone)
            {
                var header = ReadExact(s, 16);
                uint id = BitConverter.ToUInt32(header, 8);
                int len = (int)BitConverter.ToUInt32(header, 12);
                if (len > 0) ReadExact(s, len);
                if (id == PktDeviceListUpdated) DeviceListChanged?.Invoke();
            }
        }
        catch
        {
            if (_setupDone) Disconnected?.Invoke();
        }
    }

    // ---- Controller data parsing (protocol version 0) ----

    private static OpenRgbController ParseController(int index, byte[] data)
    {
        var r = new SpanReader(data);
        r.U32(); // data_size
        r.I32(); // type
        string name = r.Str();
        string desc = r.Str();
        r.Str(); // version
        r.Str(); // serial
        string location = r.Str();

        int numModes = r.U16();
        r.I32(); // active mode
        for (int m = 0; m < numModes; m++)
        {
            r.Str();               // name
            r.I32();               // value
            r.U32(); r.U32(); r.U32(); // flags, speed_min, speed_max
            r.U32(); r.U32();      // colors_min, colors_max
            r.U32(); r.U32(); r.U32(); // speed, direction, color_mode
            int nc = r.U16();
            r.Skip(nc * 4);
        }

        var zones = new List<OpenRgbZone>();
        int numZones = r.U16();
        int start = 0;
        for (int z = 0; z < numZones; z++)
        {
            string zname = r.Str();
            r.I32(); // type
            int ledsMin = (int)r.U32();
            int ledsMax = (int)r.U32();
            int ledsCount = (int)r.U32();
            int matrixLen = r.U16();
            r.Skip(matrixLen);
            zones.Add(new OpenRgbZone
            {
                Name = zname, Index = z, LedCount = ledsCount, LedsMin = ledsMin, LedsMax = ledsMax, StartLed = start,
            });
            start += ledsCount;
        }

        int numLeds = r.U16();
        for (int l = 0; l < numLeds; l++) { r.Str(); r.U32(); }

        var c = new OpenRgbController
        {
            Index = index, Name = name, Description = desc, Location = location, LedCount = numLeds,
        };
        c.Zones.AddRange(zones);
        return c;
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _p;
        public SpanReader(ReadOnlySpan<byte> d) { _d = d; _p = 0; }
        public uint U32() { var v = BitConverter.ToUInt32(_d.Slice(_p, 4)); _p += 4; return v; }
        public int I32() { var v = BitConverter.ToInt32(_d.Slice(_p, 4)); _p += 4; return v; }
        public ushort U16() { var v = BitConverter.ToUInt16(_d.Slice(_p, 2)); _p += 2; return v; }
        public void Skip(int n) => _p += n;
        public string Str()
        {
            int len = U16();
            var s = len > 0 ? Encoding.UTF8.GetString(_d.Slice(_p, len)).TrimEnd('\0') : "";
            _p += len;
            return s;
        }
    }

    public void Close()
    {
        _setupDone = false;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
