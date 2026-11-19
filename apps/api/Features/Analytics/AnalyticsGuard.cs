using Ari.Platform;
using Ari.Platform.Database;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Analytics;

/// <summary>
/// Permission gate for the admin analytics routes. The real check (RequireStaff /
/// RequirePermission("analytics.read" | "analytics.export")) belongs to Auth; until it
/// registers one, <see cref="OpenAnalyticsGuard"/> stands in so routes and the SPA can
/// be built and tested. INTEGRATOR: register your implementation with
/// AddSingleton&lt;IAnalyticsGuard, ...&gt; BEFORE the Analytics feature (Order &lt; 30)
/// or replace the default; the no-op default is registered with TryAdd and loses to any
/// earlier or explicit registration.
/// </summary>
public interface IAnalyticsGuard
{
    /// <summary>The status to answer with, or null when the caller may proceed.</summary>
    Task<int?> Deny(HttpContext context, string permission);
}

/// <summary>Allows everything. Only acceptable because the admin surface refuses public traffic.</summary>
public sealed class OpenAnalyticsGuard : IAnalyticsGuard
{
    public static readonly OpenAnalyticsGuard Instance = new();

    public Task<int?> Deny(HttpContext context, string permission) => Task.FromResult<int?>(null);
}

/// <summary>Read connections for reports: read-only mode plus query_only, never the writer's.</summary>
public sealed class AnalyticsQueries
{
    private readonly Db _db;

    public AnalyticsQueries(Db db) => _db = db;

    public async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={_db.AnalyticsPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA query_only=1;");
        return connection;
    }
}
