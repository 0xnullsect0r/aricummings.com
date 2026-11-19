using System.Text.RegularExpressions;
using Ari.Features.Auth;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Ari.Platform.Outbox;
using Dapper;

namespace Ari.Features.Settings;

/// <summary>
/// The settings table. Secrets are sealed with context setting:&lt;key&gt; so one value
/// can't be swapped for another, and the API returns hasValue for them, never the
/// value. Mail settings and credential-like keys are owner-only with step-up.
/// </summary>
public sealed class SettingsService(ApiContext api, SealedValues sealedValues, ILogger<SettingsService> logger)
{
    /// <summary>Keys the settings screen knows about, with defaults for a fresh install.</summary>
    public static readonly (string Key, string Default, bool Secret)[] Known =
    {
        ("smtp.host", "", false),
        ("smtp.port", "587", false),
        ("smtp.user", "", false),
        ("smtp.password", "", true),
        ("smtp.from_address", "", false),
        ("smtp.from_name", "aricummings.com", false),
        ("smtp.starttls", "true", false),
        ("github.token", "", true),
    };

    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9._-]{0,99}$", RegexOptions.Compiled);

    public static bool IsSecret(string key) =>
        Known.FirstOrDefault(k => k.Key == key) is { Secret: true }
        || key.EndsWith(".password", StringComparison.Ordinal)
        || key.EndsWith(".secret", StringComparison.Ordinal)
        || key.EndsWith(".token", StringComparison.Ordinal)
        || key.EndsWith(".key", StringComparison.Ordinal);

    public static bool IsCredentialLike(string key) =>
        key.StartsWith("smtp.", StringComparison.Ordinal) || IsSecret(key);

    public sealed record SettingView(string Key, bool Secret, string? Value, bool HasValue, long? UpdatedAt, string? UpdatedBy);

    public async Task<IResult> List(HttpContext http)
    {
        await using var connection = api.Db.App();
        var rows = (await connection.QueryAsync<(string Key, string Value, long Encrypted, long UpdatedAt, long? UpdatedBy)>(
            "SELECT key AS Key, value AS Value, encrypted AS Encrypted, updated_at AS UpdatedAt, updated_by AS UpdatedBy FROM settings ORDER BY key;"))
            .ToDictionary(r => r.Key, StringComparer.Ordinal);
        var users = (await connection.QueryAsync<(long Id, string Email)>("SELECT id AS Id, email AS Email FROM users;"))
            .ToDictionary(u => u.Id, u => u.Email);

        SettingView View(string key, bool secret, string? storedValue, long? updatedAt, long? updatedBy) => new(
            key, secret,
            Value: secret ? null : storedValue,
            HasValue: storedValue is { Length: > 0 },
            UpdatedAt: updatedAt,
            UpdatedBy: updatedBy is long by && users.TryGetValue(by, out var email) ? email : null);

        var items = new List<SettingView>();
        foreach (var known in Known)
        {
            rows.TryGetValue(known.Key, out var row);
            var present = !row.Equals(default) && row.Value.Length > 0;
            items.Add(View(known.Key, IsSecret(known.Key),
                present ? row.Value : (IsSecret(known.Key) ? "" : known.Default),
                present ? row.UpdatedAt : null,
                present ? row.UpdatedBy : null));
        }
        foreach (var pair in rows.Where(pair => Known.All(k => k.Key != pair.Key)))
            items.Add(View(pair.Key, pair.Value.Encrypted == 1, pair.Value.Value, pair.Value.UpdatedAt, pair.Value.UpdatedBy));
        return Results.Json(new { items });
    }

    public async Task<IResult> Put(HttpContext http, string key)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        if (!KeyPattern.IsMatch(key))
            throw new ApiException(400, "Setting keys are lowercase letters, digits, dots, dashes.", "invalid_key");
        // Mail settings and credentials can only be touched with a fresh identity proof.
        if (IsCredentialLike(key) && !staff.StepUpFresh(SessionService.Now()))
            throw new ApiException(403, "Confirm your identity again to continue.", "step_up_required");

        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Optional("value", out var value, 10_000);
        validator.Done();
        if (value.Any(char.IsControl))
            throw new ApiException(400, "Setting values cannot hold control characters.", "invalid_fields");

        var secret = IsSecret(key);
        var stored = secret && value.Length > 0
            ? sealedValues.EncryptString(value, $"setting:{key}")
            : value;
        await using var connection = api.Db.App();
        if (value.Length == 0)
        {
            await connection.ExecuteAsync("DELETE FROM settings WHERE key=@key;", new { key });
        }
        else
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO settings(key, value, encrypted, updated_at, updated_by)
                VALUES(@key, @value, @encrypted, @now, @actor)
                ON CONFLICT(key) DO UPDATE SET value=@value, encrypted=@encrypted, updated_at=@now, updated_by=@actor;
                """,
                new { key, value = stored, encrypted = secret ? 1 : 0, now = SessionService.Now(), actor = staff.UserId });
        }
        connection.Audit(staff.UserId,
            key.StartsWith("smtp.", StringComparison.Ordinal) ? "settings.smtp_updated" : "settings.updated",
            key, secret ? "secret changed" : "", Requests.ClientIp(http));
        logger.LogInformation("Setting {Key} updated by user {User}", key, staff.UserId);
        return Results.NoContent();
    }
}

/// <summary>Reads the SMTP settings each time the mail job asks; the job refuses to
/// run without a working configuration, so partial rows mean "not configured".</summary>
public sealed class SettingsSmtpConfig(ApiContext api, SealedValues sealedValues) : ISmtpConfig
{
    public (string Host, int Port, string User, string Password, string FromAddress, string FromName, bool StartTls)? Read()
    {
        using var connection = api.Db.App();
        var rows = connection.Query<(string Key, string Value, long Encrypted)>(
            "SELECT key AS Key, value AS Value, encrypted AS Encrypted FROM settings WHERE key LIKE 'smtp.%';")
            .ToDictionary(r => r.Key, StringComparer.Ordinal);
        string Value(string key)
        {
            if (!rows.TryGetValue(key, out var row) || row.Value.Length == 0) return "";
            if (row.Encrypted == 1)
            {
                try { return sealedValues.DecryptString(row.Value, $"setting:{key}") ?? ""; }
                catch (Exception) { return ""; } // unreadable secret = not configured
            }
            return row.Value;
        }

        var host = Value("smtp.host");
        var from = Value("smtp.from_address");
        if (host.Length == 0 || from.Length == 0) return null;
        if (!int.TryParse(Value("smtp.port"), out var port) || port is < 1 or > 65535) port = 587;
        return (host, port, Value("smtp.user"), Value("smtp.password"), from, Value("smtp.from_name"),
            !bool.TryParse(Value("smtp.starttls"), out var startTls) || startTls);
    }
}

public sealed class SettingsFeature : IFeature
{
    public int Order => 70;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<SettingsService>();
        // Registered after the platform's NullSmtpConfig, so this one wins.
        services.AddSingleton<ISmtpConfig, SettingsSmtpConfig>();
    }

    public void Map(WebApplication app)
    {
        app.MapGet("/api/admin/settings", async (HttpContext http, SettingsService service) => await service.List(http))
            .AddEndpointFilter(Guards.RequireOwner);
        app.MapPut("/api/admin/settings/{key}", async (HttpContext http, string key, SettingsService service) => await service.Put(http, key))
            .AddEndpointFilter(Guards.RequireOwner);
    }
}
