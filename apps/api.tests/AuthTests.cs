using System.Net;
using System.Text.Json;
using Ari.Features.Auth;
using Ari.Platform.Outbox;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace AriApi.Tests;

/// <summary>
/// Sign-in, lockout, timing parity, refresh rotation and theft detection, invites,
/// resets, TOTP single-use, password rules, cross-site refusal, guards, settings,
/// audit paging and the dashboard — against the real server on real sockets.
/// </summary>
[Collection("AuthSerial")]
public sealed class AuthTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() =>
        _server = await TestServer.Start("auth", rateLimitsDisabled: true);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<(HttpStatusCode Status, JsonElement Body)> SignIn(TestServer.Jar? jar, string email, string password)
    {
        var (status, body, _) = await _server.SendJson("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email, password }), jar);
        return (status, body);
    }

    // ---- sign-in ------------------------------------------------------------

    [Fact]
    public async Task Unknown_email_costs_exactly_what_a_wrong_password_costs()
    {
        _server.CreateUser("parity@example.com");
        var hasher = _server.Hasher;

        var before = hasher.HashCount;
        var (statusUnknown, bodyUnknown, messageUnknown) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "ghost@example.com", password = "a-totally-wrong-password" }));
        var unknownHashes = hasher.HashCount - before;

        before = hasher.HashCount;
        var (statusWrong, bodyWrong, messageWrong) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "parity@example.com", password = "a-totally-wrong-password" }));
        var wrongHashes = hasher.HashCount - before;

        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)statusUnknown);
        Assert.Equal((int)statusUnknown, (int)statusWrong);
        Assert.Equal(bodyUnknown, bodyWrong);
        // The unknown-email path must still run one full Argon2id: no timing oracle.
        Assert.Equal(1, unknownHashes);
        Assert.Equal(1, wrongHashes);
        Assert.Contains("not correct", bodyUnknown);
        _ = messageUnknown; _ = messageWrong;
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_with_the_right_password()
    {
        _server.CreateUser("locked@example.com");

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var (status, body) = await SignIn(null, "locked@example.com", "definitely-not-it-" + attempt);
            Assert.Equal((int)HttpStatusCode.Unauthorized, (int)status);
            Assert.Contains("invalid_credentials", body.GetRawText());
        }

        // The fifth failure crosses the line and answers locked_out.
        var (fifth, fifthBody) = await SignIn(null, "locked@example.com", "definitely-not-it-5");
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)fifth);
        Assert.Contains("locked_out", fifthBody.GetRawText());

        var (correct, _) = await SignIn(null, "locked@example.com", TestServer.DefaultPassword);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)correct);

        var row = _server.Query<(int Failed, long? LockedUntil)>(
            "SELECT failed_signins AS Failed, locked_until AS LockedUntil FROM users WHERE email='locked@example.com';");
        Assert.Equal(5, row.Failed);
        Assert.NotNull(row.LockedUntil);
        Assert.True(row.LockedUntil.Value > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Success_sets_exact_cookies_and_me_answers()
    {
        _server.CreateUser("cookie@example.com");
        var jar = new TestServer.Jar();

        var (status, _, message) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "cookie@example.com", password = TestServer.DefaultPassword }), jar);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)status);

        var setCookies = message.Headers.GetValues("Set-Cookie").ToList();
        var access = setCookies.Single(c => c.StartsWith("__Host-ari_at=", StringComparison.Ordinal));
        var refresh = setCookies.Single(c => c.StartsWith("__Secure-ari_rt=", StringComparison.Ordinal));
        Assert.Contains("Path=/;", access);
        Assert.Contains("HttpOnly", access);
        Assert.Contains("Secure", access);
        Assert.Contains("SameSite=Strict", access);
        Assert.Contains("Path=/api/auth", refresh);
        Assert.Contains("HttpOnly", refresh);
        Assert.Contains("SameSite=Strict", refresh);

        var (meStatus, me, _) = await _server.SendJson("GET", "/api/auth/me", jar: jar);
        Assert.Equal((int)HttpStatusCode.OK, (int)meStatus);
        Assert.Equal("cookie@example.com", me.GetProperty("email").GetString());
        Assert.Equal("staff", me.GetProperty("role").GetString());

        var (anonymous, anonymousBody, _) = await _server.SendJson("GET", "/api/auth/me");
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)anonymous);
        Assert.Equal("not_signed_in", anonymousBody.GetProperty("code").GetString());
    }

    // ---- refresh ------------------------------------------------------------

    [Fact]
    public async Task Refresh_rotates_reuses_revoke_and_never_issued_is_only_invalid()
    {
        _server.CreateUser("rotate@example.com");
        var jar = new TestServer.Jar();
        await SignIn(jar, "rotate@example.com", TestServer.DefaultPassword);
        var rt1 = jar["__Secure-ari_rt"];
        Assert.NotNull(rt1);

        var (rotated, _, _) = await _server.Send("POST", "/api/auth/refresh", jar: jar);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)rotated);
        var rt2 = jar["__Secure-ari_rt"];
        Assert.NotNull(rt2);
        Assert.NotEqual(rt1, rt2);

        var (again, _, _) = await _server.Send("POST", "/api/auth/refresh", jar: jar);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)again);
        var rt3 = jar["__Secure-ari_rt"];
        Assert.NotEqual(rt2, rt3);

        // A value that was never issued proves nothing: only invalid, session lives on.
        var junk = new TestServer.Jar();
        junk.Set("__Secure-ari_rt", "not-a-session.abc", "/api/auth");
        var (junkStatus, _, _) = await _server.Send("POST", "/api/auth/refresh", jar: junk);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)junkStatus);
        var (stillFine, _, _) = await _server.Send("POST", "/api/auth/refresh", jar: jar);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)stillFine);
        var rt4 = jar["__Secure-ari_rt"];

        // Age every retired hash past the 30-second grace, then replay rt2: theft.
        var retired = _server.Query<string>(
            "SELECT retired_hashes FROM sessions WHERE id=@id;", new { id = rt2!.Split('.')[0] });
        var entries = JsonSerializer.Deserialize<List<JsonElement>>(retired)!;
        var aged = "[" + string.Join(",", entries.Select(e =>
            $"{{\"h\":\"{e.GetProperty("h").GetString()}\",\"t\":{e.GetProperty("t").GetInt64() - 120_000}}}")) + "]";
        _server.Execute("UPDATE sessions SET retired_hashes=@aged WHERE id=@id;", new { aged, id = rt2.Split('.')[0] });

        var thief = new TestServer.Jar();
        thief.Set("__Secure-ari_rt", rt2, "/api/auth");
        var (theft, theftBody, _) = await _server.Send("POST", "/api/auth/refresh", jar: thief);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)theft);
        Assert.Equal("not_signed_in", JsonSerializer.Deserialize<JsonElement>(theftBody).GetProperty("code").GetString());

        // The whole session is revoked: even the freshest token is refused now.
        var (revoked, _, _) = await _server.Send("POST", "/api/auth/refresh", jar: jar);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)revoked);
        var (me, _, _) = await _server.SendJson("GET", "/api/auth/me", jar: jar);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)me);
        _ = rt4;
    }

    // ---- invites and resets ---------------------------------------------------

    [Fact]
    public async Task Invite_from_admin_is_consumed_exactly_once()
    {
        _server.CreateUser("boss@example.com", role: "owner");
        var owner = new TestServer.Jar();
        await SignIn(owner, "boss@example.com", TestServer.DefaultPassword);

        var (inviteStatus, invite, _) = await _server.SendJson("POST", "/api/admin/users",
            JsonSerializer.Serialize(new { email = "newbie@example.com", name = "Newbie" }), owner);
        Assert.Equal((int)HttpStatusCode.Created, (int)inviteStatus);
        var url = invite.GetProperty("invite").GetString()!;
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];

        var (accepted, _, _) = await _server.SendJson("POST", "/api/auth/invite/accept",
            JsonSerializer.Serialize(new { token, name = "Newbie Person", password = "glossy-tundra-marble-violet" }));
        Assert.Equal((int)HttpStatusCode.NoContent, (int)accepted);

        var (replayed, replayBody, _) = await _server.SendJson("POST", "/api/auth/invite/accept",
            JsonSerializer.Serialize(new { token, name = "Newbie Person", password = "glossy-tundra-marble-violet" }));
        Assert.Equal((int)HttpStatusCode.BadRequest, (int)replayed);
        Assert.Equal("link_invalid", replayBody.GetProperty("code").GetString());

        var jar = new TestServer.Jar();
        var (signedIn, _) = await SignIn(jar, "newbie@example.com", "glossy-tundra-marble-violet");
        Assert.Equal((int)HttpStatusCode.NoContent, (int)signedIn);
    }

    [Fact]
    public async Task Owner_reset_link_sets_password_and_kills_sessions()
    {
        var staffId = _server.CreateUser("resetme@example.com");
        var victim = new TestServer.Jar();
        await SignIn(victim, "resetme@example.com", TestServer.DefaultPassword);

        _server.CreateUser("root@example.com", role: "owner");
        var owner = new TestServer.Jar();
        await SignIn(owner, "root@example.com", TestServer.DefaultPassword);

        var (resetStatus, reset, _) = await _server.SendJson("POST", $"/api/admin/users/{staffId}/reset", json: "{}", jar: owner);
        Assert.Equal((int)HttpStatusCode.OK, (int)resetStatus);
        var url = reset.GetProperty("reset").GetString()!;
        var token = url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..];

        var (confirmed, _, _) = await _server.SendJson("POST", "/api/auth/reset/confirm",
            JsonSerializer.Serialize(new { token, password = "copper-lantern-ninth-quartz" }));
        Assert.Equal((int)HttpStatusCode.NoContent, (int)confirmed);

        var (oldSession, _, _) = await _server.SendJson("GET", "/api/auth/me", jar: victim);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)oldSession);

        var jar = new TestServer.Jar();
        var (signedIn, _) = await SignIn(jar, "resetme@example.com", "copper-lantern-ninth-quartz");
        Assert.Equal((int)HttpStatusCode.NoContent, (int)signedIn);
    }

    // ---- TOTP -----------------------------------------------------------------

    [Fact]
    public async Task Totp_stays_pending_until_proven_and_each_step_is_single_use()
    {
        _server.CreateUser("totp@example.com");
        var jar = new TestServer.Jar();
        await SignIn(jar, "totp@example.com", TestServer.DefaultPassword);

        var (startStatus, start, _) = await _server.SendJson("POST", "/api/auth/account/totp/start", json: "{}", jar: jar);
        Assert.Equal((int)HttpStatusCode.OK, (int)startStatus);
        var secret = start.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrEmpty(secret));
        Assert.Contains("otpauth://totp/", start.GetProperty("url").GetString());
        Assert.Contains("<svg", start.GetProperty("qr").GetString());

        // Pending: sign-in must not demand a code yet.
        var pending = new TestServer.Jar();
        var (stillDirect, _, _) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "totp@example.com", password = TestServer.DefaultPassword }), pending);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)stillDirect);

        // Confirm with a code computed for the step after the confirming code's, so
        // the window always accepts it whatever wall-clock second we are in.
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var confirmCode = totp.ComputeTotp(DateTime.UtcNow);
        totp.VerifyTotp(confirmCode, out var confirmStep, new VerificationWindow(1, 1));
        var (confirmStatus, confirmed, _) = await _server.SendJson("POST", "/api/auth/account/totp/confirm",
            JsonSerializer.Serialize(new { code = confirmCode }), jar: jar);
        Assert.Equal((int)HttpStatusCode.OK, (int)confirmStatus);
        var codes = confirmed.GetProperty("recoveryCodes").EnumerateArray().ToList();
        Assert.Equal(10, codes.Count);

        // Now 2FA is required.
        var jar2 = new TestServer.Jar();
        var (needs2Fa, next, _) = await _server.SendJson("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "totp@example.com", password = TestServer.DefaultPassword }), jar2);
        Assert.Equal((int)HttpStatusCode.OK, (int)needs2Fa);
        Assert.Equal("2fa", next.GetProperty("next").GetString());
        var pendingToken = next.GetProperty("token").GetString()!;

        var stepCode = totp.ComputeTotp(DateTimeOffset.FromUnixTimeSeconds((confirmStep + 1) * 30).UtcDateTime);
        var (ok, _, _) = await _server.Send("POST", "/api/auth/2fa",
            JsonSerializer.Serialize(new { token = pendingToken, code = stepCode }), jar2);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)ok);

        // The same code is spent: a second sign-in with it is refused.
        var jar3 = new TestServer.Jar();
        var (_, again, _) = await _server.SendJson("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "totp@example.com", password = TestServer.DefaultPassword }), jar3);
        var token3 = again.GetProperty("token").GetString()!;
        var (replayed, replayBody, _) = await _server.SendJson("POST", "/api/auth/2fa",
            JsonSerializer.Serialize(new { token = token3, code = stepCode }), jar3);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)replayed);
        Assert.Equal("invalid_code", replayBody.GetProperty("code").GetString());

        // A recovery code works, once.
        var jar4 = new TestServer.Jar();
        var (_, fourth, _) = await _server.SendJson("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "totp@example.com", password = TestServer.DefaultPassword }), jar4);
        var token4 = fourth.GetProperty("token").GetString()!;
        var recovery = codes[0].GetString()!;
        var (recoveryStatus, _, _) = await _server.Send("POST", "/api/auth/2fa",
            JsonSerializer.Serialize(new { token = token4, code = recovery }), jar4);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)recoveryStatus);

        var jar5 = new TestServer.Jar();
        var (_, fifth, _) = await _server.SendJson("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "totp@example.com", password = TestServer.DefaultPassword }), jar5);
        var token5 = fifth.GetProperty("token").GetString()!;
        var (reusedRecovery, _, _) = await _server.SendJson("POST", "/api/auth/2fa",
            JsonSerializer.Serialize(new { token = token5, code = recovery }), jar5);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)reusedRecovery);
    }

    // ---- password rules ---------------------------------------------------------

    [Fact]
    public async Task Weak_passwords_are_refused_and_a_change_revokes_other_sessions()
    {
        _server.CreateUser("changer@example.com");
        var jar = new TestServer.Jar();
        await SignIn(jar, "changer@example.com", TestServer.DefaultPassword);
        var other = new TestServer.Jar();
        await SignIn(other, "changer@example.com", TestServer.DefaultPassword);

        foreach (var weak in new[] { "short", "aaaaaaaaaaaa", "ari-ari-ari-ari", "passwordpassword" })
        {
            var (status, body, _) = await _server.SendJson("PUT", "/api/auth/account/password",
                JsonSerializer.Serialize(new { current = TestServer.DefaultPassword, next = weak }), jar);
            Assert.Equal((int)HttpStatusCode.BadRequest, (int)status);
            Assert.Equal("weak_password", body.GetProperty("code").GetString());
        }

        var (wrongCurrent, _, _) = await _server.SendJson("PUT", "/api/auth/account/password",
            JsonSerializer.Serialize(new { current = "not-the-current-one", next = "velvet-cactus-harbor-77" }), jar);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)wrongCurrent);

        var (changed, _, _) = await _server.SendJson("PUT", "/api/auth/account/password",
            JsonSerializer.Serialize(new { current = TestServer.DefaultPassword, next = "velvet-cactus-harbor-77" }), jar);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)changed);

        var (me, _, _) = await _server.SendJson("GET", "/api/auth/me", jar: jar);
        Assert.Equal((int)HttpStatusCode.OK, (int)me);

        var (otherSession, _, _) = await _server.SendJson("GET", "/api/auth/me", jar: other);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)otherSession);

        var (oldPassword, _, _) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "changer@example.com", password = TestServer.DefaultPassword }));
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)oldPassword);
        var fresh = new TestServer.Jar();
        var (newPassword, _) = await SignIn(fresh, "changer@example.com", "velvet-cactus-harbor-77");
        Assert.Equal((int)HttpStatusCode.NoContent, (int)newPassword);
    }

    // ---- cross-site --------------------------------------------------------------

    [Fact]
    public async Task Writes_from_a_foreign_origin_are_refused_as_cross_site()
    {
        _server.CreateUser("crosssite@example.com");

        var (evil, evilBody, _) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "crosssite@example.com", password = TestServer.DefaultPassword }),
            origin: "https://evil.example");
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)evil);
        Assert.Equal("cross_site", JsonSerializer.Deserialize<JsonElement>(evilBody).GetProperty("code").GetString());

        // No Origin and no Sec-Fetch-Site: not from a page, refused.
        var (noMarkers, noBody, _) = await _server.Send("POST", "/api/auth/signin",
            JsonSerializer.Serialize(new { email = "crosssite@example.com", password = TestServer.DefaultPassword }),
            browserFetch: false);
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)noMarkers);
        Assert.Contains("cross_site", noBody);

        var (contactEvil, contactBody, _) = await _server.Send("POST", "/api/contact",
            JsonSerializer.Serialize(new { name = "a", email = "a@example.com", body = "hi" }),
            origin: "https://evil.example", port: _server.PublicPort);
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)contactEvil);
        Assert.Contains("cross_site", contactBody);
    }

    // ---- guards --------------------------------------------------------------------

    [Fact]
    public async Task Permissions_and_step_up_are_enforced_on_the_server()
    {
        _server.CreateUser("plain@example.com");
        var plain = new TestServer.Jar();
        await SignIn(plain, "plain@example.com", TestServer.DefaultPassword);

        var (forbidden, forbiddenBody, _) = await _server.SendJson("GET", "/api/admin/users", jar: plain);
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)forbidden);
        Assert.Equal("forbidden", forbiddenBody.GetProperty("code").GetString());

        var (settingsForbidden, _, _) = await _server.SendJson("GET", "/api/admin/settings", jar: plain);
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)settingsForbidden);

        _server.CreateUser("chief@example.com", role: "owner");
        var chief = new TestServer.Jar();
        await SignIn(chief, "chief@example.com", TestServer.DefaultPassword);

        var (listOk, list, _) = await _server.SendJson("GET", "/api/admin/users", jar: chief);
        Assert.Equal((int)HttpStatusCode.OK, (int)listOk);
        Assert.True(list.GetProperty("items").EnumerateArray().Count() >= 2);
        var permissionKeys = list.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetProperty("key").GetString()!).ToList();
        Assert.Contains("content.publish", permissionKeys);
        Assert.Equal(10, permissionKeys.Count);

        // Step-up older than 10 minutes: the invite route answers step_up_required.
        _server.Execute("UPDATE users SET step_up_at=@old WHERE email='chief@example.com';",
            new { old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 20 * 60 * 1000 });
        var (stepUp, stepUpBody, _) = await _server.SendJson("POST", "/api/admin/users",
            JsonSerializer.Serialize(new { email = "someone@example.com", name = "Someone" }), chief);
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)stepUp);
        Assert.Equal("step_up_required", stepUpBody.GetProperty("code").GetString());

        // The stepup endpoint re-opens the window: wrong password first, then the real one.
        var (wrongStep, wrongStepBody, _) = await _server.SendJson("POST", "/api/auth/stepup",
            JsonSerializer.Serialize(new { password = "not-the-password" }), chief);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)wrongStep);
        Assert.Equal("invalid_credentials", wrongStepBody.GetProperty("code").GetString());

        var (reconfirmed, _, _) = await _server.SendJson("POST", "/api/auth/stepup",
            JsonSerializer.Serialize(new { password = TestServer.DefaultPassword }), chief);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)reconfirmed);

        var (invitedNow, inviteBody, _) = await _server.SendJson("POST", "/api/admin/users",
            JsonSerializer.Serialize(new { email = "someone@example.com", name = "Someone" }), chief);
        Assert.Equal((int)HttpStatusCode.Created, (int)invitedNow);
        Assert.NotEmpty(inviteBody.GetProperty("invite").GetString()!);
    }

    // ---- settings -------------------------------------------------------------------

    [Fact]
    public async Task Settings_roundtrip_keeps_secrets_and_feeds_smtp()
    {
        _server.CreateUser("sowner@example.com", role: "owner");
        var owner = new TestServer.Jar();
        await SignIn(owner, "sowner@example.com", TestServer.DefaultPassword);

        var (host, _, _) = await _server.SendJson("PUT", "/api/admin/settings/smtp.host",
            JsonSerializer.Serialize(new { value = "smtp.example.com" }), owner);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)host);
        var (portSetting, _, _) = await _server.SendJson("PUT", "/api/admin/settings/smtp.port",
            JsonSerializer.Serialize(new { value = "465" }), owner);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)portSetting);
        var (password, _, _) = await _server.SendJson("PUT", "/api/admin/settings/smtp.password",
            JsonSerializer.Serialize(new { value = "seekrit-passphrase" }), owner);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)password);
        var (from, _, _) = await _server.SendJson("PUT", "/api/admin/settings/smtp.from_address",
            JsonSerializer.Serialize(new { value = "site@aricummings.com" }), owner);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)from);

        var (listStatus, list, _) = await _server.SendJson("GET", "/api/admin/settings", jar: owner);
        Assert.Equal((int)HttpStatusCode.OK, (int)listStatus);
        var items = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString()!);
        Assert.Equal("smtp.example.com", items["smtp.host"].GetProperty("value").GetString());
        var stored = items["smtp.password"];
        Assert.True(stored.GetProperty("secret").GetBoolean());
        Assert.True(stored.GetProperty("hasValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("value").ValueKind);
        // The stored row is sealed, not plaintext.
        var raw = _server.Query<string>("SELECT value FROM settings WHERE key='smtp.password';");
        Assert.StartsWith("v1.", raw);
        Assert.DoesNotContain("seekrit", raw);

        var config = _server.App.Services.GetRequiredService<ISmtpConfig>().Read();
        Assert.NotNull(config);
        Assert.Equal("smtp.example.com", config!.Value.Host);
        Assert.Equal(465, config.Value.Port);
        Assert.Equal("seekrit-passphrase", config.Value.Password);
        Assert.Equal("site@aricummings.com", config.Value.FromAddress);
    }

    // ---- audit ------------------------------------------------------------------------

    [Fact]
    public async Task Audit_log_reads_back_with_cursor_paging()
    {
        _server.CreateUser("auditor@example.com", permissionsJson: """["audit.read"]""");
        var auditor = new TestServer.Jar();
        await SignIn(auditor, "auditor@example.com", TestServer.DefaultPassword);

        var (status, page, _) = await _server.SendJson("GET", "/api/admin/audit", jar: auditor);
        Assert.Equal((int)HttpStatusCode.OK, (int)status);
        var firstPage = page.GetProperty("items").EnumerateArray().ToList();
        Assert.True(firstPage.Count > 0);
        Assert.Contains("auth.login", firstPage.Select(e => e.GetProperty("action").GetString()));

        // Enough rows of our own to page through, whatever ran before this test.
        for (var i = 0; i < 6; i++)
            _server.Execute(
                "INSERT INTO audit_log(at, actor, action, target, detail, ip) VALUES(@at, NULL, 'test.row', @target, '', '127.0.0.1');",
                new { at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), target = "#t" + i });

        var (paged, pageTwo, _) = await _server.SendJson("GET", "/api/admin/audit?limit=3", jar: auditor);
        var three = pageTwo.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, three.Count);
        var next = pageTwo.GetProperty("next");
        Assert.Equal(JsonValueKind.Number, next.ValueKind);

        var (secondStatus, after, _) = await _server.SendJson("GET", $"/api/admin/audit?limit=3&before={next.GetInt64()}",
            jar: auditor);
        Assert.Equal((int)HttpStatusCode.OK, (int)secondStatus);
        var secondPage = after.GetProperty("items").EnumerateArray().ToList();
        Assert.True(secondPage.Count > 0);
        var firstIds = three.Select(e => e.GetProperty("id").GetInt64()).ToHashSet();
        var secondIds = secondPage.Select(e => e.GetProperty("id").GetInt64());
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.True(secondIds.Max() < firstIds.Min());

        // Without the permission: refused.
        _server.CreateUser("noaudit@example.com");
        var noAudit = new TestServer.Jar();
        await SignIn(noAudit, "noaudit@example.com", TestServer.DefaultPassword);
        var (refused, _, _) = await _server.SendJson("GET", "/api/admin/audit", jar: noAudit);
        Assert.Equal((int)HttpStatusCode.Forbidden, (int)refused);
    }

    // ---- sessions ----------------------------------------------------------------------

    [Fact]
    public async Task Sessions_list_revoke_and_signout()
    {
        _server.CreateUser("sessions@example.com");
        var jar1 = new TestServer.Jar();
        await SignIn(jar1, "sessions@example.com", TestServer.DefaultPassword);
        var jar2 = new TestServer.Jar();
        await SignIn(jar2, "sessions@example.com", TestServer.DefaultPassword);

        var (listStatus, list, _) = await _server.SendJson("GET", "/api/auth/account/sessions", jar: jar1);
        Assert.Equal((int)HttpStatusCode.OK, (int)listStatus);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Single(items, i => i.GetProperty("current").GetBoolean());
        var other = items.Single(i => !i.GetProperty("current").GetBoolean());
        Assert.False(other.GetProperty("revoked").GetBoolean());

        var (revoked, _, _) = await _server.SendJson("DELETE", "/api/auth/account/sessions/" + other.GetProperty("id").GetString(),
            jar: jar1);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)revoked);
        var (otherDead, _, _) = await _server.SendJson("GET", "/api/auth/me", jar: jar2);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)otherDead);

        var (signedOut, _, _) = await _server.SendJson("POST", "/api/auth/signout", json: "{}", jar: jar1);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)signedOut);
        var (me, _, _) = await _server.SendJson("GET", "/api/auth/me", jar: jar1);
        Assert.Equal((int)HttpStatusCode.Unauthorized, (int)me);
    }

    // ---- messages inbox + overview --------------------------------------------------------

    [Fact]
    public async Task Message_inbox_routes_and_overview()
    {
        var (contact, _, _) = await _server.Send("POST", "/api/contact",
            JsonSerializer.Serialize(new { name = "Visitor", email = "visitor@example.com", body = "Hello there" }),
            port: _server.PublicPort);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)contact);

        _server.CreateUser("clerk@example.com", permissionsJson: """["messages.read","messages.manage","analytics.read"]""");
        var clerk = new TestServer.Jar();
        await SignIn(clerk, "clerk@example.com", TestServer.DefaultPassword);

        var (inboxStatus, inbox, _) = await _server.SendJson("GET", "/api/admin/messages", jar: clerk);
        Assert.Equal((int)HttpStatusCode.OK, (int)inboxStatus);
        var items = inbox.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(1, inbox.GetProperty("unread").GetInt32());
        Assert.Equal("Visitor", items[0].GetProperty("name").GetString());

        var id = items[0].GetProperty("id").GetInt64();
        var (read, _, _) = await _server.SendJson("PATCH", $"/api/admin/messages/{id}",
            JsonSerializer.Serialize(new { read = "true" }), clerk);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)read);
        var (archived, _, _) = await _server.SendJson("PATCH", $"/api/admin/messages/{id}",
            JsonSerializer.Serialize(new { archived = "true" }), clerk);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)archived);

        var (overviewStatus, overview, _) = await _server.SendJson("GET", "/api/admin/overview", jar: clerk);
        Assert.Equal((int)HttpStatusCode.OK, (int)overviewStatus);
        Assert.Equal(0, overview.GetProperty("messagesUnread").GetInt32());
        Assert.True(overview.GetProperty("jobs").EnumerateArray().Count() >= 0);
        Assert.True(overview.GetProperty("analytics").GetProperty("visits7d").ValueKind is JsonValueKind.Number);

        var (deleted, _, _) = await _server.SendJson("DELETE", $"/api/admin/messages/{id}", jar: clerk);
        Assert.Equal((int)HttpStatusCode.NoContent, (int)deleted);
        var (empty, emptyBody, _) = await _server.SendJson("GET", "/api/admin/messages", jar: clerk);
        Assert.Empty(emptyBody.GetProperty("items").EnumerateArray());
        _ = empty;
    }

    // ---- passkey surfaces -------------------------------------------------------------------

    [Fact]
    public async Task Passkey_options_are_served_and_challenges_die_once()
    {
        _server.CreateUser("keyed@example.com");
        var jar = new TestServer.Jar();
        await SignIn(jar, "keyed@example.com", TestServer.DefaultPassword);

        var (signInOptions, options, _) = await _server.SendJson("POST", "/api/auth/passkey/options", json: "{}");
        Assert.Equal((int)HttpStatusCode.OK, (int)signInOptions);
        Assert.False(string.IsNullOrEmpty(options.GetProperty("id").GetString()));
        Assert.Equal("localhost", options.GetProperty("options").GetProperty("rpId").GetString());
        Assert.Equal(1, _server.Query<long>("SELECT COUNT(*) FROM passkey_challenges;"));

        var (registerOptions, registration, _) = await _server.SendJson("POST", "/api/auth/account/passkeys/register",
            JsonSerializer.Serialize(new { name = "Yubikey" }), jar);
        Assert.Equal((int)HttpStatusCode.OK, (int)registerOptions);
        var registrationId = registration.GetProperty("id").GetString()!;
        Assert.NotEmpty(registration.GetProperty("options").GetProperty("challenge").GetString()!);

        // A garbage response is refused, and it burns the challenge: the same id can
        // never be verified again.
        var (rejected, rejectedBody, _) = await _server.SendJson("POST", "/api/auth/account/passkeys/register/verify",
            JsonSerializer.Serialize(new { id = registrationId, response = new { } }), jar);
        Assert.Equal((int)HttpStatusCode.BadRequest, (int)rejected);
        Assert.True(rejectedBody.GetProperty("code").GetString() is "invalid_passkey" or "bad_passkey");
        var (replayed, replayBody, _) = await _server.SendJson("POST", "/api/auth/account/passkeys/register/verify",
            JsonSerializer.Serialize(new { id = registrationId, response = new { } }), jar);
        Assert.Equal((int)HttpStatusCode.BadRequest, (int)replayed);
        Assert.Equal("challenge_invalid", replayBody.GetProperty("code").GetString());

        var (list, listBody, _) = await _server.SendJson("GET", "/api/auth/account/passkeys", jar: jar);
        Assert.Equal((int)HttpStatusCode.OK, (int)list);
        Assert.Empty(listBody.GetProperty("items").EnumerateArray());
        _ = list;
    }
}
