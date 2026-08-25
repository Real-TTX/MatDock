using System.Security.Cryptography;

namespace MatDock.Core.Ssh;

/// <summary>Generates an SSH key pair for environments where password login is not allowed.</summary>
public static class SshKeygen
{
    public sealed record KeyPair(string PrivateKeyPem, string PublicKeyOpenSsh);

    /// <summary>
    /// Creates an RSA key pair. The private key is PKCS#1 PEM (readable by SSH.NET); the public key is
    /// in the OpenSSH <c>authorized_keys</c> one-line format for the user to install on the host.
    /// </summary>
    public static KeyPair Generate(string comment = "matdock")
    {
        using var rsa = RSA.Create(3072);
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        var publicKey = BuildOpenSshPublicKey(rsa, string.IsNullOrWhiteSpace(comment) ? "matdock" : comment.Trim());
        return new KeyPair(privatePem, publicKey);
    }

    private static string BuildOpenSshPublicKey(RSA rsa, string comment)
    {
        var p = rsa.ExportParameters(includePrivateParameters: false);
        using var ms = new MemoryStream();
        WriteLengthPrefixed(ms, System.Text.Encoding.ASCII.GetBytes("ssh-rsa"));
        WriteMpint(ms, p.Exponent!);
        WriteMpint(ms, p.Modulus!);
        var blob = Convert.ToBase64String(ms.ToArray());
        return $"ssh-rsa {blob} {comment}";
    }

    private static void WriteLengthPrefixed(Stream stream, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        stream.Write(len);
        stream.Write(data);
    }

    private static void WriteMpint(Stream stream, byte[] value)
    {
        // SSH mpint: big-endian, prefixed with 0x00 when the high bit is set (to keep it positive).
        if (value.Length > 0 && (value[0] & 0x80) != 0)
        {
            var padded = new byte[value.Length + 1];
            Array.Copy(value, 0, padded, 1, value.Length);
            WriteLengthPrefixed(stream, padded);
        }
        else
        {
            WriteLengthPrefixed(stream, value);
        }
    }
}
