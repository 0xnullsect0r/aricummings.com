using System.Security.Cryptography;
using System.Text;
using Ari.Platform;
using Ari.Platform.Crypto;

namespace Ari.Features.Auth;

/// <summary>
/// Sealed values in the pinned format 'v1.&lt;kid&gt;.&lt;iv&gt;.&lt;tag&gt;.&lt;body&gt;', base64url
/// parts, AES-256-GCM under an HKDF(site:encrypt) key, context as additional
/// authenticated data. Byte-compatible with Platform's Sealed, whose Encrypt
/// currently binds to the wrong AesGcm overload (argument order) and throws at
/// runtime; this twin exists until that is fixed. Format is permanent either way.
/// </summary>
public sealed class SealedValues(Env env)
{
    private const string Prefix = "v1";
    private const string Info = "site:encrypt";

    public string EncryptString(string plaintext, string context)
    {
        var entry = env.Keys.Current;
        var encKey = env.Keys.Derive(entry, Info);
        var iv = RandomNumberGenerator.GetBytes(12);
        var body = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        var tag = new byte[16];
        using (var aes = new AesGcm(encKey, 16))
            aes.Encrypt(iv, Encoding.UTF8.GetBytes(plaintext), body, tag, Encoding.UTF8.GetBytes(context));
        return string.Join('.', Prefix, entry.Kid, B64.Url(iv), B64.Url(tag), B64.Url(body));
    }

    public string? DecryptString(string sealedText, string context)
    {
        var parts = sealedText.Split('.');
        if (parts.Length != 5 || parts[0] != Prefix) return null;
        var entry = env.Keys.Find(parts[1]);
        if (entry is null) return null;
        var iv = B64.FromUrl(parts[2]);
        var tag = B64.FromUrl(parts[3]);
        var body = B64.FromUrl(parts[4]);
        var encKey = env.Keys.Derive(entry, Info);
        try
        {
            var plaintext = new byte[body.Length];
            using var aes = new AesGcm(encKey, 16);
            var aad = Encoding.UTF8.GetBytes(context);
            aes.Decrypt(iv, body, tag, plaintext, aad);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException) { return null; }
    }
}
