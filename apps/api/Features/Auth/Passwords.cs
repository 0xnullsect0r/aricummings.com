using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Ari.Features.Auth;

/// <summary>
/// Argon2id in PHC format, m=19456 t=2 p=1, 16-byte salt, 32-byte hash. Parameters live
/// in the stored string so they can change later. A semaphore of 4: each hash costs
/// 19 MB and a burst must queue, not exhaust memory.
/// </summary>
public sealed class PasswordHasher
{
    private const int MemoryKib = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private static readonly SemaphoreSlim Gate = new(4, 4);

    /// <summary>Total hashes performed. Tests use it to prove timing parity between
    /// the unknown-email and wrong-password paths: both must cost exactly one hash.</summary>
    internal long HashCount;

    public string Hash(string password) => Hash(password, count: true);

    /// <summary>The timing-parity dummy: a real hash that must not move the counter,
    /// or per-request hash-count assertions would see it.</summary>
    internal string HashUncounted(string password) => Hash(password, count: false);

    private string Hash(string password, bool count)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var digest = Compute(password, salt, MemoryKib, Iterations, Parallelism, count);
        return string.Join('$', "",
            "argon2id",
            $"v=19",
            $"m={MemoryKib},t={Iterations},p={Parallelism}",
            B64(salt),
            B64(digest));
    }

    public bool Verify(string password, string phcString)
    {
        var parts = phcString.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19") return false;
        var parameters = parts[2].Split(',');
        if (parameters.Length != 3) return false;
        int m = 0, t = 0, p = 0;
        foreach (var parameter in parameters)
        {
            var (name, raw) = parameter.Split('=') is { Length: 2 } kv ? (kv[0], kv[1]) : ("", "");
            if (name == "m") int.TryParse(raw, out m);
            else if (name == "t") int.TryParse(raw, out t);
            else if (name == "p") int.TryParse(raw, out p);
        }
        if (m is < 8 or > 1_048_576 || t is < 1 or > 32 || p is < 1 or > 16) return false;
        byte[] salt, expected;
        try
        {
            salt = Unb64(parts[3]);
            expected = Unb64(parts[4]);
        }
        catch (FormatException) { return false; }
        if (salt.Length is < 8 or > 64 || expected.Length is < 16 or > 128) return false;

        var actual = Compute(password, salt, m, t, p, count: true);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private byte[] Compute(string password, byte[] salt, int memoryKib, int iterations, int parallelism, bool count)
    {
        Gate.Wait();
        try
        {
            if (count) Interlocked.Increment(ref HashCount);
            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                MemorySize = memoryKib,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };
            return argon2.GetBytes(HashSize);
        }
        finally { Gate.Release(); }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Unb64(string text)
    {
        var body = text.Replace('-', '+').Replace('_', '/');
        if (body.Length % 4 != 0) body += new string('=', 4 - body.Length % 4);
        return Convert.FromBase64String(body);
    }
}

/// <summary>Password rules: 12 to 128 characters, at least 5 distinct characters,
/// never the email's local part, never a common word. Field problems, not exceptions,
/// so the account screen can show them.</summary>
public static class PasswordRules
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly string[] CommonWords =
    {
        "password", "qwerty", "123456", "letmein", "welcome", "aricummings", "administrator", "iloveyou",
    };

    public static List<string> Problems(string password, string email)
    {
        var problems = new List<string>();
        if (password.Length < MinLength) problems.Add($"must be at least {MinLength} characters");
        if (password.Length > MaxLength) problems.Add($"must be at most {MaxLength} characters");
        if (password.Distinct().Count() < 5) problems.Add("must use at least 5 different characters");

        var lowered = password.ToLowerInvariant();
        var local = email.Split('@')[0];
        if (local.Length >= 3 && lowered.Contains(local.ToLowerInvariant()))
            problems.Add("must not contain the email address");
        foreach (var word in CommonWords)
            if (lowered.Contains(word))
            {
                problems.Add("is too common a word");
                break;
            }
        return problems;
    }
}
