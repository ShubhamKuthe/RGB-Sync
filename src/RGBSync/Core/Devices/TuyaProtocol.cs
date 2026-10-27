using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RGBSync.Core.Devices;

/// <summary>
/// Tuya local LAN protocol (port 6668) message framing + crypto for versions 3.3, 3.4 and 3.5.
/// Calex Smart lights are Tuya devices, so this is how we talk to them without the phone app.
/// </summary>
public static class TuyaProtocol
{
    public const uint Prefix55AA = 0x000055AA;
    public const uint Suffix55AA = 0x0000AA55;
    public const uint Prefix6699 = 0x00006699;
    public const uint Suffix6699 = 0x00009966;

    public const uint CmdSessKeyNegStart = 3;
    public const uint CmdSessKeyNegResp = 4;
    public const uint CmdSessKeyNegFinish = 5;
    public const uint CmdControl = 7;
    public const uint CmdStatus = 8;
    public const uint CmdHeartbeat = 9;
    public const uint CmdDpQuery = 10;
    public const uint CmdControlNew = 13;

    public sealed record Message(uint Seq, uint Cmd, uint? RetCode, byte[] Payload);

    // ---------- AES helpers ----------

    public static byte[] EcbEncrypt(byte[] key, byte[] data, bool pad = true)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, pad ? PaddingMode.PKCS7 : PaddingMode.None);
    }

    public static byte[] EcbDecrypt(byte[] key, byte[] data, bool pad = true)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, pad ? PaddingMode.PKCS7 : PaddingMode.None);
    }

    /// <summary>Returns iv(12) + ciphertext + tag(16).</summary>
    public static byte[] GcmEncrypt(byte[] key, byte[] iv, byte[] plain, byte[]? aad)
    {
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Encrypt(iv, plain, ct, tag, aad);
        }
        var result = new byte[12 + ct.Length + 16];
        Buffer.BlockCopy(iv, 0, result, 0, 12);
        Buffer.BlockCopy(ct, 0, result, 12, ct.Length);
        Buffer.BlockCopy(tag, 0, result, 12 + ct.Length, 16);
        return result;
    }

    public static byte[] GcmDecrypt(byte[] key, ReadOnlySpan<byte> ivCtTag, byte[]? aad)
    {
        var iv = ivCtTag.Slice(0, 12);
        var ct = ivCtTag.Slice(12, ivCtTag.Length - 28);
        var tag = ivCtTag.Slice(ivCtTag.Length - 16, 16);
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(iv, ct, tag, plain, aad);
        return plain;
    }

    public static byte[] HmacSha256(byte[] key, ReadOnlySpan<byte> data) => HMACSHA256.HashData(key, data);

    // ---------- CRC32 (for 3.3) ----------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    // ---------- Packing ----------

    /// <summary>Frame a message. For 3.3 the payload must already be encrypted; for 3.4 too (hmacKey used);
    /// for 3.5 the payload is plaintext and is GCM-encrypted with key here.</summary>
    public static byte[] Pack(string version, uint seq, uint cmd, byte[] payload, byte[] key)
    {
        if (version == "3.5")
        {
            // header: prefix(4) unknown(2) seq(4) cmd(4) len(4)
            var header = new byte[18];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), Prefix6699);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6), seq);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(10), cmd);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(14), (uint)(payload.Length + 12 + 16));
            var iv = RandomNumberGenerator.GetBytes(12);
            var enc = GcmEncrypt(key, iv, payload, header.AsSpan(4).ToArray());
            var msg = new byte[18 + enc.Length + 4];
            Buffer.BlockCopy(header, 0, msg, 0, 18);
            Buffer.BlockCopy(enc, 0, msg, 18, enc.Length);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(18 + enc.Length), Suffix6699);
            return msg;
        }

        bool hmac = version == "3.4";
        int tailLen = hmac ? 32 + 4 : 4 + 4;
        var buf = new byte[16 + payload.Length + tailLen];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), Prefix55AA);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), seq);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), cmd);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), (uint)(payload.Length + tailLen));
        Buffer.BlockCopy(payload, 0, buf, 16, payload.Length);
        int p = 16 + payload.Length;
        if (hmac)
        {
            var mac = HmacSha256(key, buf.AsSpan(0, p));
            Buffer.BlockCopy(mac, 0, buf, p, 32);
            p += 32;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), Crc32(buf.AsSpan(0, p)));
            p += 4;
        }
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p), Suffix55AA);
        return buf;
    }

    /// <summary>Unpack a full frame received from the device and decrypt its payload.</summary>
    public static Message Unpack(string version, byte[] frame, byte[] key)
    {
        uint prefix = BinaryPrimitives.ReadUInt32BigEndian(frame);
        if (prefix == Prefix6699)
        {
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(6));
            uint cmd = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(10));
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(14));
            var plain = GcmDecrypt(key, frame.AsSpan(18, len), frame.AsSpan(4, 14).ToArray());
            uint? ret = null;
            // device replies usually start with a 4-byte return code
            if (plain.Length >= 4 && plain[0] == 0 && plain[1] == 0 && plain[2] == 0 && plain[3] <= 1)
            {
                ret = BinaryPrimitives.ReadUInt32BigEndian(plain);
                plain = plain[4..];
            }
            return new Message(seq, cmd, ret, plain);
        }

        if (prefix != Prefix55AA) throw new InvalidOperationException("Unknown Tuya frame prefix");
        {
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4));
            uint cmd = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8));
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(12));
            int tail = version == "3.4" ? 36 : 8;
            var body = frame.AsSpan(16, len - tail).ToArray();
            uint? ret = null;
            if (body.Length % 16 == 4 || (version != "3.4" && body.Length >= 4 && body[0] == 0 && body[1] == 0 && body[2] == 0))
            {
                ret = BinaryPrimitives.ReadUInt32BigEndian(body);
                body = body[4..];
            }
            if (body.Length == 0) return new Message(seq, cmd, ret, body);

            if (version == "3.4")
            {
                body = TryDecrypt(key, body);
            }
            else if (version == "3.3")
            {
                if (body.Length > 15 && body[0] == '3' && body[1] == '.' && body[2] == '3') body = body[15..];
                body = TryDecrypt(key, body);
            }
            return new Message(seq, cmd, ret, body);
        }
    }

    private static byte[] TryDecrypt(byte[] key, byte[] body)
    {
        if (body.Length % 16 != 0) return body;
        try { return EcbDecrypt(key, body); }
        catch (CryptographicException) { return body; }
    }

    /// <summary>Build the (encrypted for 3.3/3.4, plain for 3.5) payload for a command.</summary>
    public static byte[] BuildPayload(string version, uint cmd, string json, byte[] key)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        bool versionHeader = cmd == CmdControl || cmd == CmdControlNew;
        if (version == "3.3")
        {
            var enc = EcbEncrypt(key, raw);
            if (!versionHeader) return enc;
            var withHeader = new byte[15 + enc.Length];
            Encoding.ASCII.GetBytes("3.3").CopyTo(withHeader, 0);
            Buffer.BlockCopy(enc, 0, withHeader, 15, enc.Length);
            return withHeader;
        }

        // 3.4 / 3.5: header goes inside the encryption
        byte[] plain = raw;
        if (versionHeader)
        {
            plain = new byte[15 + raw.Length];
            Encoding.ASCII.GetBytes(version).CopyTo(plain, 0);
            Buffer.BlockCopy(raw, 0, plain, 15, raw.Length);
        }
        return version == "3.4" ? EcbEncrypt(key, plain) : plain;
    }
}
