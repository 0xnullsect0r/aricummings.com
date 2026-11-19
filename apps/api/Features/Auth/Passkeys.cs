using System.Text.Json;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Crypto;
using Ari.Platform.Database;
using Dapper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Auth;

/// <summary>
/// Passkeys: discoverable credentials, user verification required, attestation none,
/// algorithms Ed25519, ES256 and RS256. Challenges live 5 minutes and work once; the
/// claim is the DELETE itself, so a captured response can't be replayed. A key that has
/// counted before must keep counting, and the stored counter only moves forward.
/// </summary>
public sealed class PasskeyService(
    ApiContext api,
    IFido2 fido2,
    SessionService sessions,
    AuthOptions options,
    ILogger<PasskeyService> logger)
{
    private static readonly TimeSpan ChallengeLife = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions ResponseJson = new() { PropertyNameCaseInsensitive = true };

    private string RpId => api.Env.WebauthnRpId ?? Env.ParseOrigin(api.Env.AdminOrigin).Host;

    private Fido2Configuration Configuration() => new()
    {
        ServerDomain = RpId,
        ServerName = "aricummings.com",
        Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { api.Env.AdminOrigin },
    };

    /// <summary>The passkey user handle: the user id as 8 network-order bytes.</summary>
    private static byte[] Handle(long userId)
    {
        var handle = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(handle, userId);
        return handle;
    }

    // ---- Sign-in ------------------------------------------------------------

    /// <summary>Assertion options for a discoverable credential: the browser asks the
    /// user which account, so no email is sent and none is revealed. Rate-limited per
    /// network: each call stores a challenge row until the daily prune.</summary>
    public async Task<IResult> SignInOptions(HttpContext http)
    {
        var limits = http.RequestServices.GetRequiredService<Platform.Api.RateLimits>();
        var network = Requests.NetworkKey(http);
        if (!limits.TryConsume(limits.SignInNetwork, $"passkey-options:{network}"))
            return Results.Json(new { error = "Too many requests.", code = "rate_limited" }, statusCode: 429);
        var assertion = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerification = UserVerificationRequirement.Required,
        });
        var id = await StoreChallenge(null, assertion.Challenge);
        return Results.Text($"{{\"id\":\"{id}\",\"options\":{assertion.ToJson()}}}", "application/json");
    }

    public async Task<IResult> SignInVerify(HttpContext http)
    {
        var body = await HttpBody.Read(http);
        string id, responseJson;
        try
        {
            using var document = JsonDocument.Parse(body);
            id = document.RootElement.GetProperty("id").GetString() ?? "";
            responseJson = document.RootElement.GetProperty("response").GetRawText();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ApiException(400, "The request body is not valid JSON.", "bad_json");
        }

        // Claim: DELETE ... RETURNING means the challenge is spent exactly once.
        var challenge = await ClaimChallenge(id, requireUserId: null);
        if (challenge is null) throw new ApiException(400, "That sign-in attempt expired. Start again.", "challenge_invalid");

        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(responseJson, ResponseJson)
            ?? throw new ApiException(400, "The passkey response could not be read.", "bad_passkey");
        var credentialId = response.RawId is { Length: > 0 } ? B64.Url(response.RawId) : "";
        await using var connection = api.Db.App();
        var credential = await connection.QuerySingleOrDefaultAsync<(byte[] PublicKey, long SignCount, long UserId)?>(
            "SELECT public_key AS PublicKey, sign_count AS SignCount, user_id AS UserId FROM passkey_credentials WHERE id=@id;",
            new { id = credentialId }, transaction: null);
        if (credential is null)
        {
            throw new ApiException(401, "That passkey is not known here.", "invalid_credentials");
        }
        var stored = credential.Value;

        var original = AssertionOptions.Create(
            Configuration(), challenge, Array.Empty<PublicKeyCredentialDescriptor>(),
            UserVerificationRequirement.Required, null);
        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = original,
                StoredPublicKey = stored.PublicKey,
                StoredSignatureCounter = (uint)stored.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.CredentialId is { Length: > 0 }
                        && args.UserHandle.AsSpan().SequenceEqual(Handle(stored.UserId))),
            }, http.RequestAborted);
        }
        catch (Fido2VerificationException error)
        {
            logger.LogInformation("Passkey assertion rejected: {Reason}", error.Message);
            throw new ApiException(401, "The passkey check failed. Start again.", "invalid_credentials");
        }

        RejectCloneKey(result.SignCount, (int)stored.SignCount);
        var updated = await connection.ExecuteAsync(
            "UPDATE passkey_credentials SET sign_count=@next, last_used_at=@now WHERE id=@id AND sign_count=@previous;",
            new { next = (int)result.SignCount, now = SessionService.Now(), id = credentialId, previous = (int)stored.SignCount });
        if (updated != 1) throw new ApiException(401, "The passkey check failed. Start again.", "invalid_credentials");

        var user = await StaffResolver.LoadUser(api.Db, stored.UserId);
        if (user is null || user.Value.Active != 1)
            throw new ApiException(401, "That account is not available.", "invalid_credentials");

        var now = SessionService.Now();
        await connection.ExecuteAsync("UPDATE users SET step_up_at=@now WHERE id=@id;", new { now, id = stored.UserId });
        var (sessionId, refreshToken) = await sessions.Create(stored.UserId);
        CookieJar.WriteAccess(http, options, sessions.IssueAccessToken(stored.UserId, sessionId));
        CookieJar.WriteRefresh(http, options, refreshToken);
        connection.Audit(stored.UserId, "auth.login", user.Value.Email, "passkey", Requests.ClientIp(http));
        logger.LogInformation("User {User} signed in with a passkey", stored.UserId);
        return Results.NoContent();
    }

    // ---- Registration (account routes) --------------------------------------

    public async Task<IResult> RegisterOptions(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Optional("name", out var name, 100);
        validator.Done();

        await using var connection = api.Db.App();
        var existing = (await connection.QueryAsync<string>("SELECT id FROM passkey_credentials WHERE user_id=@userId;", new { userId = staff.UserId }))
            .Select(raw => new PublicKeyCredentialDescriptor(B64.FromUrl(raw)))
            .ToList();

        var creation = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = Handle(staff.UserId), Name = staff.Email, DisplayName = staff.Name },
            ExcludeCredentials = existing,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams = new[]
            {
                new PubKeyCredParam(COSE.Algorithm.EdDSA, PublicKeyCredentialType.PublicKey),
                new PubKeyCredParam(COSE.Algorithm.ES256, PublicKeyCredentialType.PublicKey),
                new PubKeyCredParam(COSE.Algorithm.RS256, PublicKeyCredentialType.PublicKey),
            },
        });
        var id = await StoreChallenge(staff.UserId, creation.Challenge);
        return Results.Text($"{{\"id\":\"{id}\",\"options\":{creation.ToJson()}}}", "application/json");
    }

    public async Task<IResult> RegisterVerify(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        string id, responseJson, name;
        try
        {
            using var document = JsonDocument.Parse(body);
            id = document.RootElement.GetProperty("id").GetString() ?? "";
            responseJson = document.RootElement.GetProperty("response").GetRawText();
            name = document.RootElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? "" : "";
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ApiException(400, "The request body is not valid JSON.", "bad_json");
        }
        if (name.Length > 100) throw new ApiException(400, "Name is too long.", "invalid_fields");

        var challenge = await ClaimChallenge(id, requireUserId: staff.UserId);
        if (challenge is null) throw new ApiException(400, "That registration attempt expired. Start again.", "challenge_invalid");

        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(responseJson, ResponseJson)
            ?? throw new ApiException(400, "The passkey response could not be read.", "bad_passkey");

        var original = await RebuildCreationOptions(staff, challenge);
        RegisteredPublicKeyCredential success;
        try
        {
            success = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = original,
                IsCredentialIdUniqueToUserCallback = (_, _) => Task.FromResult(true),
            }, http.RequestAborted);
        }
        catch (Fido2VerificationException error)
        {
            logger.LogInformation("Passkey attestation rejected: {Reason}", error.Message);
            throw new ApiException(400, "The passkey could not be registered. Start again.", "invalid_passkey");
        }

        var credentialId = B64.Url(success.Id);
        var transports = success.Transports is { Length: > 0 } used
            ? string.Join(',', used.Select(t => t.ToString().ToLowerInvariant())) : null;
        await using var connection = api.Db.App();
        var inserted = Db.Write(connection, transaction => connection.Execute(
            """
            INSERT INTO passkey_credentials(id, user_id, public_key, sign_count, transports, name, created_at)
            VALUES(@id, @userId, @publicKey, @signCount, @transports, @name, @now);
            """,
            new
            {
                id = credentialId,
                userId = staff.UserId,
                publicKey = success.PublicKey,
                signCount = (int)success.SignCount,
                transports,
                name = name.Length > 0 ? name : "Passkey",
                now = SessionService.Now(),
            }, transaction));
        if (inserted != 1) throw new ApiException(400, "That passkey is already registered.", "duplicate_passkey");
        connection.Audit(staff.UserId, "auth.passkey_registered", credentialId, "", Requests.ClientIp(http));
        logger.LogInformation("User {User} registered passkey {Credential}", staff.UserId, credentialId[..Math.Min(8, credentialId.Length)]);
        return Results.NoContent();
    }

    /// <summary>Listed and deleted by row id: credential ids are case-sensitive base64url
    /// and the API refuses uppercase path segments, so they never appear in a URL.</summary>
    public async Task<IResult> List(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var rows = await connection.QueryAsync<(long RowId, string? Name, long CreatedAt, long? LastUsedAt)>(
            "SELECT rowid AS RowId, name AS Name, created_at AS CreatedAt, last_used_at AS LastUsedAt FROM passkey_credentials WHERE user_id=@userId ORDER BY created_at DESC;",
            new { userId = staff.UserId });
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.RowId,
                name = r.Name,
                createdAt = r.CreatedAt,
                lastUsedAt = r.LastUsedAt,
            }),
        });
    }

    public async Task<IResult> Delete(HttpContext http, long id)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var credentialId = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT id FROM passkey_credentials WHERE rowid=@id AND user_id=@userId;", new { id, userId = staff.UserId });
        if (credentialId is null) throw new ApiException(404, "No such passkey.", "not_found");
        await connection.ExecuteAsync(
            "DELETE FROM passkey_credentials WHERE rowid=@id AND user_id=@userId;", new { id, userId = staff.UserId });
        connection.Audit(staff.UserId, "auth.passkey_removed", credentialId, "", Requests.ClientIp(http));
        return Results.NoContent();
    }


    // ---- internals -----------------------------------------------------------

    /// <summary>A key that has counted before must keep counting: a zero counter after
    /// a non-zero one may mean a cloned authenticator.</summary>
    private static void RejectCloneKey(uint reported, int stored)
    {
        if (stored > 0 && reported == 0)
            throw new ApiException(401, "That passkey looks cloned. Start again.", "passkey_clone");
        if (reported != 0 && reported <= (uint)stored)
            throw new ApiException(401, "That passkey looks cloned. Start again.", "passkey_clone");
    }

    private async Task<CredentialCreateOptions> RebuildCreationOptions(StaffContext staff, byte[] challenge)
    {
        await using var connection = api.Db.App();
        var existing = (await connection.QueryAsync<string>("SELECT id FROM passkey_credentials WHERE user_id=@userId;", new { userId = staff.UserId }))
            .Select(raw => new PublicKeyCredentialDescriptor(B64.FromUrl(raw)))
            .ToList();
        return CredentialCreateOptions.Create(
            Configuration(), challenge,
            new Fido2User { Id = Handle(staff.UserId), Name = staff.Email, DisplayName = staff.Name },
            new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationConveyancePreference.None, existing, null, new[]
            {
                new PubKeyCredParam(COSE.Algorithm.EdDSA, PublicKeyCredentialType.PublicKey),
                new PubKeyCredParam(COSE.Algorithm.ES256, PublicKeyCredentialType.PublicKey),
                new PubKeyCredParam(COSE.Algorithm.RS256, PublicKeyCredentialType.PublicKey),
            });
    }

    private async Task<string> StoreChallenge(long? userId, byte[] challenge)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = SessionService.Now();
        await using var connection = api.Db.App();
        await connection.ExecuteAsync(
            "INSERT INTO passkey_challenges(id, user_id, challenge, created_at, expires_at) VALUES(@id, @userId, @challenge, @now, @expires);",
            new { id, userId, challenge, now, expires = now + (long)ChallengeLife.TotalMilliseconds });
        return id;
    }

    /// <summary>Claims and deletes the challenge row; only a row owned by the required
    /// user (null for sign-in) and still alive can be claimed.</summary>
    private async Task<byte[]?> ClaimChallenge(string id, long? requireUserId)
    {
        if (id.Length is < 16 or > 64 || !id.All(char.IsAsciiLetterOrDigit)) return null;
        await using var connection = api.Db.App();
        return Db.Write(connection, transaction =>
        {
            var challenge = connection.QuerySingleOrDefault<byte[]?>(
                """
                DELETE FROM passkey_challenges
                WHERE id=@id AND expires_at > @now AND (@required IS NULL AND user_id IS NULL OR @required IS NOT NULL AND user_id=@required)
                RETURNING challenge;
                """,
                new { id, now = SessionService.Now(), required = requireUserId }, transaction);
            return challenge;
        });
    }
}
