using Ari.Platform;
using Microsoft.Data.Sqlite;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Ari.Platform.Tokens;
using Dapper;

namespace Ari.Features.Auth;

/// <summary>
/// Password sign-in with per-network and per-network+email token buckets, lockout
/// after 5 failures doubling to at most 15 minutes, and a dummy hash so an unknown
/// email costs exactly what a wrong password costs. One generic failure message.
/// </summary>
public sealed class SignInService(
    ApiContext api,
    SealedValues sealedValues,
    RateGate limits,
    PasswordHasher hasher,
    SessionService sessions,
    AuthOptions options,
    ILogger<SignInService> logger)
{
    private readonly string _dummyHash = hasher.HashUncounted("not-a-real-account-" + Guid.NewGuid().ToString("N"));

    public async Task<IResult> SignIn(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("email", out var email, 3, 320);
        validator.Require("password", out var password, 1, 200);
        validator.Done();
        email = email.Trim().ToLowerInvariant();

        var network = Requests.NetworkKey(http);
        if (!limits.TryConsume(limits.SignInNetwork, "net:" + network))
            return TooManyRequests(http, limits.SignInNetwork.RetryAfter("net:" + network));
        if (!limits.TryConsume(limits.SignInEmail, "email:" + network + "|" + email))
            return TooManyRequests(http, limits.SignInEmail.RetryAfter("email:" + network + "|" + email));

        var now = SessionService.Now();
        await using var connection = api.Db.App();
        var user = await connection.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {UserColumns} FROM users WHERE email=@email;", new { email });

        // A missing account or an unusable stored value still costs a full hash.
        var storedHash = user?.PasswordHash;
        if (string.IsNullOrEmpty(storedHash))
        {
            hasher.Verify(password, _dummyHash);
            connection.Audit(null, "auth.login_failed", email, "invalid credentials", Requests.ClientIp(http));
            logger.LogInformation("Sign-in refused for unknown email {Email}", email);
            return Invalid();
        }
        if (user!.Active != 1)
        {
            hasher.Verify(password, _dummyHash);
            connection.Audit(null, "auth.login_failed", email, "account disabled", Requests.ClientIp(http));
            return Invalid();
        }
        if (user.LockedUntil is long locked && locked > now)
        {
            // Same answer as an unknown account: a distinct message here would be an
            // enumeration oracle (this account exists AND is locked). The audit row
            // keeps the detail server-side.
            hasher.Verify(password, _dummyHash);
            connection.Audit(user.Id, "auth.locked_out", email, "locked", Requests.ClientIp(http));
            return Invalid();
        }

        if (!hasher.Verify(password, storedHash))
        {
            var (failed, lockFor) = CountFailure(connection, user.Id);
            connection.Audit(user.Id, "auth.login_failed", email, "invalid credentials", Requests.ClientIp(http));
            logger.LogInformation("Sign-in failed for user {User} (attempt {Attempt})", user.Id, failed);
            if (lockFor is not null)
            {
                connection.Audit(user.Id, "auth.locked_out", email, "locked after repeated failures", Requests.ClientIp(http));
                return Results.Json(new { error = "Too many failed attempts. Try again later.", code = "locked_out" }, statusCode: 401);
            }
            return Invalid();
        }

        await connection.ExecuteAsync(
            "UPDATE users SET failed_signins=0, locked_until=NULL, step_up_at=@now WHERE id=@id;",
            new { now, id = user.Id });

        // 2FA is only demanded once the app proved it works: a pending secret is inert.
        var totpActive = user.TotpSecret is not null && user.TotpPending == 0;
        if (totpActive)
        {
            logger.LogInformation("Password accepted for user {User}; 2FA pending", user.Id);
            return Results.Json(new { next = "2fa", token = sessions.Pending2FaToken(user.Id) });
        }

        return await CompleteSignIn(http, connection, user.Id, user.Email, "password");
    }

    /// <summary>Second factor: a TOTP code or a recovery code, carried by a 5-minute
    /// staff-2fa token. Failures count toward lockout exactly like password failures.</summary>
    public async Task<IResult> SecondFactor(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("token", out var token);
        validator.Require("code", out var code, 6, 20);
        validator.Done();

        var network = Requests.NetworkKey(http);
        if (!limits.TryConsume(limits.SignInNetwork, "net:" + network))
            return TooManyRequests(http, limits.SignInNetwork.RetryAfter("net:" + network));

        var principal = await api.Tokens.Read(token, TokenAudience.Staff2Fa);
        var uid = principal?.FindFirst("sub")?.Value ?? principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (principal is null || !long.TryParse(uid, out var userId))
            return Invalid(code: "invalid_code");

        var now = SessionService.Now();
        await using var connection = api.Db.App();
        var user = await connection.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {UserColumns} FROM users WHERE id=@id;", new { id = userId });
        if (user is null || user.Active != 1) return Invalid(code: "invalid_code");
        if (user.LockedUntil is long locked && locked > now)
            return Results.Json(new { error = "Too many failed attempts. Try again later.", code = "locked_out" }, statusCode: 401);

        var verified = false;
        if (user.TotpSecret is not null && user.TotpPending == 0 && code.Length == 6 && code.All(char.IsDigit))
            verified = await TotpService.SpendStep(sealedValues, connection, user.Id, user.TotpSecret, code);
        if (!verified)
            verified = await TotpService.TryRecoveryCode(connection, user.Id, code);

        if (!verified)
        {
            var (failed, lockFor) = CountFailure(connection, user.Id);
            connection.Audit(user.Id, "auth.login_failed", user.Email, "invalid second factor", Requests.ClientIp(http));
            if (lockFor is not null)
            {
                connection.Audit(user.Id, "auth.locked_out", user.Email, "locked after repeated failures", Requests.ClientIp(http));
                return Results.Json(new { error = "Too many failed attempts. Try again later.", code = "locked_out" }, statusCode: 401);
            }
            return Invalid(code: "invalid_code");
        }

        await connection.ExecuteAsync(
            "UPDATE users SET failed_signins=0, locked_until=NULL, step_up_at=@now WHERE id=@id;",
            new { now, id = user.Id });
        return await CompleteSignIn(http, connection, user.Id, user.Email, "2fa");
    }

    public async Task<IResult> CompleteSignIn(HttpContext http, SqliteConnection connection, long userId, string email, string via)
    {
        var (sessionId, refreshToken) = await sessions.Create(userId);
        CookieJar.WriteAccess(http, options, sessions.IssueAccessToken(userId, sessionId));
        CookieJar.WriteRefresh(http, options, refreshToken);
        await connection.ExecuteAsync("UPDATE users SET step_up_at=@now WHERE id=@id;",
            new { now = SessionService.Now(), id = userId });
        connection.Audit(userId, "auth.login", email, via, Requests.ClientIp(http));
        logger.LogInformation("User {User} signed in via {Via}", userId, via);
        return Results.NoContent();
    }

    private (int Failed, TimeSpan? Lock) CountFailure(SqliteConnection connection, long userId)
    {
        var now = SessionService.Now();
        return Db.Write(connection, (SqliteTransaction transaction) =>
        {
            var failed = connection.ExecuteScalar<int>(
                "SELECT failed_signins FROM users WHERE id=@id;", new { id = userId }, transaction) + 1;
            long? locked = null;
            if (failed >= 5)
            {
                var seconds = Math.Min(900, 30L << Math.Min(10, failed - 5));
                locked = now + seconds * 1000;
            }
            connection.Execute(
                "UPDATE users SET failed_signins=@failed, locked_until=@locked WHERE id=@id;",
                new { failed, locked, id = userId }, transaction);
            return (failed, locked is null ? (TimeSpan?)null : TimeSpan.FromSeconds((locked.Value - now) / 1000.0));
        });
    }

    public async Task<IResult> SignOut(HttpContext http)
    {
        var staff = await http.RequestServices.GetRequiredService<StaffResolver>().Resolve(http);
        if (staff is not null)
        {
            await sessions.Revoke(staff.SessionId);
            await using var connection = api.Db.App();
            connection.Audit(staff.UserId, "auth.signout", staff.Email, "", Requests.ClientIp(http));
        }
        CookieJar.WipeAll(http, options);
        return Results.NoContent();
    }

    /// <summary>Step-up: re-confirm the password to open the 10-minute window
    /// RequireStepUp reads (owner routes, mail settings, credential changes).</summary>
    public async Task<IResult> StepUp(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("password", out var password, 1, 200);
        validator.Done();

        var network = Requests.NetworkKey(http);
        if (!limits.TryConsume(limits.SignInNetwork, "net:" + network))
            return TooManyRequests(http, limits.SignInNetwork.RetryAfter("net:" + network));

        await using var connection = api.Db.App();
        var stored = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT password_hash FROM users WHERE id=@id;", new { id = staff.UserId });
        // A passkey-only account has no password to confirm here; the fresh sign-in
        // (user verification) already set the window.
        var ok = stored is not null && hasher.Verify(password, stored);
        if (!ok)
        {
            connection.Audit(staff.UserId, "auth.step_up_failed", staff.Email, "", Requests.ClientIp(http));
            return Results.Json(new { error = "Email or password is not correct.", code = "invalid_credentials" }, statusCode: 401);
        }
        await connection.ExecuteAsync("UPDATE users SET step_up_at=@now WHERE id=@id;",
            new { now = SessionService.Now(), id = staff.UserId });
        connection.Audit(staff.UserId, "auth.step_up", staff.Email, "", Requests.ClientIp(http));
        return Results.NoContent();
    }

    public async Task<IResult> Refresh(HttpContext http)
    {
        var network = Requests.NetworkKey(http);
        if (!limits.TryConsume(limits.Refresh, "net:" + network))
            return TooManyRequests(http, limits.Refresh.RetryAfter("net:" + network));

        var token = http.Request.Cookies[options.RefreshCookie];
        if (string.IsNullOrEmpty(token))
        {
            CookieJar.WipeAll(http, options);
            return Results.Json(new { error = "Sign in required.", code = "not_signed_in" }, statusCode: 401);
        }
        var split = token.Split('.', 2);
        if (split.Length != 2)
        {
            CookieJar.WipeAll(http, options);
            return Results.Json(new { error = "Sign in required.", code = "not_signed_in" }, statusCode: 401);
        }

        var (outcome, newToken) = await sessions.Rotate(split[0], split[1]);
        if (outcome == SessionService.RefreshOutcome.Invalid)
        {
            CookieJar.WipeAll(http, options);
            return Results.Json(new { error = "Sign in required.", code = "not_signed_in" }, statusCode: 401);
        }
        if (outcome == SessionService.RefreshOutcome.Theft)
        {
            // A retired refresh token came back long after rotation: someone holds a
            // stolen copy. The session is already revoked; wipe everything.
            await using var connection = api.Db.App();
            connection.Audit(null, "auth.refresh_reuse", split[0][..Math.Min(8, split[0].Length)], "session revoked after retired token reuse", Requests.ClientIp(http));
            logger.LogWarning("Refresh token reuse detected; session {Session} revoked", split[0][..Math.Min(8, split[0].Length)]);
            CookieJar.WipeAll(http, options);
            return Results.Json(new { error = "Sign in required.", code = "not_signed_in" }, statusCode: 401);
        }

        var sessionId = split[0];
        var session = await sessions.Load(sessionId);
        if (session is null || !SessionService.Usable(session, SessionService.Now()))
        {
            CookieJar.WipeAll(http, options);
            return Results.Json(new { error = "Sign in required.", code = "not_signed_in" }, statusCode: 401);
        }
        CookieJar.WriteAccess(http, options, sessions.IssueAccessToken(session.UserId, sessionId));
        CookieJar.WriteRefresh(http, options, newToken!);
        return Results.NoContent();
    }

    private static IResult TooManyRequests(HttpContext http, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait) http.Response.Headers.RetryAfter = ((int)wait.TotalSeconds).ToString();
        return Results.Json(new { error = "Too many requests. Slow down.", code = "rate_limited" }, statusCode: 429);
    }

    private static IResult Invalid(string code = "invalid_credentials") =>
        Results.Json(new { error = "Email or password is not correct.", code }, statusCode: 401);

    private sealed record UserRow(
        long Id,
        string Email,
        string Name,
        string Role,
        string Permissions,
        long Active,
        string? PasswordHash,
        string? TotpSecret,
        long TotpPending,
        long? TotpLastStep,
        long FailedSignins,
        long? LockedUntil);

    private const string UserColumns = """
        id AS Id, email AS Email, name AS Name, role AS Role, permissions AS Permissions, active AS Active,
        password_hash AS PasswordHash, totp_secret AS TotpSecret, totp_pending AS TotpPending,
        totp_last_step AS TotpLastStep, failed_signins AS FailedSignins, locked_until AS LockedUntil
        """;
}
