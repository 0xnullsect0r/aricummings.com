using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Dapper;

namespace Ari.Features.Auth;

/// <summary>Account routes: who am I, own password, own TOTP, own sessions,
/// invite acceptance and password resets from emailed links.</summary>
public sealed class AccountService(
    ApiContext api,
    RateGate limits,
    PasswordHasher hasher,
    SessionService sessions,
    TotpService totp,
    LinkService links,
    AuthOptions options,
    ILogger<AccountService> logger)
{
    public async Task<IResult> Me(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var row = await connection.QuerySingleAsync<(string? PasswordHash, string? TotpSecret, long TotpPending, long Passkeys)>(
            """
            SELECT password_hash AS PasswordHash, totp_secret AS TotpSecret, totp_pending AS TotpPending,
                   (SELECT COUNT(*) FROM passkey_credentials WHERE user_id=@id) AS Passkeys
            FROM users WHERE id=@id;
            """,
            new { id = staff.UserId });
        return Results.Json(new
        {
            id = staff.UserId,
            email = staff.Email,
            name = staff.Name,
            role = staff.Role,
            permissions = staff.IsOwner ? Permissions.Keys : staff.Permissions,
            hasPassword = row.PasswordHash is not null,
            totp = new { enabled = row.TotpSecret is not null && row.TotpPending == 0, pending = row.TotpPending == 1 && row.TotpSecret is not null },
            passkeys = row.Passkeys,
            stepUpFresh = staff.StepUpFresh(SessionService.Now()),
        });
    }

    public IResult PasswordRulesGet(HttpContext http) =>
        Results.Json(new { min = PasswordRules.MinLength, max = PasswordRules.MaxLength });

    public async Task<IResult> PasswordChange(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Optional("current", out var current, 200);
        validator.Require("next", out var next, 1, 200);
        validator.Done();

        var problems = PasswordRules.Problems(next, staff.Email);
        if (problems.Count > 0)
            throw new ApiException(400, string.Join("; ", problems.Select(p => $"Password {p}.")), "weak_password");

        var now = SessionService.Now();
        await using var connection = api.Db.App();
        var stored = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT password_hash FROM users WHERE id=@id;", new { id = staff.UserId });
        if (stored is not null)
        {
            if (current.Length == 0)
                throw new ApiException(400, "Enter your current password.", "current_required");
            if (!hasher.Verify(current, stored))
            {
                connection.Audit(staff.UserId, "auth.password_change_failed", staff.Email, "wrong current password", Requests.ClientIp(http));
                throw new ApiException(401, "Email or password is not correct.", "invalid_credentials");
            }
        }

        var updated = Db.Write(connection, transaction => connection.Execute(
            "UPDATE users SET password_hash=@hash, step_up_at=@now WHERE id=@id;",
            new { hash = hasher.Hash(next), now, id = staff.UserId }, transaction));
        if (updated != 1) throw new ApiException(404, "No such account.", "not_found");

        // A new password means the old ones are suspect: every other session goes.
        await sessions.RevokeAllForUser(staff.UserId, staff.SessionId);
        connection.Audit(staff.UserId, "auth.password_changed", staff.Email, "", Requests.ClientIp(http));
        logger.LogInformation("User {User} changed their password", staff.UserId);
        return Results.NoContent();
    }

    public async Task<IResult> TotpStart(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var started = await totp.Start(staff.UserId, staff.Email);
        await using var connection = api.Db.App();
        connection.Audit(staff.UserId, "auth.totp_started", staff.Email, "", Requests.ClientIp(http));
        return Results.Json(new { secret = started.Secret, url = started.Url, qr = started.Qr });
    }

    public async Task<IResult> TotpConfirm(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("code", out var code, 6, 6);
        validator.Done();

        var codes = await totp.Confirm(staff.UserId, code);
        if (codes is null) throw new ApiException(400, "That code did not verify. Check the app and try again.", "invalid_code");
        await using var connection = api.Db.App();
        connection.Audit(staff.UserId, "auth.totp_enabled", staff.Email, "", Requests.ClientIp(http));
        return Results.Json(new { recoveryCodes = codes });
    }

    public async Task<IResult> TotpRecovery(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var codes = await totp.Regenerate(staff.UserId);
        await using var connection = api.Db.App();
        connection.Audit(staff.UserId, "auth.recovery_regenerated", staff.Email, "", Requests.ClientIp(http));
        return Results.Json(new { recoveryCodes = codes });
    }

    public async Task<IResult> SessionsList(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var rows = await connection.QueryAsync<(string Id, long CreatedAt, long LastSeenAt, long ExpiresAt, long? RevokedAt)>(
            """
            SELECT id AS Id, created_at AS CreatedAt, last_seen_at AS LastSeenAt, expires_at AS ExpiresAt, revoked_at AS RevokedAt
            FROM sessions WHERE user_id=@userId ORDER BY last_seen_at DESC LIMIT 100;
            """,
            new { userId = staff.UserId });
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                createdAt = r.CreatedAt,
                lastSeenAt = r.LastSeenAt,
                expiresAt = r.ExpiresAt,
                revoked = r.RevokedAt is not null,
                current = r.Id == staff.SessionId,
            }),
        });
    }

    public async Task<IResult> SessionRevoke(HttpContext http, string id)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var revoked = await connection.ExecuteAsync(
            "UPDATE sessions SET revoked_at=@now WHERE id=@id AND user_id=@userId AND revoked_at IS NULL;",
            new { now = SessionService.Now(), id, userId = staff.UserId });
        if (revoked != 1) throw new ApiException(404, "No such session.", "not_found");
        connection.Audit(staff.UserId, "auth.session_revoked", id, "", Requests.ClientIp(http));
        if (id == staff.SessionId) CookieJar.WipeAll(http, options);
        return Results.NoContent();
    }

    // ---- Link endpoints (no session): invite acceptance and password reset ----

    public async Task<IResult> InviteAccept(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("token", out var token, 20, 200);
        validator.Require("name", out var name, 1, 100);
        validator.Require("password", out var password, 1, 200);
        validator.Done();

        var link = await LinkService.Peek(api.Db, token, "invite")
            ?? throw new ApiException(400, "That invite is used or expired. Ask for a new one.", "link_invalid");
        var problems = PasswordRules.Problems(password, link.Email);
        if (problems.Count > 0) throw WeakPassword(problems);

        var userId = await LinkService.Consume(api.Db, token, "invite")
            ?? throw new ApiException(400, "That invite is used or expired. Ask for a new one.", "link_invalid");

        await using var connection = api.Db.App();
        await connection.ExecuteAsync(
            "UPDATE users SET name=@name, password_hash=@hash, step_up_at=@now WHERE id=@id;",
            new { name, hash = hasher.Hash(password), now = SessionService.Now(), id = userId });
        connection.Audit(userId, "users.joined", link.Email, "", Requests.ClientIp(http));
        logger.LogInformation("User {User} accepted their invite", userId);
        return Results.NoContent();
    }

    /// <summary>Always answers 204: the form never reveals whether an address exists.
    /// A reset link is only mailed to a real, active account.</summary>
    public async Task<IResult> ResetRequest(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("email", out var email, 3, 320);
        validator.Done();
        email = email.Trim().ToLowerInvariant();

        var network = Requests.NetworkKey(http);
        if (limits.TryConsume(limits.Reset, "reset:" + network)
            && limits.TryConsume(limits.Reset, "reset:" + network + "|" + email))
        {
            await using var connection = api.Db.App();
            var user = await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT id FROM users WHERE email=@email AND active=1;", new { email });
            if (user is not null)
            {
                var link = await links.CreateReset(user.Value, email, api.Env.AdminOrigin);
                await api.Queue.Enqueue("mail", new Dictionary<string, string>
                {
                    ["to"] = email,
                    ["subject"] = "aricummings.com password reset",
                    ["text"] = "A password reset was requested for your account.\n\nOpen this link within one hour to choose a new password:\n\n" + link.Url + "\n\nIf you did not ask for this, ignore the message; your password stays unchanged.\n",
                });
                connection.Audit(user, "auth.reset_requested", email, "", Requests.ClientIp(http));
            }
        }
        return Results.NoContent();
    }

    public async Task<IResult> ResetConfirm(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("token", out var token, 20, 200);
        validator.Require("password", out var password, 1, 200);
        validator.Done();

        var link = await LinkService.Peek(api.Db, token, "reset")
            ?? throw new ApiException(400, "That reset link is used or expired. Request a new one.", "link_invalid");
        var problems = PasswordRules.Problems(password, link.Email);
        if (problems.Count > 0) throw WeakPassword(problems);

        var userId = await LinkService.Consume(api.Db, token, "reset")
            ?? throw new ApiException(400, "That reset link is used or expired. Request a new one.", "link_invalid");

        await using var connection = api.Db.App();
        await connection.ExecuteAsync(
            "UPDATE users SET password_hash=@hash, failed_signins=0, locked_until=NULL WHERE id=@id;",
            new { hash = hasher.Hash(password), id = userId });
        await sessions.RevokeAllForUser(userId, exceptSessionId: null);
        connection.Audit(userId, "auth.password_reset", link.Email, "", Requests.ClientIp(http));
        logger.LogInformation("User {User} reset their password", userId);
        return Results.NoContent();
    }

    private static ApiException WeakPassword(List<string> problems) =>
        new(400, string.Join("; ", problems.Select(p => $"Password {p}.")), "weak_password");
}
