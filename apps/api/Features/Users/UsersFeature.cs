using System.Text.Json;
using Ari.Features.Auth;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Dapper;

namespace Ari.Features.Users;

/// <summary>
/// Staff management: invite-only creation, owner-only changes, owner + step-up for
/// password resets. The permission list is exactly the site's, and a role or permission
/// string that is not known here is refused.
/// </summary>
public sealed class UsersService(
    ApiContext api,
    ILogger<UsersService> logger)
{
    private sealed record UserRow(
        long Id, string Email, string Name, string Role, string Permissions, long Active,
        string? PasswordHash, string? TotpSecret, long TotpPending, long CreatedAt);

    private const string UserColumns = """
        id AS Id, email AS Email, name AS Name, role AS Role, permissions AS Permissions, active AS Active,
        password_hash AS PasswordHash, totp_secret AS TotpSecret, totp_pending AS TotpPending, created_at AS CreatedAt
        """;

    public async Task<IResult> List(HttpContext http)
    {
        await using var connection = api.Db.App();
        var rows = await connection.QueryAsync<UserRow>($"SELECT {UserColumns} FROM users ORDER BY created_at;");
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                email = r.Email,
                name = r.Name,
                role = r.Role,
                permissions = StaffResolver.ParsePermissions(r.Permissions),
                active = r.Active == 1,
                hasPassword = r.PasswordHash is not null,
                hasTotp = r.TotpSecret is not null && r.TotpPending == 0,
                createdAt = r.CreatedAt,
            }),
            permissions = Permissions.All.Select(p => new { key = p.Key, description = p.Description }),
        });
    }

    public async Task<IResult> Invite(HttpContext http)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Require("email", out var email, 3, 320);
        validator.Require("name", out var name, 1, 100);
        validator.Optional("role", out var role, 10);
        validator.Done();
        email = email.Trim().ToLowerInvariant();
        if (!email.Contains('@') || email.Any(char.IsWhiteSpace))
            throw new ApiException(400, "Email is not valid.", "invalid_fields");
        role = role.Length == 0 ? "staff" : role;
        if (role is not ("owner" or "staff"))
            throw new ApiException(400, "Role must be owner or staff.", "invalid_fields");
        var permissions = ParsePermissions(body)
            ?? throw new ApiException(400, "Permissions must be a list of known keys.", "invalid_fields");

        await using var connection = api.Db.App();
        var existing = await connection.QuerySingleOrDefaultAsync<long?>("SELECT id FROM users WHERE email=@email;", new { email });
        if (existing is not null) throw new ApiException(400, "That email already has an account.", "duplicate_email");

        var now = SessionService.Now();
        var userId = Db.Write(connection, transaction => connection.QuerySingle<long>(
            """
            INSERT INTO users(email, name, role, permissions, active, created_at)
            VALUES(@email, @name, @role, @permissions, 1, @now)
            RETURNING id;
            """,
            new { email, name, role, permissions = JsonSerializer.Serialize(permissions), now }, transaction));

        var link = await new LinkService(api.Db).CreateInvite(userId, email, api.Env.AdminOrigin);
        connection.Audit(staff.UserId, "users.invited", email, role, Requests.ClientIp(http));
        logger.LogInformation("Owner {Owner} invited {Email} as {Role}", staff.UserId, email, role);
        return Results.Json(new { id = userId, invite = link.Url, inviteExpiresDays = 7 }, statusCode: 201);
    }

    public async Task<IResult> Update(HttpContext http, long id)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        var body = await HttpBody.Read(http);
        var validator = new Validator(body);
        validator.Optional("name", out var name, 100);
        validator.Optional("role", out var role, 10);
        validator.Optional("active", out var activeText, 10);
        validator.Done();
        if (role.Length > 0 && role is not ("owner" or "staff"))
            throw new ApiException(400, "Role must be owner or staff.", "invalid_fields");
        if (activeText.Length > 0 && activeText is not ("true" or "false"))
            throw new ApiException(400, "Active must be true or false.", "invalid_fields");
        var permissions = ParsePermissions(body)
            ?? throw new ApiException(400, "Permissions must be a list of known keys.", "invalid_fields");

        await using var connection = api.Db.App();
        var user = await connection.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {UserColumns} FROM users WHERE id=@id;", new { id });
        if (user is null) throw new ApiException(404, "No such user.", "not_found");

        var nextRole = role.Length > 0 ? role : user.Role;
        var nextActive = activeText.Length > 0 ? activeText == "true" : user.Active == 1;
        if ((nextRole != user.Role || nextActive != (user.Active == 1)) && user.Role == "owner")
        {
            // Never leave the site without one active owner.
            var activeOwners = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM users WHERE role='owner' AND active=1 AND id != @id;", new { id });
            if (activeOwners == 0)
                throw new ApiException(400, "The last active owner cannot be demoted or disabled.", "last_owner");
        }

        var newName = name.Length > 0 ? name : user.Name;
        var nextPermissions = permissions.Count > 0 || HasPermissions(body)
            ? permissions
            : StaffResolver.ParsePermissions(user.Permissions);
        await connection.ExecuteAsync(
            "UPDATE users SET name=@name, role=@role, permissions=@permissions, active=@active WHERE id=@id;",
            new { name = newName, role = nextRole, permissions = JsonSerializer.Serialize(nextPermissions), active = nextActive ? 1 : 0, id });
        connection.Audit(staff.UserId, "users.updated", user.Email,
            $"role={nextRole} active={nextActive}", Requests.ClientIp(http));
        logger.LogInformation("Owner {Owner} updated user {User}", staff.UserId, id);
        return Results.NoContent();
    }

    public async Task<IResult> Reset(HttpContext http, long id)
    {
        var staff = http.Staff() ?? throw new ApiException(401, "Sign in required.", "not_signed_in");
        await using var connection = api.Db.App();
        var user = await connection.QuerySingleOrDefaultAsync<(string Email, long Active)?>(
            "SELECT email AS Email, active AS Active FROM users WHERE id=@id;", new { id });
        if (user is null) throw new ApiException(404, "No such user.", "not_found");

        var link = await new LinkService(api.Db).CreateReset(id, user.Value.Email, api.Env.AdminOrigin);
        await api.Queue.Enqueue("mail", new Dictionary<string, string>
        {
            ["to"] = user.Value.Email,
            ["subject"] = "aricummings.com password reset",
            ["text"] = "An administrator issued you a password reset.\n\nOpen this link within one hour to choose a new password:\n\n" + link.Url + "\n",
        });
        connection.Audit(staff.UserId, "users.reset_issued", user.Value.Email, "", Requests.ClientIp(http));
        logger.LogInformation("Owner {Owner} issued a reset for user {User}", staff.UserId, id);
        return Results.Json(new { reset = link.Url, expiresMinutes = 60 });
    }

    /// <summary>The permissions field when present, else an empty list. Unknown keys are
    /// refused by returning null so the caller can answer one field error.</summary>
    private static List<string>? ParsePermissions(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("permissions", out var element)) return new List<string>();
            if (element.ValueKind != JsonValueKind.Array) return null;
            var list = element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .ToList();
            if (list.Any(p => !Permissions.IsKnown(p))) return null;
            return list;
        }
        catch (JsonException) { return null; }
    }

    private static bool HasPermissions(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("permissions", out _);
        }
        catch (JsonException) { return false; }
    }
}

public sealed class UsersFeature : IFeature
{
    public int Order => 80;

    public void Register(IServiceCollection services) => services.AddSingleton<UsersService>();

    public void Map(WebApplication app)
    {
        app.MapGet("/api/admin/users", async (HttpContext http, UsersService service) => await service.List(http))
            .AddEndpointFilter(Guards.RequirePermission("users.manage"));

        app.MapPost("/api/admin/users", async (HttpContext http, UsersService service) => await service.Invite(http))
            .AddEndpointFilter(Guards.RequireOwner)
            .AddEndpointFilter(Guards.RequireStepUp);

        app.MapPatch("/api/admin/users/{id}", async (HttpContext http, long id, UsersService service) => await service.Update(http, id))
            .AddEndpointFilter(Guards.RequireOwner);

        app.MapPost("/api/admin/users/{id}/reset", async (HttpContext http, long id, UsersService service) => await service.Reset(http, id))
            .AddEndpointFilter(Guards.RequireOwner)
            .AddEndpointFilter(Guards.RequireStepUp);
    }
}
