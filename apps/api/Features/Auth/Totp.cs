using System.Security.Cryptography;
using System.Text;
using Ari.Platform;
using Microsoft.Data.Sqlite;
using Ari.Platform.Database;
using Dapper;
using OtpNet;
using QRCoder;

namespace Ari.Features.Auth;

/// <summary>
/// TOTP: SHA-1, 6 digits, 30 s, one step either side. The secret is sealed with
/// context totp:&lt;userId&gt; and stays pending until the person proves their app works.
/// Each code works once; the conditional update stops two simultaneous requests
/// spending the same step. Recovery codes are stored hashed and single use.
/// </summary>
public sealed class TotpService(ApiContext api, SealedValues sealedValues)
{
    private const string Issuer = "aricummings.com";

    private static readonly char[] Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();

    public static string Context(long userId) => $"totp:{userId}";

    /// <summary>Creates (or recreates) a pending secret. Returns the base32 secret,
    /// the otpauth URL and the QR as a plain SVG drawn from the modules.</summary>
    public async Task<StartResult> Start(long userId, string email)
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        var base32 = Base32Encoding.ToString(secret);
        var sealedSecret = sealedValues.EncryptString(base32, Context(userId));
        await using var connection = api.Db.App();
        await connection.ExecuteAsync(
            "UPDATE users SET totp_secret=@secret, totp_pending=1, totp_last_step=NULL WHERE id=@id;",
            new { secret = sealedSecret, id = userId });
        var url = $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}?secret={base32}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";
        return new StartResult(base32, url, QrSvg(url));
    }

    public sealed record StartResult(string Secret, string Url, string Qr);

    /// <summary>Activates a pending secret once its code verifies, records the spent
    /// step, and issues a fresh set of recovery codes (returned once, in the clear).</summary>
    public async Task<IReadOnlyList<string>?> Confirm(long userId, string code)
    {
        await using var connection = api.Db.App();
        var sealedSecret = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT totp_secret FROM users WHERE id=@id AND totp_pending=1;", new { id = userId });
        if (sealedSecret is null) return null;

        var secret = sealedValues.DecryptString(sealedSecret, Context(userId));
        if (secret is null) return null;
        if (!VerifyCode(secret, code, out var step)) return null;

        var confirmed = Db.Write(connection, transaction => connection.Execute(
            """
            UPDATE users SET totp_pending=0, totp_last_step=@step
            WHERE id=@id AND totp_pending=1 AND (totp_last_step IS NULL OR totp_last_step < @step);
            """,
            new { id = userId, step }, transaction));
        if (confirmed != 1) return null;
        return await GenerateRecoveryCodes(connection, userId);
    }

    /// <summary>Verifies and spends one step. The update condition makes the code
    /// single-use even for two simultaneous requests.</summary>
    public static async Task<bool> SpendStep(SealedValues sealedValues, SqliteConnection connection, long userId, string sealedSecret, string code)
    {
        var secret = sealedValues.DecryptString(sealedSecret, Context(userId));
        if (secret is null || !VerifyCode(secret, code, out var step)) return false;
        var spent = await connection.ExecuteAsync(
            "UPDATE users SET totp_last_step=@step WHERE id=@id AND (totp_last_step IS NULL OR totp_last_step < @step);",
            new { id = userId, step });
        return spent == 1;
    }

    /// <summary>One recovery code, single use, matched by hash.</summary>
    public static async Task<bool> TryRecoveryCode(SqliteConnection connection, long userId, string code)
    {
        var normalized = code.Trim().ToUpperInvariant().Replace("-", "");
        if (normalized.Length is < 8 or > 20 || !normalized.All(Alphabet.Contains)) return false;
        var hash = SessionService.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var spent = await connection.ExecuteAsync(
            "UPDATE recovery_codes SET used_at=@now WHERE user_id=@userId AND code_hash=@hash AND used_at IS NULL;",
            new { now = SessionService.Now(), userId, hash });
        return spent == 1;
    }

    /// <summary>Replaces the whole set. The plaintext codes are shown once.</summary>
    public async Task<IReadOnlyList<string>> Regenerate(long userId)
    {
        await using var connection = api.Db.App();
        return await GenerateRecoveryCodes(connection, userId);
    }

    private static async Task<IReadOnlyList<string>> GenerateRecoveryCodes(SqliteConnection connection, long userId)
    {
        var codes = new List<string>();
        var rows = new List<(string Hash, long Created)>();
        var now = SessionService.Now();
        for (var i = 0; i < 10; i++)
        {
            var code = RandomCode();
            codes.Add(code);
            rows.Add((SessionService.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)))), now));
        }
        Db.Write(connection, transaction =>
        {
            connection.Execute("DELETE FROM recovery_codes WHERE user_id=@userId;", new { userId }, transaction);
            foreach (var row in rows)
                connection.Execute(
                    "INSERT INTO recovery_codes(user_id, code_hash, created_at) VALUES(@userId, @hash, @created);",
                    new { userId, hash = row.Hash, created = row.Created }, transaction);
            return 0;
        });
        return codes;
    }

    public async Task Disable(long userId)
    {
        await using var connection = api.Db.App();
        await connection.ExecuteAsync(
            "UPDATE users SET totp_secret=NULL, totp_pending=1, totp_last_step=NULL WHERE id=@id;", new { id = userId });
        await connection.ExecuteAsync("DELETE FROM recovery_codes WHERE user_id=@id;", new { id = userId });
    }

    public static string Normalize(string code) => code.Trim().ToUpperInvariant().Replace("-", "");

    private static string RandomCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(10);
        var chars = bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray();
        return new string(chars[..5]) + "-" + new string(chars[5..]);
    }

    private static bool VerifyCode(string base32Secret, string code, out long step)
    {
        step = 0;
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        var totp = new Totp(Base32Encoding.ToBytes(base32Secret), step: 30, mode: OtpHashMode.Sha1, totpSize: 6);
        return totp.VerifyTotp(code, out var used, new VerificationWindow(previous: 1, future: 1)) && (step = used) >= 0;
    }

    /// <summary>A plain SVG of the QR modules: no image library, no CSP change.</summary>
    private static string QrSvg(string content)
    {
        using var data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\">");
        svg.Append($"<rect width=\"{size}\" height=\"{size}\" fill=\"#ffffff\"/>");
        svg.Append("<path fill=\"#000000\" d=\"");
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (matrix[y][x]) svg.Append($"M{x} {y}h1v1h-1z");
        svg.Append("\"/></svg>");
        return svg.ToString();
    }
}
