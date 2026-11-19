using System.Security.Cryptography;
using System.Text;

namespace Ari.Platform.Crypto;

/// <summary>
/// One or more master keys, each with a key id. The last line signs and encrypts; older
/// lines still read, so rotation is adding a line. Derived per-purpose keys via HKDF mean
/// one master key never does two jobs.
/// </summary>
public sealed class KeyRing
{
    public sealed record Entry(string Kid, byte[] Key);

    private readonly List<Entry> _entries;

    private KeyRing(List<Entry> entries) => _entries = entries;

    public Entry Current => _entries[^1];

    public Entry? Find(string kid) => _entries.FirstOrDefault(e => e.Kid == kid);

    public byte[] Derive(Entry entry, string info) => HKDF.DeriveKey(
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        entry.Key, 32, salt: null, info: Encoding.UTF8.GetBytes(info));

    public static KeyRing Load(string? keyFile, string? inline, bool production)
    {
        List<string> lines = new();
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            if (!File.Exists(keyFile))
                throw new InvalidOperationException($"APP_KEY_FILE '{keyFile}' does not exist.");
            lines.AddRange(File.ReadAllLines(keyFile));
        }
        else if (!string.IsNullOrWhiteSpace(inline))
        {
            lines.AddRange(inline.Split('\n'));
        }
        else if (production)
        {
            throw new InvalidOperationException("No key configured. Production refuses to start without APP_KEY_FILE or APP_KEYS.");
        }
        else
        {
            // Dev-only key, generated per boot, named so nobody mistakes it for a real one.
            lines.Add($"dev:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        var entries = new List<Entry>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0 || colon == line.Length - 1)
                throw new InvalidOperationException("Key lines must look like 'kid:base64'.");
            var kid = line[..colon];
            var body = line[(colon + 1)..].TrimEnd().Replace('-', '+').Replace('_', '/');
            if (body.Length % 4 != 0) body += new string('=', 4 - body.Length % 4);
            byte[] key;
            try { key = Convert.FromBase64String(body); }
            catch (FormatException) { throw new InvalidOperationException($"Key '{kid}' is not valid base64."); }
            if (key.Length < 32)
                throw new InvalidOperationException($"Key '{kid}' is shorter than 32 bytes.");
            entries.Add(new Entry(kid, key));
        }
        if (entries.Count == 0)
            throw new InvalidOperationException("Key file held no keys.");
        return new KeyRing(entries);
    }
}
