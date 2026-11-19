using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using Ari.Platform;
using Ari.Platform.Crypto;
using Ari.Platform.Database;
using Ari.Platform.Tokens;
using Dapper;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ari.Features.Auth;

/// <summary>Cookie names and attributes, written by hand so they are exact.
/// ARI_INSECURE_COOKIES switches to non-Secure names for plain-http dev;
/// production refuses to boot with it set (Env).</summary>
public sealed record AuthOptions(bool InsecureCookies)
{
    public string AccessCookie => InsecureCookies ? "ari_at" : "__Host-ari_at";
    public string RefreshCookie => InsecureCookies ? "ari_rt" : "__Secure-ari_rt";
    public string RefreshCookiePath => "/api/auth";

    public static AuthOptions Load() =>
        new(Environment.GetEnvironmentVariable("ARI_INSECURE_COOKIES") == "1");
}

/// <summary>The signed-in person plus their session, loaded fresh from the database on
/// every request so disabling a user or revoking a session takes effect immediately.</summary>
public sealed record StaffContext(
    long UserId,
    string Email,
    string Name,
    string Role,
    IReadOnlyList<string> Permissions,
    string SessionId,
    long StepUpAt)
{
    public bool IsOwner => Role == "owner";
    public bool HasPermission(string permission) => IsOwner || Permissions.Contains(permission);
    public bool StepUpFresh(long now) => StepUpAt > 0 && now - StepUpAt <= 10 * 60 * 1000;
}

public sealed record SessionRecord(
    string Id,
    long UserId,
    long CreatedAt,
    long LastSeenAt,
    long ExpiresAt,
    long? RevokedAt);

/// <summary>
/// Issues the staff JWTs in the exact pinned format (HS256, iss "ari", kid header,
/// key derived with info "site:jwt") including the custom claims the platform issuer
/// currently drops. Reading stays with TokenIssuer, which verifies this shape fine;
/// only Issue is local. Fold back into Platform once Platform.Issue carries claims.
/// </summary>
public sealed class StaffTokens(Env env)
{
    private readonly KeyRing keys = env.Keys;

    private const string Issuer = "ari";
    private const string Info = "site:jwt";

    private static readonly JsonWebTokenHandler Handler = new();

    public string Issue(TokenAudience audience, TimeSpan lifetime, IReadOnlyList<(string Type, string Value)> claims)
    {
        var entry = keys.Current;
        var now = DateTimeOffset.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience switch
            {
                TokenAudience.Staff => "staff",
                TokenAudience.Staff2Fa => "staff-2fa",
                TokenAudience.Preview => "preview",
                _ => "staff-marker",
            },
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(keys.Derive(entry, Info)) { KeyId = entry.Kid },
                SecurityAlgorithms.HmacSha256),
            Claims = claims.ToDictionary(c => c.Type, c => (object)c.Value),
        };
        return Handler.CreateToken(descriptor);
    }
}

/// <summary>Sessions, refresh rotation with grace, retired-hash theft detection, cookies.</summary>
public sealed class SessionService(ApiContext api, StaffTokens tokens, ILogger<SessionService> logger)
{
    public const int AccessMinutes = 10;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan IdleAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan RotationGrace = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions RetiredJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string IssueAccessToken(long userId, string sessionId) =>
        tokens.Issue(TokenAudience.Staff, TimeSpan.FromMinutes(AccessMinutes),
            new[] { ("sub", userId.ToString()), ("sid", sessionId) });

    public string Pending2FaToken(long userId) =>
        tokens.Issue(TokenAudience.Staff2Fa, TimeSpan.FromMinutes(5),
            new[] { ("sub", userId.ToString()) });

    public async Task<(string SessionId, string RefreshToken)> Create(long userId)
    {
        // Session ids travel in URL paths (account screens), and the API refuses any
        // uppercase path segment, so ids are lowercase throughout. Entropy lives in
        // the refresh secret, not the id.
        var sessionId = RandomToken.New().ToLowerInvariant();
        var secret = RandomToken.New();
        var now = Now();
        await using var connection = api.Db.App();
        Db.Write(connection, transaction =>
        {
            connection.Execute("""
                INSERT INTO sessions(id, user_id, created_at, last_seen_at, expires_at, refresh_hash, refresh_rotated_at, retired_hashes)
                VALUES(@id, @userId, @now, @now, @expires, @hash, @now, '[]');
                """,
                new { id = sessionId, userId, now, expires = now + (long)MaxAge.TotalMilliseconds, hash = Hex(RandomToken.Hash(secret)) },
                transaction);
            return 0;
        });
        logger.LogInformation("Session {Session} created for user {User}", sessionId[..8], userId);
        return (sessionId, sessionId + "." + secret);
    }

    public async Task<SessionRecord?> Load(string sessionId)
    {
        await using var connection = api.Db.App();
        return await connection.QuerySingleOrDefaultAsync<SessionRecord>(
            "SELECT id, user_id AS UserId, created_at AS CreatedAt, last_seen_at AS LastSeenAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt FROM sessions WHERE id=@id;",
            new { id = sessionId });
    }

    /// <summary>True when the session may serve requests: not revoked, not past 30 days,
    /// and seen within the 7-day idle window.</summary>
    public static bool Usable(SessionRecord? session, long now) =>
        session is not null
        && session.RevokedAt is null
        && session.ExpiresAt > now
        && session.LastSeenAt > now - (long)IdleAge.TotalMilliseconds;

    public async Task Touch(string sessionId, long lastSeen)
    {
        await using var connection = api.Db.App();
        await connection.ExecuteAsync("UPDATE sessions SET last_seen_at=@now WHERE id=@id;", new { now = lastSeen, id = sessionId });
    }

    public enum RefreshOutcome { Rotated, Theft, Invalid }

    public sealed record RetiredEntry
    {
        public string H { get; set; } = "";
        public long T { get; set; }
    }

    /// <summary>
    /// Rotation with a 30-second grace so two tabs can refresh at once: the loser
    /// presents the token the winner just replaced and is simply served again. A
    /// retired hash past its grace means someone holds a stolen copy: the session is
    /// revoked outright. A value never issued proves nothing and is only invalid.
    /// All of it happens inside one write transaction.
    /// </summary>
    public async Task<(RefreshOutcome Outcome, string? NewRefreshToken)> Rotate(string sessionId, string presentedSecret)
    {
        var presentedHash = Hex(RandomToken.Hash(presentedSecret));
        var now = Now();
        await using var connection = api.Db.App();
        return await Db.Write<(RefreshOutcome Outcome, string? NewRefreshToken)>(connection, async transaction =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<(string RefreshHash, long ExpiresAt, long? RevokedAt, long LastSeen, string Retired)>(
                "SELECT refresh_hash AS RefreshHash, expires_at AS ExpiresAt, revoked_at AS RevokedAt, last_seen_at AS LastSeen, retired_hashes AS Retired FROM sessions WHERE id=@id;",
                new { id = sessionId }, transaction);
            if (row == default || row.RefreshHash is null || row.RevokedAt is not null || row.ExpiresAt <= now
                || row.LastSeen <= now - (long)IdleAge.TotalMilliseconds)
                return (RefreshOutcome.Invalid, null);

            if (presentedHash != row.RefreshHash)
            {
                List<RetiredEntry> retired = ParseRetired(row.Retired);
                var entry = retired.FirstOrDefault(r => r.H == presentedHash);
                if (entry is null) return (RefreshOutcome.Invalid, null);
                if (now - entry.T > (long)RotationGrace.TotalMilliseconds)
                {
                    await connection.ExecuteAsync("UPDATE sessions SET revoked_at=@now WHERE id=@id AND revoked_at IS NULL;",
                        new { now, id = sessionId }, transaction);
                    return (RefreshOutcome.Theft, null);
                }
                // Inside the grace window: serve this tab again by rotating once more.
            }

            var newSecret = RandomToken.New();
            var updated = ParseRetired(row.Retired);
            updated.Add(new RetiredEntry { H = row.RefreshHash, T = now });
            await connection.ExecuteAsync("""
                UPDATE sessions
                SET refresh_hash=@hash, refresh_rotated_at=@now, retired_hashes=@retired, last_seen_at=@now
                WHERE id=@id;
                """,
                new { hash = Hex(RandomToken.Hash(newSecret)), now, retired = JsonSerializer.Serialize(updated, RetiredJson), id = sessionId },
                transaction);
            return (RefreshOutcome.Rotated, sessionId + "." + newSecret);
        });
    }

    public async Task Revoke(string sessionId)
    {
        await using var connection = api.Db.App();
        await connection.ExecuteAsync("UPDATE sessions SET revoked_at=@now WHERE id=@id AND revoked_at IS NULL;",
            new { now = Now(), id = sessionId });
    }

    public async Task RevokeAllForUser(long userId, string? exceptSessionId)
    {
        await using var connection = api.Db.App();
        await connection.ExecuteAsync(
            "UPDATE sessions SET revoked_at=@now WHERE user_id=@userId AND revoked_at IS NULL AND (@except IS NULL OR id != @except);",
            new { now = Now(), userId, except = exceptSessionId });
    }

    private static List<RetiredEntry> ParseRetired(string? json)
    {
        try { return JsonSerializer.Deserialize<List<RetiredEntry>>(json ?? "[]", RetiredJson) ?? new List<RetiredEntry>(); }
        catch (JsonException) { return new List<RetiredEntry>(); }
    }

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Resolves the staff context from the access-token cookie on every request.</summary>
public sealed class StaffResolver(ApiContext api, AuthOptions options, SessionService sessions)
{
    public async Task<StaffContext?> Resolve(HttpContext http)
    {
        var token = http.Request.Cookies[options.AccessCookie];
        if (string.IsNullOrEmpty(token)) return null;
        var principal = await api.Tokens.Read(token, TokenAudience.Staff);
        if (principal is null) return null;
        var userId = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var sessionId = principal.FindFirst("sid")?.Value ?? principal.FindFirst(ClaimTypes.Sid)?.Value;
        if (!long.TryParse(userId, out var uid) || string.IsNullOrEmpty(sessionId)) return null;

        var now = SessionService.Now();
        var session = await sessions.Load(sessionId);
        if (!SessionService.Usable(session, now) || session!.UserId != uid) return null;

        var user = await LoadUser(api.Db, uid);
        if (user is null || user.Value.Active != 1) return null;

        // Touch at most once a minute per session: enough for the 7-day idle rule.
        if (now - session.LastSeenAt > 60_000) await sessions.Touch(sessionId, now);

        return new StaffContext(uid, user.Value.Email, user.Value.Name, user.Value.Role,
            ParsePermissions(user.Value.Permissions), sessionId, user.Value.StepUpAt);
    }

    public static async Task<(long Id, string Email, string Name, string Role, string Permissions, long Active, long StepUpAt)?> LoadUser(Db db, long id)
    {
        await using var connection = db.App();
        return await connection.QuerySingleOrDefaultAsync<(long, string, string, string, string, long, long)?>(
            "SELECT id, email, name, role, permissions, active, step_up_at FROM users WHERE id=@id;", new { id });
    }

    public static IReadOnlyList<string> ParsePermissions(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}

/// <summary>Hand-written Set-Cookie values so every attribute is exact.</summary>
public static class CookieJar
{
    public static void WriteAccess(HttpContext http, AuthOptions options, string token) =>
        http.Response.Headers.Append("Set-Cookie",
            $"{options.AccessCookie}={token}; Path=/; Max-Age={SessionService.AccessMinutes * 60}; HttpOnly; Secure; SameSite=Strict");

    public static void WriteRefresh(HttpContext http, AuthOptions options, string token) =>
        http.Response.Headers.Append("Set-Cookie",
            $"{options.RefreshCookie}={token}; Path={options.RefreshCookiePath}; Max-Age={(long)SessionService.IdleAge.TotalSeconds}; HttpOnly; Secure; SameSite=Strict");

    /// <summary>A refused session must leave no cookies behind. The refresh cookie is
    /// only wiped when refresh itself failed, so the SPA's one retry still works.</summary>
    public static void WipeAccess(HttpContext http, AuthOptions options) =>
        http.Response.Headers.Append("Set-Cookie",
            $"{options.AccessCookie}=; Path=/; Max-Age=0; HttpOnly; Secure; SameSite=Strict");

    public static void WipeRefresh(HttpContext http, AuthOptions options) =>
        http.Response.Headers.Append("Set-Cookie",
            $"{options.RefreshCookie}=; Path={options.RefreshCookiePath}; Max-Age=0; HttpOnly; Secure; SameSite=Strict");

    public static void WipeAll(HttpContext http, AuthOptions options)
    {
        WipeAccess(http, options);
        WipeRefresh(http, options);
    }
}

/// <summary>Client address per the edge's rewritten X-Forwarded-For, which is either the
/// proxy-confirmed client or the socket peer. Network keys count an IPv6 /64.</summary>
public static class Requests
{
    public static string ClientIp(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwarded)) return forwarded.Split(',')[0].Trim();
        return http.Connection.RemoteIpAddress?.ToString() ?? "?";
    }

    public static string NetworkKey(HttpContext http)
    {
        var raw = ClientIp(http);
        if (!IPAddress.TryParse(raw, out var address)) return raw;
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return raw;
        var prefix = new byte[8];
        Array.Copy(address.GetAddressBytes(), prefix, 8);
        return new IPAddress(prefix).ToString() + "/64";
    }
}
