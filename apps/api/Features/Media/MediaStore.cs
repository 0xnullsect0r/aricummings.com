using Ari.Platform;
using Ari.Platform.Database;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Media;

/// <summary>
/// The port the content feature uses for media-in-use checks. Checks read the raw JSON, so
/// new block types need no change here.
/// </summary>
public interface IMediaStore
{
    Task<IReadOnlySet<string>> ExistingIds(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyCollection<string> ids);

    /// <summary>True when the id appears in any draft, published document or old revision — a rollback must never point at a deleted image.</summary>
    Task<bool> IsInUse(SqliteConnection connection, string id);
}

public sealed record MediaRow(string Id, string Original, int Width, int Height, long Bytes, string Sha256, string? Alt, long CreatedAt, long? CreatedBy);

/// <summary>Media rows and files. The id is the content address; files live in MediaDir/&lt;id&gt;/.</summary>
public sealed class MediaStore(Db db, Env env) : IMediaStore
{
    public string DirectoryFor(string id) => Path.Combine(env.MediaDir, id);

    public async Task<IReadOnlySet<string>> ExistingIds(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyCollection<string> ids) =>
        (IReadOnlySet<string>)(await connection.QueryAsync<string>(
            "SELECT id FROM media WHERE id IN @ids;",
            new { ids = ids.ToList() },
            transaction)).ToHashSet(StringComparer.Ordinal);

    public async Task<bool> IsInUse(SqliteConnection connection, string id)
    {
        // Media ids appear as whole JSON string values; the LIKE narrows the scan and the
        // quotes keep the match whole.
        var pattern = $"%\"{id}\"%";
        const string Sql = """
            SELECT COUNT(*) FROM (
                SELECT draft AS json FROM documents WHERE draft LIKE @pattern
                UNION ALL
                SELECT published FROM documents WHERE published LIKE @pattern
                UNION ALL
                SELECT content FROM document_revisions WHERE content LIKE @pattern
            );
            """;
        return await connection.ExecuteScalarAsync<long>(Sql, new { pattern }) > 0;
    }

    public async Task<MediaRow?> Find(SqliteConnection connection, string id) =>
        (await connection.QueryAsync<(string Id, string Original, int Width, int Height, long Bytes, string Sha256, string? Alt, long CreatedAt, long? CreatedBy)>(
            "SELECT id, original, width, height, bytes, sha256, alt, created_at, created_by FROM media WHERE id=@id;",
            new { id }))
        .Select(r => new MediaRow(r.Id, r.Original, r.Width, r.Height, r.Bytes, r.Sha256, r.Alt, r.CreatedAt, r.CreatedBy))
        .FirstOrDefault();

    public async Task<List<MediaRow>> List(SqliteConnection connection) =>
        (await connection.QueryAsync<(string Id, string Original, int Width, int Height, long Bytes, string Sha256, string? Alt, long CreatedAt, long? CreatedBy)>(
            "SELECT id, original, width, height, bytes, sha256, alt, created_at, created_by FROM media ORDER BY created_at DESC;"))
        .Select(r => new MediaRow(r.Id, r.Original, r.Width, r.Height, r.Bytes, r.Sha256, r.Alt, r.CreatedAt, r.CreatedBy))
        .ToList();

    public async Task Insert(SqliteConnection connection, SqliteTransaction transaction, ProcessedImage image, string? alt, long? actor)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await connection.ExecuteAsync("""
            INSERT INTO media(id, original, width, height, bytes, sha256, alt, created_at, created_by)
            VALUES(@id, @original, @width, @height, @bytes, @sha256, @alt, @now, @actor)
            ON CONFLICT(id) DO UPDATE SET original=@original, width=@width, height=@height,
                bytes=@bytes, sha256=@sha256, alt=@alt;
            """,
            new { id = image.Id, original = image.OriginalName, width = image.Width, height = image.Height, bytes = image.Bytes, sha256 = image.Sha256, alt, now, actor }, transaction);
    }

    public async Task SetAlt(string id, string alt)
    {
        await using var connection = db.App();
        await Db.Write(connection, async transaction =>
        {
            await connection.ExecuteAsync("UPDATE media SET alt=@alt WHERE id=@id;", new { id, alt }, transaction);
            return 0;
        });
    }

    /// <summary>Removes the row and the files; false when the id was unknown.</summary>
    public async Task<bool> Delete(string id)
    {
        await using var connection = db.App();
        return await Db.Write(connection, async transaction =>
        {
            var rows = await connection.ExecuteAsync("DELETE FROM media WHERE id=@id;", new { id }, transaction);
            if (rows == 0) return false;
            var directory = DirectoryFor(id);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return true;
        });
    }
}
