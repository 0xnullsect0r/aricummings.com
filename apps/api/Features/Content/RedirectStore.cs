using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Ari.Platform.Edge;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Content;

public sealed record RedirectRow(long Id, string From, string To);

/// <summary>
/// Redirect CRUD with target validation. The from-path belongs to this table alone: it can
/// never shadow staff routes, the work index or the home page, and a local target may not
/// itself be a redirect, so chains are impossible.
/// </summary>
public sealed class RedirectStore(Db db, ContentSnapshotService snapshots)
{
    private static readonly string[] ForbiddenFromPrefixes =
        { "/admin", "/api", "/media", "/internal", "/healthz", "/_next", "/work" };

    public async Task<List<RedirectRow>> List(SqliteConnection connection) =>
        (await connection.QueryAsync<(long Id, string From, string To)>(
            "SELECT id, from_path, to_path FROM redirects ORDER BY from_path;"))
        .Select(r => new RedirectRow(r.Id, r.From, r.To))
        .ToList();

    public async Task<RedirectRow> Create(string from, string to, long? actor, string? ip)
    {
        ValidateFrom(from);
        ValidateTo(to, from);
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var duplicate = await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT id FROM redirects WHERE from_path=@from;", new { from }, transaction);
            if (duplicate is not null)
                throw new ApiException(400, "A redirect already exists for that path.", "duplicate");

            // A local target must be a real destination, never the start of another hop.
            if (to.StartsWith("/", StringComparison.Ordinal))
            {
                var chained = await connection.QuerySingleOrDefaultAsync<long?>(
                    "SELECT id FROM redirects WHERE from_path=@to;", new { to }, transaction);
                if (chained is not null)
                    throw new ApiException(400, "The target is itself a redirect; point at the final destination.", "chain");
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO redirects(from_path, to_path, created_at, created_by)
                VALUES(@from, @to, @now, @actor) RETURNING id;
                """,
                new { from, to, now, actor }, transaction);
            AuditLog.Record(connection, transaction, actor, "redirect.created", from, to, ip);
            await snapshots.Reload(connection, transaction);
            return new RedirectRow(id, from, to);
        });
    }

    public async Task<bool> Delete(long id, long? actor, string? ip)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var from = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT from_path FROM redirects WHERE id=@id;", new { id }, transaction);
            if (from is null) return false;
            await connection.ExecuteAsync("DELETE FROM redirects WHERE id=@id;", new { id }, transaction);
            AuditLog.Record(connection, transaction, actor, "redirect.deleted", from, "", ip);
            await snapshots.Reload(connection, transaction);
            return true;
        });
    }

    private static void ValidateFrom(string from)
    {
        if (from.Length is < 2 or > 500)
            throw new ApiException(400, "'from' must be a path of 2 to 500 characters.", "invalid_from");
        if (from[0] != '/' || from != Paths.Canonical(from))
            throw new ApiException(400, "'from' must be a canonical path: lowercase, no query, no trailing slash.", "invalid_from");
        if (from.Contains('?') || from.Contains('#'))
            throw new ApiException(400, "'from' must not have a query or fragment.", "invalid_from");
        if (ForbiddenFromPrefixes.Any(prefix => from == prefix || from.StartsWith(prefix + "/", StringComparison.Ordinal)))
            throw new ApiException(400, "'from' is reserved for staff routes or page documents.", "invalid_from");
    }

    private static void ValidateTo(string to, string from)
    {
        if (to.Length is < 1 or > 1000)
            throw new ApiException(400, "'to' must be 1 to 1000 characters.", "invalid_to");
        if (to.StartsWith("//", StringComparison.Ordinal) || to.StartsWith('\\'))
            throw new ApiException(400, "'to' must not be protocol-relative.", "invalid_to");
        if (to.StartsWith("/", StringComparison.Ordinal))
        {
            if (to != Paths.Canonical(to) || to.Contains('?') || to.Contains('#'))
                throw new ApiException(400, "'to' must be a canonical path when it is local.", "invalid_to");
            if (to == from)
                throw new ApiException(400, "'to' must differ from 'from'.", "invalid_to");
            return;
        }
        if (!to.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !to.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "'to' must be a local path or an http(s) address.", "invalid_to");
        if (!Uri.TryCreate(to, UriKind.Absolute, out _))
            throw new ApiException(400, "'to' is not a valid address.", "invalid_to");
    }
}

/// <summary>Answers the edge from the current snapshot: one lookup, no database on the hot path.</summary>
public sealed class SnapshotRedirectSource(ContentSnapshotService snapshots) : IRedirectSource
{
    public string? TargetFor(string canonicalPath) =>
        snapshots.Current.Redirects.TryGetValue(canonicalPath, out var to) ? to : null;
}
