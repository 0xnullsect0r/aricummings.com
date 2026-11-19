using Ari.Features.Auth;
using Ari.Platform;
using Ari.Platform.Api;
using Dapper;

namespace Ari.Features.Audit;

/// <summary>The audit log read endpoint: newest first, cursor paging on the row id so
/// a page boundary never skips or repeats an entry.</summary>
public sealed class AuditService(ApiContext api)
{
    public async Task<IResult> List(HttpContext http)
    {
        var query = http.Request.Query;
        var beforeText = query["before"].ToString();
        long? before = null;
        if (beforeText.Length > 0 && (!long.TryParse(beforeText, out var parsed) || parsed <= 0))
            throw new ApiException(400, "before must be an audit row id.", "invalid_fields");
        else if (beforeText.Length > 0)
            before = long.Parse(beforeText);
        var limitText = query["limit"].ToString();
        var limit = 50;
        if (limitText.Length > 0)
        {
            if (!int.TryParse(limitText, out var parsedLimit) || parsedLimit is < 1 or > 200)
                throw new ApiException(400, "limit must be between 1 and 200.", "invalid_fields");
            limit = parsedLimit;
        }

        await using var connection = api.Db.App();
        var rows = (await connection.QueryAsync<(long Id, long At, long? Actor, string Action, string Target, string Detail, string? Ip)>(
            """
            SELECT id AS Id, at AS At, actor AS Actor, action AS Action, target AS Target, detail AS Detail, ip AS Ip
            FROM audit_log WHERE (@before IS NULL OR id < @before)
            ORDER BY id DESC LIMIT @limit;
            """,
            new { before, limit = limit + 1 })).ToList();

        long? next = null;
        if (rows.Count > limit)
        {
            next = rows[^2].Id;
            rows.RemoveAt(rows.Count - 1);
        }
        var users = (await connection.QueryAsync<(long Id, string Email)>("SELECT id AS Id, email AS Email FROM users;"))
            .ToDictionary(u => u.Id, u => u.Email);
        return Results.Json(new
        {
            items = rows.Select(r => new
            {
                id = r.Id,
                at = r.At,
                actor = r.Actor,
                actorEmail = r.Actor is long actor && users.TryGetValue(actor, out var email) ? email : null,
                action = r.Action,
                target = r.Target,
                detail = r.Detail,
                ip = r.Ip,
            }),
            next,
        });
    }
}

public sealed class AuditFeature : IFeature
{
    public int Order => 90;

    public void Register(IServiceCollection services) => services.AddSingleton<AuditService>();

    public void Map(WebApplication app) =>
        app.MapGet("/api/admin/audit", async (HttpContext http, AuditService service) => await service.List(http))
            .AddEndpointFilter(Guards.RequirePermission("audit.read"));
}
