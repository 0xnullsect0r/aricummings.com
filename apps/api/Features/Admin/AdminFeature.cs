using Ari.Features.Auth;
using Ari.Platform;
using Dapper;

namespace Ari.Features.Admin;

/// <summary>
/// The dashboard summary: unread messages, the last publish, 7-day analytics and job
/// state. Anything not yet present fails soft to null rather than breaking the panel.
/// </summary>
public sealed class AdminService(ApiContext api, ILogger<AdminService> logger)
{
    public async Task<IResult> Overview(HttpContext http)
    {
        var now = SessionService.Now();
        var weekAgo = now - 7L * 24 * 3600 * 1000;

        await using var app = api.Db.App();
        var unread = await app.ExecuteScalarAsync<int?>(
            "SELECT COUNT(*) FROM messages WHERE read_at IS NULL AND archived_at IS NULL;") ?? 0;
        long? lastPublish = null;
        try
        {
            lastPublish = await app.ExecuteScalarAsync<long?>(
                """
                SELECT MAX(at) FROM (
                    SELECT MAX(published_at) AS at FROM document_revisions
                    UNION ALL
                    SELECT MAX(updated_at) AS at FROM documents WHERE published IS NOT NULL
                );
                """);
        }
        catch (Exception error) { logger.LogInformation(error, "No published content yet"); }
        var contactWeek = await app.ExecuteScalarAsync<int?>(
            "SELECT COUNT(*) FROM messages WHERE created_at >= @week;", new { week = weekAgo }) ?? 0;
        var jobs = await app.QueryAsync<(string Name, long? LastRunAt, long? LastOkAt, string? LastError)>(
            "SELECT name AS Name, last_run_at AS LastRunAt, last_ok_at AS LastOkAt, last_error AS LastError FROM jobs_state ORDER BY name;");

        long? visitsWeek = null;
        try
        {
            await using var analytics = api.Db.Analytics();
            visitsWeek = await analytics.ExecuteScalarAsync<long?>(
                "SELECT COUNT(*) FROM visits WHERE at >= @week;", new { week = weekAgo });
        }
        catch (Exception error) { logger.LogInformation(error, "Analytics not available for the overview"); }

        return Results.Json(new
        {
            messagesUnread = unread,
            messagesUnreadWeek = contactWeek,
            lastPublishAt = lastPublish,
            analytics = new { visits7d = visitsWeek },
            jobs = jobs.Select(j => new
            {
                name = j.Name,
                lastRunAt = j.LastRunAt,
                lastOkAt = j.LastOkAt,
                ok = j.LastOkAt is not null && (j.LastRunAt is null || j.LastOkAt >= j.LastRunAt),
                lastError = j.LastError,
            }),
            generatedAt = now,
        });
    }
}

public sealed class AdminFeature : IFeature
{
    public int Order => 100;

    public void Register(IServiceCollection services) => services.AddSingleton<AdminService>();

    public void Map(WebApplication app) =>
        app.MapGet("/api/admin/overview", async (HttpContext http, AdminService service) => await service.Overview(http))
            .AddEndpointFilter(Guards.RequireStaff);
}
