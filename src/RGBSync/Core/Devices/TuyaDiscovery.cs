using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

public sealed record TuyaFound(string Ip, string DeviceId, string Version, string ProductKey);

/// <summary>
/// Tuya Wi-Fi devices shout "I'm here" on the local network every few seconds (UDP 6666 / 6667 / 7000).
/// Listening for those gives us each device's IP, ID and protocol version - no cloud needed.
/// (The local key is the only thing that still has to come from Tuya.)
/// </summary>
public static class TuyaDiscovery
{
    private static readonly byte[] UdpKey = MD5.HashData(Encoding.ASCII.GetBytes("yGAdlopoPVldABfn"));

    public static async Task<List<TuyaFound>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = new Dictionary<string, TuyaFound>();
        var sockets = new List<UdpClient>();
        foreach (var port in new[] { 6666, 6667, 7000 })
        {
            try
            {
                var u = new UdpClient();
                u.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                u.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                sockets.Add(u);
            }
            catch
            {
                // port busy (e.g. another Tuya app running) - skip it
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var tasks = sockets.Select(s => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await s.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch { break; }
                var dev = Parse(r.Buffer, r.RemoteEndPoint.Address.ToString());
                if (dev != null) lock (found) found[dev.DeviceId] = dev;
            }
        })).ToArray();

        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        foreach (var s in sockets) s.Dispose();
        lock (found) return found.Values.OrderBy(d => d.Ip).ToList();
    }

    /// <summary>
    /// Backup method that works even when the firewall blocks broadcasts: try to open Tuya's
    /// control port (6668) on every address of the PC's local /24 networks.
    /// </summary>
    public static async Task<List<string>> SweepAsync(int timeoutMs = 700)
    {
        var targets = new List<IPAddress>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // no DHCP address
                for (int h = 1; h < 255; h++)
                {
                    if (h == b[3]) continue;
                    targets.Add(new IPAddress(new byte[] { b[0], b[1], b[2], (byte)h }));
                }
            }
        }

        var hits = new List<string>();
        using var gate = new SemaphoreSlim(128);
        var tasks = targets.Distinct().Select(async ip =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(timeoutMs);
                await tcp.ConnectAsync(ip, 6668, cts.Token).ConfigureAwait(false);
                lock (hits) hits.Add(ip.ToString());
            }
            catch { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return hits.OrderBy(h => Version.Parse(h)).ToList();
    }

    /// <summary>
    /// Deep search for one device by MAC: ping/probe every address in the PC's real subnet (up to 4096),
    /// then look the MAC up in Windows' ARP table. Returns the IP (or null) plus a readable report.
    /// </summary>
    public static async Task<(string? Ip, bool PortOpen, string Report)> FindByMacAsync(string mac)
    {
        var sb = new StringBuilder();
        string want = new string(mac.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var targets = new List<IPAddress>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                if (ip[0] == 169 && ip[1] == 254) continue;
                uint a = BinaryPrimitives.ReadUInt32BigEndian(ip);
                uint m = BinaryPrimitives.ReadUInt32BigEndian(ua.IPv4Mask.GetAddressBytes());
                int prefix = System.Numerics.BitOperations.PopCount(m);
                sb.AppendLine($"PC adapter '{ni.Name}' ({ni.NetworkInterfaceType}): {ua.Address}/{prefix}, gateway " +
                              string.Join(",", ni.GetIPProperties().GatewayAddresses.Select(g => g.Address)));
                if (prefix < 20) { m = 0xFFFFF000; prefix = 20; sb.AppendLine("  (large network - searching the nearest /20 only)"); }
                uint net = a & m, bc = net | ~m;
                for (uint h = net + 1; h < bc; h++)
                {
                    if (h == a) continue;
                    var b = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(b, h);
                    targets.Add(new IPAddress(b));
                }
            }
        }
        sb.AppendLine($"Probing {targets.Count} addresses...");

        int alive = 0;
        var open6668 = new List<string>();
        using (var gate = new SemaphoreSlim(256))
        {
            await Task.WhenAll(targets.Distinct().Select(async ip =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var r = await ping.SendPingAsync(ip, 400).ConfigureAwait(false);
                    if (r.Status == System.Net.NetworkInformation.IPStatus.Success) Interlocked.Increment(ref alive);
                }
                catch { }
                try
                {
                    using var tcp = new TcpClient();
                    using var cts = new CancellationTokenSource(400);
                    await tcp.ConnectAsync(ip, 6668, cts.Token).ConfigureAwait(false);
                    lock (open6668) open6668.Add(ip.ToString());
                }
                catch { }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }
        sb.AppendLine($"Devices answering ping: {alive}. Answering on Tuya port 6668: {(open6668.Count == 0 ? "none" : string.Join(", ", open6668))}");

        // ARP table lookup
        string? found = null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string arp = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            int entries = 0;
            foreach (var line in arp.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _)) continue;
                entries++;
                string m2 = new string(parts[1].ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                if (m2 == want) found = parts[0];
            }
            sb.AppendLine($"ARP table entries: {entries}. Strip MAC {(found != null ? "FOUND at " + found : "not seen")}.");
        }
        catch (Exception ex) { sb.AppendLine("ARP lookup failed: " + ex.Message); }

        bool portOpen = found != null && open6668.Contains(found);
        if (found != null && !portOpen)
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(1500);
                await tcp.ConnectAsync(found, 6668, cts.Token).ConfigureAwait(false);
                portOpen = true;
            }
            catch { }
        }
        sb.AppendLine($"Result: {(found == null ? "strip not reachable from this PC" : $"strip at {found}, port 6668 {(portOpen ? "OPEN" : "closed")}")}");
        return (found, portOpen, sb.ToString());
    }

    public static TuyaFound? Parse(byte[] data, string fromIp)
    {
        try
        {
            string? json = null;
            uint prefix = BinaryPrimitives.ReadUInt32BigEndian(data);
            if (prefix == TuyaProtocol.Prefix55AA)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
                var body = data.AsSpan(16, len - 8).ToArray(); // minus crc + suffix
                if (body.Length >= 4 && body[0] == 0 && body[1] == 0) body = body[4..]; // return code
                json = TryText(body) ?? TryText(SafeEcb(body));
            }
            else if (prefix == TuyaProtocol.Prefix6699)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(14));
                var plain = TuyaProtocol.GcmDecrypt(UdpKey, data.AsSpan(18, len), data.AsSpan(4, 14).ToArray());
                json = TryText(plain) ?? (plain.Length > 4 ? TryText(plain[4..]) : null);
            }
            if (json == null) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string id = root.TryGetProperty("gwId", out var g) ? g.GetString() ?? "" : "";
            if (id == "") return null;
            string ip = root.TryGetProperty("ip", out var i) ? i.GetString() ?? fromIp : fromIp;
            string ver = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            string pk = root.TryGetProperty("productKey", out var p) ? p.GetString() ?? "" : "";
            return new TuyaFound(ip, id, ver, pk);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] SafeEcb(byte[] body)
    {
        try { return TuyaProtocol.EcbDecrypt(UdpKey, body); } catch { return Array.Empty<byte>(); }
    }

    private static string? TryText(byte[] b)
    {
        if (b.Length == 0) return null;
        var s = Encoding.UTF8.GetString(b).Trim('\0', ' ');
        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : null;
    }
}
