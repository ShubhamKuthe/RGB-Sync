using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

/// <summary>A persistent local connection to one Tuya Wi-Fi device.</summary>
public sealed class TuyaClient : IDisposable
{
    private readonly string _ip;
    private readonly string _devId;
    private readonly byte[] _localKey;
    private readonly string _version;
    private byte[] _sessionKey;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private uint _seq = 1;
    private readonly object _writeLock = new();
    private Thread? _reader;
    private volatile bool _running;

    public event Action<string>? Error;
    /// <summary>Raw JSON status pushed by the device (useful for the "Detect DPS" helper).</summary>
    public event Action<string>? StatusReceived;

    public TuyaClient(string ip, string devId, string localKey, string version)
    {
        _ip = ip.Trim();
        _devId = devId.Trim();
        _localKey = Encoding.UTF8.GetBytes(localKey.Trim());
        if (_localKey.Length != 16) throw new ArgumentException("The local key must be exactly 16 characters.");
        _version = version is "3.4" or "3.5" ? version : "3.3";
        _sessionKey = _localKey;
    }

    public int Port { get; set; } = 6668;

    public bool IsConnected => _running && _tcp?.Connected == true;

    public async Task ConnectAsync(int timeoutMs = 8000)
    {
        var tcp = new TcpClient { NoDelay = true };
        using (var cts = new CancellationTokenSource(timeoutMs))
        {
            await tcp.ConnectAsync(_ip, Port, cts.Token).ConfigureAwait(false);
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _stream.ReadTimeout = timeoutMs;

        if (_version is "3.4" or "3.5")
            await Task.Run(NegotiateSessionKey).ConfigureAwait(false);

        _stream.ReadTimeout = Timeout.Infinite;
        _stream.WriteTimeout = 3000;
        try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        _running = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Tuya reader" };
        _reader.Start();
    }

    private void NegotiateSessionKey()
    {
        var localNonce = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());

        SendRaw(TuyaProtocol.CmdSessKeyNegStart,
            _version == "3.4" ? TuyaProtocol.EcbEncrypt(_localKey, localNonce) : localNonce, _localKey);

        var resp = ReadMessage(_localKey);
        if (resp.Cmd != TuyaProtocol.CmdSessKeyNegResp || resp.Payload.Length < 48)
            throw new IOException("Device rejected the handshake - check the local key and protocol version.");

        var payload = resp.Payload;
        // some firmwares put a 4-byte return code in front of the nonce
        if (payload.Length >= 52 && payload[0] == 0 && payload[1] == 0 && payload[2] == 0) payload = payload[4..];

        var remoteNonce = payload.AsSpan(0, 16).ToArray();
        var expected = TuyaProtocol.HmacSha256(_localKey, localNonce);
        if (!payload.AsSpan(16, 32).SequenceEqual(expected))
            throw new IOException("Handshake check failed - the local key is wrong.");

        var finish = TuyaProtocol.HmacSha256(_localKey, remoteNonce);
        SendRaw(TuyaProtocol.CmdSessKeyNegFinish,
            _version == "3.4" ? TuyaProtocol.EcbEncrypt(_localKey, finish) : finish, _localKey);

        var xor = new byte[16];
        for (int i = 0; i < 16; i++) xor[i] = (byte)(localNonce[i] ^ remoteNonce[i]);
        _sessionKey = _version == "3.4"
            ? TuyaProtocol.EcbEncrypt(_localKey, xor, pad: false)
            : TuyaProtocol.GcmEncrypt(_localKey, localNonce.AsSpan(0, 12).ToArray(), xor, null).AsSpan(12, 16).ToArray();
    }

    /// <summary>Set one or more data points, e.g. { "20": true, "24": "00f003e803e8" }.</summary>
    public void SetDps(IDictionary<string, object> dps)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string json;
        uint cmd;
        if (_version == "3.3")
        {
            cmd = TuyaProtocol.CmdControl;
            json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["devId"] = _devId, ["uid"] = _devId, ["t"] = t.ToString(), ["dps"] = dps,
            });
        }
        else
        {
            cmd = TuyaProtocol.CmdControlNew;
            json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["protocol"] = 5, ["t"] = t, ["data"] = new Dictionary<string, object> { ["dps"] = dps },
            });
        }
        Send(cmd, json);
    }

    public void Heartbeat() => Send(TuyaProtocol.CmdHeartbeat, "{}");

    /// <summary>Ask for current status (device answers via StatusReceived).</summary>
    public void QueryStatus()
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["gwId"] = _devId, ["devId"] = _devId, ["uid"] = _devId, ["t"] = t.ToString(),
        });
        Send(_version == "3.3" ? TuyaProtocol.CmdDpQuery : 16u, _version == "3.3" ? json : "{}");
    }

    private void Send(uint cmd, string json)
    {
        var payload = TuyaProtocol.BuildPayload(_version, cmd, json, _sessionKey);
        SendRaw(cmd, payload, _sessionKey);
    }

    private void SendRaw(uint cmd, byte[] payload, byte[] key)
    {
        var s = _stream ?? throw new IOException("Not connected");
        lock (_writeLock)
        {
            var frame = TuyaProtocol.Pack(_version, _seq++, cmd, payload, key);
            s.Write(frame);
        }
    }

    private TuyaProtocol.Message ReadMessage(byte[] key)
    {
        var s = _stream ?? throw new IOException("Not connected");
        var prefix = ReadExact(s, 4);
        uint p = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        byte[] frame;
        if (p == TuyaProtocol.Prefix6699)
        {
            var rest = ReadExact(s, 14);
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(rest.AsSpan(10));
            var body = ReadExact(s, len + 4);
            frame = prefix.Concat(rest).Concat(body).ToArray();
        }
        else if (p == TuyaProtocol.Prefix55AA)
        {
            var rest = ReadExact(s, 12);
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(rest.AsSpan(8));
            var body = ReadExact(s, len);
            frame = prefix.Concat(rest).Concat(body).ToArray();
        }
        else throw new IOException("Unexpected data from device");
        return TuyaProtocol.Unpack(_version, frame, key);
    }

    private static byte[] ReadExact(Stream s, int len)
    {
        var buf = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = s.Read(buf, read, len - read);
            if (n <= 0) throw new IOException("Device closed the connection");
            read += n;
        }
        return buf;
    }

    private void ReadLoop()
    {
        int badFrames = 0;
        try
        {
            while (_running)
            {
                TuyaProtocol.Message msg;
                try
                {
                    msg = ReadMessage(_sessionKey);
                    badFrames = 0;
                }
                catch (Exception ex) when (ex is not IOException && ex is not ObjectDisposedException && ex is not SocketException)
                {
                    // one frame we couldn't decode (odd reply/ack) - the stream is still in sync, keep going
                    if (++badFrames > 20) throw;
                    continue;
                }
                if (msg.Payload.Length > 0 && msg.Cmd is TuyaProtocol.CmdStatus or TuyaProtocol.CmdDpQuery or 16)
                {
                    try
                    {
                        var text = Encoding.UTF8.GetString(msg.Payload);
                        int i = text.IndexOf('{');
                        if (i >= 0) StatusReceived?.Invoke(text[i..]);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                _running = false;
                Error?.Invoke(ex.Message);
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }
}
