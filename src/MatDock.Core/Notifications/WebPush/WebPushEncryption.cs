using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MatDock.Core.Notifications.WebPush;

/// <summary>
/// The encryption of a Web Push message (taken over from Matmon and MatMail, where it is tested against the RFC): RFC 8291 (the keys of the subscription and an ephemeral key of the server give the
/// secret) in the aes128gcm content coding of RFC 8188 (one record). The push service of the browser only carries bytes; only the
/// device that owns the subscription can read them.
/// </summary>
public static class WebPushEncryption
{
    /// <summary>The record size announced in the header. A push service accepts 4096 bytes in all.</summary>
    public const int RecordSize = 4096;

    private const int HeaderBytes = 16 + 4 + 1 + 65;   // salt, record size, key id length, the key id (the server's public key)
    private const int TagBytes = 16;

    /// <summary>The most a message may carry: 4096 bytes less the header, the tag and the one byte that ends the last record.</summary>
    public const int MaxPlaintextBytes = RecordSize - HeaderBytes - TagBytes - 1;

    /// <summary>Encrypts a message for a subscription (its public key and its auth secret, both decoded).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> receiverPublicKey, ReadOnlySpan<byte> authSecret)
    {
        using ECDiffieHellman sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, receiverPublicKey, authSecret, sender, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The same with the key and the salt given (the test of the RFC brings its own).</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> receiverPublicKey, ReadOnlySpan<byte> authSecret, ECDiffieHellman sender, ReadOnlySpan<byte> salt)
    {
        if (plaintext.Length > MaxPlaintextBytes)
        {
            throw new ArgumentException($"A push message may carry {MaxPlaintextBytes} bytes at most.", nameof(plaintext));
        }

        if (receiverPublicKey.Length != 65 || receiverPublicKey[0] != 0x04)
        {
            throw new ArgumentException("The key of the device is not an uncompressed P-256 point.", nameof(receiverPublicKey));
        }

        if (authSecret.Length != 16)
        {
            throw new ArgumentException("The auth secret of a subscription has 16 bytes.", nameof(authSecret));
        }

        byte[] senderPublic = UncompressedPoint(sender);
        using ECDiffieHellman receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = receiverPublicKey[1..33].ToArray(), Y = receiverPublicKey[33..65].ToArray() },
        });

        byte[] secret = sender.DeriveRawSecretAgreement(receiver.PublicKey);
        (byte[] key, byte[] nonce) = DeriveKeys(secret, authSecret, salt, receiverPublicKey, senderPublic);

        // One record: the message, then 0x02 as the mark of the last record (RFC 8188 section 2).
        byte[] record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record);
        record[^1] = 0x02;

        byte[] output = new byte[HeaderBytes + record.Length + TagBytes];
        salt.CopyTo(output);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(16, 4), RecordSize);
        output[20] = 65;
        senderPublic.CopyTo(output, 21);

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, record, output.AsSpan(HeaderBytes, record.Length), output.AsSpan(HeaderBytes + record.Length, TagBytes));
        return output;
    }

    /// <summary>The content encryption key and the nonce (RFC 8291 section 3.4 and RFC 8188 section 2.2/2.3).</summary>
    internal static (byte[] Key, byte[] Nonce) DeriveKeys(byte[] secret, ReadOnlySpan<byte> authSecret, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> receiverPublic, ReadOnlySpan<byte> senderPublic)
    {
        byte[] prkKey = HKDF.Extract(HashAlgorithmName.SHA256, secret, authSecret.ToArray());
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. receiverPublic, .. senderPublic];
        byte[] ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        byte[] prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        byte[] key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        byte[] nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        return (key, nonce);
    }

    /// <summary>The public key as the 65 bytes the protocol uses: 0x04, X, Y.</summary>
    internal static byte[] UncompressedPoint(ECDiffieHellman key)
    {
        ECParameters parameters = key.ExportParameters(false);
        byte[] point = new byte[65];
        point[0] = 0x04;
        parameters.Q.X!.CopyTo(point, 1 + 32 - parameters.Q.X.Length);
        parameters.Q.Y!.CopyTo(point, 33 + 32 - parameters.Q.Y.Length);
        return point;
    }

    /// <summary>base64url without padding, the form the Web Push specifications use for keys and secrets.</summary>
    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The bytes of a base64url text (padding optional); null when it is not valid.</summary>
    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2000)
        {
            return null;
        }

        string padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
