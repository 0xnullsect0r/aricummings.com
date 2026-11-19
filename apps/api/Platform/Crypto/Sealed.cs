using System.Security.Cryptography;
using System.Text;

namespace Ari.Platform.Crypto;

/// <summary>
/// Sealed values, text form 'v1.&lt;kid&gt;.&lt;iv&gt;.&lt;tag&gt;.&lt;body&gt;', each part base64url.
/// Every value carries a context as additional authenticated data, so a sealed value
/// cannot be moved between rows or swapped for another setting. The format is permanent
/// once data exists; changing anything here makes saved values unreadable.
/// </summary>
public sealed class Sealed(KeyRing keys)
{
    private const string Prefix = "v1";
    private const string Info = "site:encrypt";

    public string Encrypt(ReadOnlySpan<byte> plaintext, string context)
    {
        var entry = keys.Current;
        var encKey = keys.Derive(entry, Info);
        var iv = RandomNumberGenerator.GetBytes(12);
        var aad = Encoding.UTF8.GetBytes(context);
        var body = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(encKey, 16))
            aes.Encrypt(nonce: iv, plaintext: plaintext, ciphertext: body, tag: tag, associatedData: aad);
        return string.Join('.', Prefix, entry.Kid, B64.Url(iv), B64.Url(tag), B64.Url(body));
    }

    public string EncryptString(string plaintext, string context) => Encrypt(Encoding.UTF8.GetBytes(plaintext), context);

    public byte[]? Decrypt(string sealedText, string context)
    {
        var parts = sealedText.Split('.');
        if (parts.Length != 5 || parts[0] != Prefix) return null;
        var entry = keys.Find(parts[1]);
        if (entry is null) return null;
        var iv = B64.FromUrl(parts[2]);
        var tag = B64.FromUrl(parts[3]);
        var body = B64.FromUrl(parts[4]);
        var encKey = keys.Derive(entry, Info);
        try
        {
            var plaintext = new byte[body.Length];
            using var aes = new AesGcm(encKey, 16);
            aes.Decrypt(nonce: iv, ciphertext: body, tag: tag, plaintext: plaintext, associatedData: Encoding.UTF8.GetBytes(context));
            return plaintext;
        }
        catch (CryptographicException) { return null; }
    }

    public string? DecryptString(string sealedText, string context)
    {
        var bytes = Decrypt(sealedText, context);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }
}

public static class B64
{
    public static string Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromUrl(string text)
    {
        var body = text.Replace('-', '+').Replace('_', '/');
        if (body.Length % 4 != 0) body += new string('=', 4 - body.Length % 4);
        return Convert.FromBase64String(body);
    }
}
