using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RGBSync.Core.Devices;

/// <summary>
/// Minimal Tuya OpenAPI client - only used once, to fetch a device's local key
/// (the same thing iot.tuya.com's API Explorer shows).
/// </summary>
public static class TuyaCloud
{
    public static readonly (string Name, string Host)[] Regions =
    {
        ("Central Europe", "https://openapi.tuyaeu.com"),
        ("Western Europe", "https://openapi-weaz.tuyaeu.com"),
        ("America", "https://openapi.tuyaus.com"),
        ("China", "https://openapi.tuyacn.com"),
        ("India", "https://openapi.tuyain.com"),
    };

    public sealed record DeviceInfo(string LocalKey, string Name, string Ip);

    public static async Task<DeviceInfo> GetDeviceAsync(string host, string accessId, string secret, string deviceId)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var tokenJson = await SendAsync(http, host, accessId, secret, null, "/v1.0/token?grant_type=1").ConfigureAwait(false);
        string token = tokenJson.GetProperty("result").GetProperty("access_token").GetString()!;

        var dev = await SendAsync(http, host, accessId, secret, token, $"/v1.0/devices/{deviceId}").ConfigureAwait(false);
        var r = dev.GetProperty("result");
        string key = r.TryGetProperty("local_key", out var k) ? k.GetString() ?? "" : "";
        string name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string ip = r.TryGetProperty("ip", out var i) ? i.GetString() ?? "" : "";
        if (key == "") throw new InvalidOperationException("Tuya didn't return a local key for that device.");
        return new DeviceInfo(key, name, ip);
    }

    private static async Task<JsonElement> SendAsync(HttpClient http, string host, string id, string secret, string? token, string path)
    {
        string t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string nonce = Guid.NewGuid().ToString("N");
        string emptyBodyHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        string stringToSign = $"GET\n{emptyBodyHash}\n\n{path}";
        string signStr = id + (token ?? "") + t + nonce + stringToSign;
        string sign = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signStr)));

        using var req = new HttpRequestMessage(HttpMethod.Get, host + path);
        req.Headers.Add("client_id", id);
        req.Headers.Add("sign", sign);
        req.Headers.Add("t", t);
        req.Headers.Add("nonce", nonce);
        req.Headers.Add("sign_method", "HMAC-SHA256");
        if (token != null) req.Headers.Add("access_token", token);

        using var resp = await http.SendAsync(req).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
        {
            string msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : body;
            throw new InvalidOperationException("Tuya says: " + msg);
        }
        return root;
    }
}
