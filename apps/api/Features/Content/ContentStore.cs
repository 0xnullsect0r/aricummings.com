using System.Security.Claims;
using System.Text.Json;
using Ari.Features.Media;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Ari.Platform.Tokens;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Content;

public sealed record DocumentRow(string Key, string Draft, string? Published, long Version, long UpdatedAt, long? UpdatedBy);

public sealed record RevisionRow(long Version, string Content, string? Note, long? Author, long PublishedAt);

/// <summary>
/// The documents store. Saving a draft validates and writes only the draft; publishing
/// writes draft and published together, adds a revision, and builds the whole site snapshot
/// inside the same transaction — a document pointing at missing media is refused here, so
/// one bad save can't take every page down at the next restart. Rollback is a new version
/// with old content, so history is never rewritten.
/// </summary>
public sealed class ContentStore(Db db, ContentSnapshotService snapshots, IServiceProvider services)
{
    /// <summary>Validates the key, then the document; returns normalized JSON or throws the field problems.</summary>
    public static string ValidateAndNormalize(string key, JsonElement document)
    {
        if (!DocumentSchema.IsValidKey(key, out var keyProblem))
            throw new ApiException(400, keyProblem!, "bad_key");
        var normalized = DocumentSchema.Normalize(key, document, out var problems);
        if (problems.Count > 0)
            throw new ApiException(400, string.Join("; ", problems), "schema");
        return normalized;
    }

    public async Task<DocumentRow?> Get(SqliteConnection connection, string key)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(string Key, string Draft, string? Published, long Version, long UpdatedAt, long? UpdatedBy)>(
            "SELECT key, draft, published, version, updated_at, updated_by FROM documents WHERE key=@key;",
            new { key });
        return row.Key is null ? null : new DocumentRow(row.Key, row.Draft, row.Published, row.Version, row.UpdatedAt, row.UpdatedBy);
    }

    public sealed record DocumentListItem(string Key, long Version, long UpdatedAt, bool HasDraft, bool HasPublished, bool DraftDiffers);

    public async Task<List<DocumentListItem>> List(SqliteConnection connection)
    {
        var rows = await connection.QueryAsync<(string Key, string Draft, string? Published, long Version, long UpdatedAt)>(
            "SELECT key, draft, published, version, updated_at FROM documents ORDER BY key;");
        return rows.Select(r => new DocumentListItem(
                r.Key, r.Version, r.UpdatedAt,
                r.Draft is not null && r.Draft != "{}",
                r.Published is not null,
                r.Published is not null && r.Draft != r.Published))
            .ToList();
    }

    public async Task<List<RevisionRow>> Revisions(SqliteConnection connection, string key) =>
        (await connection.QueryAsync<(long Version, string Content, string? Note, long? Author, long PublishedAt)>(
            """
            SELECT version, content, note, author, published_at FROM document_revisions
            WHERE key=@key ORDER BY version DESC;
            """,
            new { key }))
        .Select(r => new RevisionRow(r.Version, r.Content, r.Note, r.Author, r.PublishedAt))
        .ToList();

    /// <summary>Save a draft: validate, normalize, write only the draft column.</summary>
    public async Task<long> SaveDraft(string key, string contentJson, long? actor, string? ip)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await connection.ExecuteAsync("""
                INSERT INTO documents(key, draft, published, version, updated_at, updated_by)
                VALUES(@key, @draft, NULL, 0, @now, @actor)
                ON CONFLICT(key) DO UPDATE SET draft=@draft, updated_at=@now, updated_by=@actor;
                """,
                new { key, draft = contentJson, now, actor }, transaction);
            AuditLog.Record(connection, transaction, actor, "content.draft_saved", key, "", ip);
            var version = await connection.ExecuteScalarAsync<long>("SELECT version FROM documents WHERE key=@key;", new { key }, transaction);
            return version;
        });
    }

    /// <summary>
    /// Publish: write draft and published together, add a revision, build the snapshot inside
    /// the transaction, and swap it in only after the commit.
    /// </summary>
    public async Task<long> Publish(string key, string? note, long? actor, string? ip)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var document = await Get(connection, key) ?? throw new ApiException(404, "No such document.", "not_found");
            if (document.Draft is not { Length: > 2 })
                throw new ApiException(400, "The draft is empty.", "empty_draft");

            using var parsed = JsonDocument.Parse(document.Draft);
            var normalized = DocumentSchema.Normalize(key, parsed.RootElement, out var problems);
            if (problems.Count > 0)
                throw new ApiException(400, string.Join("; ", problems), "schema");

            await RefuseMissingMedia(connection, transaction, normalized);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var version = document.Version + 1;
            await connection.ExecuteAsync(
                "UPDATE documents SET draft=@draft, published=@draft, version=@version, updated_at=@now, updated_by=@actor WHERE key=@key;",
                new { draft = normalized, version, now, actor, key }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO document_revisions(key, version, content, note, author, published_at)
                VALUES(@key, @version, @content, @note, @author, @now);
                """,
                new { key, version, content = normalized, note, author = actor, now }, transaction);
            AuditLog.Record(connection, transaction, actor, "content.published", key, note ?? "", ip);

            await snapshots.Reload(connection, transaction);
            return version;
        });
    }

    /// <summary>Unpublish clears the published column; the draft stays for the next round.</summary>
    public async Task<long> Unpublish(string key, long? actor, string? ip)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var document = await Get(connection, key) ?? throw new ApiException(404, "No such document.", "not_found");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var version = document.Version + 1;
            await connection.ExecuteAsync(
                "UPDATE documents SET published=NULL, version=@version, updated_at=@now, updated_by=@actor WHERE key=@key;",
                new { version, now, actor, key }, transaction);
            AuditLog.Record(connection, transaction, actor, "content.unpublished", key, "", ip);
            await snapshots.Reload(connection, transaction);
            return version;
        });
    }

    /// <summary>Rollback = a new version with old content. The old revision row stays untouched.</summary>
    public async Task<long> Rollback(string key, long toVersion, string? note, long? actor, string? ip)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var document = await Get(connection, key) ?? throw new ApiException(404, "No such document.", "not_found");
            var revision = (await connection.QuerySingleOrDefaultAsync<(long Version, string Content)?>(
                "SELECT version, content FROM document_revisions WHERE key=@key AND version=@version;",
                new { key, version = toVersion }, transaction));
            if (revision is null)
                throw new ApiException(404, "No such revision.", "not_found");

            using var parsed = JsonDocument.Parse(revision.Value.Content);
            var normalized = DocumentSchema.Normalize(key, parsed.RootElement, out var problems);
            if (problems.Count > 0)
                throw new ApiException(400, string.Join("; ", problems), "schema");

            await RefuseMissingMedia(connection, transaction, normalized);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var version = document.Version + 1;
            await connection.ExecuteAsync(
                "UPDATE documents SET draft=@draft, published=@draft, version=@version, updated_at=@now, updated_by=@actor WHERE key=@key;",
                new { draft = normalized, version, now, actor, key }, transaction);
            await connection.ExecuteAsync("""
                INSERT INTO document_revisions(key, version, content, note, author, published_at)
                VALUES(@key, @version, @content, @note, @author, @now);
                """,
                new { key, version, content = normalized, note = note ?? $"Rollback to v{toVersion}", author = actor, now }, transaction);
            AuditLog.Record(connection, transaction, actor, "content.published", key, $"rollback to v{toVersion}", ip);

            await snapshots.Reload(connection, transaction);
            return version;
        });
    }

    /// <summary>A 1-hour preview link for one document: public address plus a preview-audience JWT.</summary>
    public async Task<(string Url, string Token)> PreviewLink(string key, Env env, TokenIssuer tokens)
    {
        await using var connection = db.App();
        if (await Get(connection, key) is null)
            throw new ApiException(404, "No such document.", "not_found");
        var token = PreviewTokens.Issue(env, key);
        var path = key switch
        {
            "site" or "page:home" => "/",
            string k when k.StartsWith("work:", StringComparison.Ordinal) => "/work/" + k["work:".Length..],
            _ => "/",
        };
        return ($"{env.PublicOrigin}{path}?preview={Uri.EscapeDataString(token)}", token);
    }

    /// <summary>
    /// Refuse a publish whose blocks reference media that is not in the library. The check
    /// reads the raw JSON, so new block types need no change here: any 16-hex string value
    /// in a document is, by convention, a media id.
    /// </summary>
    private async Task RefuseMissingMedia(SqliteConnection connection, SqliteTransaction transaction, string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var ids = new HashSet<string>();
        CollectMediaIds(parsed.RootElement, ids);
        if (ids.Count == 0) return;
        var store = services.GetService<IMediaStore>();
        if (store is null) return;
        var existing = await store.ExistingIds(connection, transaction, ids);
        var missing = ids.Where(id => !existing.Contains(id)).ToList();
        if (missing.Count > 0)
            throw new ApiException(400, "Media not found: " + string.Join(", ", missing.Order()), "missing_media");
    }

    private static void CollectMediaIds(JsonElement element, HashSet<string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var child in element.EnumerateObject()) CollectMediaIds(child.Value, ids);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray()) CollectMediaIds(child, ids);
                break;
            case JsonValueKind.String when element.GetString() is { } text && MediaIds.Pattern().IsMatch(text):
                ids.Add(text);
                break;
        }
    }
}

/// <summary>
/// Mints the preview token. TokenIssuer.Issue drops its claims argument (a platform gap),
/// and the claim is the whole point of a preview token, so the descriptor is built here
/// with the same issuer, HKDF info and signing parameters TokenIssuer.Read validates.
/// </summary>
public static class PreviewTokens
{
    public static string Issue(Env env, string key)
    {
        var entry = env.Keys.Current;
        var now = DateTimeOffset.UtcNow;
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = "ari",
            Audience = "preview",
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddHours(1).UtcDateTime,
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                    env.Keys.Derive(entry, "site:jwt")) { KeyId = entry.Kid },
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["key"] = key },
        };
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);
    }
}
