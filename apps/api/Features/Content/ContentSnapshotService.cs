using System.Text.Json;
using Ari.Features.GitHub;
using Ari.Features.Media;
using Ari.Platform;
using Ari.Platform.Database;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Content;

/// <summary>One media asset as the snapshot serves it.</summary>
public sealed record MediaIndexEntry(int Width, int Height);

/// <summary>
/// One immutable build of everything pages read. A change swaps in a new instance, so a
/// request never sees half of one. The version comes from the clock and is compared for
/// equality only; the ETag is the quoted version.
/// </summary>
public sealed record ContentSnapshot
{
    public required long Version { get; init; }
    public required byte[] Json { get; init; }
    public required string ETag { get; init; }
    public required IReadOnlyDictionary<string, string> Documents { get; init; }
    public required IReadOnlyDictionary<string, string> Redirects { get; init; }
    public required IReadOnlyDictionary<string, MediaIndexEntry> Media { get; init; }
    public GitHubDerived? GitHub { get; init; }

    public static ContentSnapshot Empty { get; } = new()
    {
        Version = 0,
        Json = """{"version":0,"documents":{},"redirects":[],"media":{},"github":null}"""u8.ToArray(),
        ETag = "\"0\"",
        Documents = new Dictionary<string, string>(),
        Redirects = new Dictionary<string, string>(),
        Media = new Dictionary<string, MediaIndexEntry>(),
    };
}

/// <summary>
/// Builds the site snapshot in one read transaction, so it can never mix rows from either
/// side of a write. Reload() after anything a page shows changes; only swaps when the JSON
/// actually changed, so the version and ETag stay steady across no-op rebuilds.
/// </summary>
public sealed class ContentSnapshotService(Db db, IServiceProvider services)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ContentSnapshot _current = ContentSnapshot.Empty;

    public ContentSnapshot Current => _current;

    /// <summary>Build and swap when the JSON changed. Publish passes its own open transaction.</summary>
    public async Task Reload(SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        var candidate = await Build(connection, transaction);
        await _gate.WaitAsync();
        try
        {
            if (!candidate.Json.AsSpan().SequenceEqual(_current.Json))
                _current = candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A preview's own snapshot: one document's draft in place of its published form, built
    /// per request and never swapped in. Visitors can't see a preview that is rendering at
    /// the same time.
    /// </summary>
    public async Task<ContentSnapshot> BuildPreview(string key)
    {
        await using var connection = db.App();
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await Build(connection, transaction, overrideKey: key, useDraft: true);
    }

    internal async Task<ContentSnapshot> Build(
        SqliteConnection? connection = null,
        SqliteTransaction? transaction = null,
        string? overrideKey = null,
        bool useDraft = false)
    {
        var ownConnection = connection is null;
        connection ??= db.App();
        if (ownConnection) transaction = connection.BeginTransaction(deferred: true);
        try
        {
            var documents = (await connection.QueryAsync<(string Key, string Draft, string? Published)>(
                "SELECT key, draft, published FROM documents ORDER BY key;",
                transaction: transaction))
                .ToList();
            var redirects = (await connection.QueryAsync<(string From, string To)>(
                "SELECT from_path, to_path FROM redirects ORDER BY from_path;",
                transaction: transaction))
                .ToList();
            var media = (await connection.QueryAsync<(string Id, int Width, int Height)>(
                "SELECT id, width, height FROM media ORDER BY id;",
                transaction: transaction))
                .ToList();
            // The GitHub cache is derived data: read it outside the transaction's tables, and
            // never let its absence fail the build.
            GitHubDerived? github = null;
            try
            {
                github = services.GetService<IGitHubCache>()?.Read();
            }
            catch (Exception)
            {
                github = null;
            }

            var version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", version);
                writer.WritePropertyName("documents");
                writer.WriteStartObject();
                foreach (var (key, draft, published) in documents)
                {
                    var json = key == overrideKey ? (useDraft ? draft : published) : published;
                    if (json is null) continue; // published JSON only
                    writer.WritePropertyName(key);
                    writer.WriteRawValue(json, skipInputValidation: false);
                }
                writer.WriteEndObject();
                writer.WritePropertyName("redirects");
                writer.WriteStartArray();
                foreach (var (from, to) in redirects)
                {
                    writer.WriteStartObject();
                    writer.WriteString("from", from);
                    writer.WriteString("to", to);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WritePropertyName("media");
                writer.WriteStartObject();
                foreach (var (id, width, height) in media)
                {
                    writer.WritePropertyName(id);
                    writer.WriteStartObject();
                    writer.WriteNumber("w", width);
                    writer.WriteNumber("h", height);
                    writer.WritePropertyName("widths");
                    writer.WriteStartArray();
                    foreach (var w in MediaRenditions.WidthsFor(width)) writer.WriteNumberValue(w);
                    writer.WriteEndArray();
                    writer.WriteBoolean("og", true);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WritePropertyName("github");
                if (github is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("profile");
                    writer.WriteRawValue(github.ProfileJson);
                    writer.WritePropertyName("repos");
                    writer.WriteRawValue(github.ReposJson);
                    writer.WriteNumber("fetchedAt", github.FetchedAt);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            return new ContentSnapshot
            {
                Version = version,
                Json = buffer.ToArray(),
                ETag = $"\"{version}\"",
                Documents = documents
                    .Where(d => d.Published is not null || (d.Key == overrideKey && d.Draft is not null))
                    .ToDictionary(
                        d => d.Key,
                        d => d.Key == overrideKey && useDraft ? d.Draft : d.Published!,
                        StringComparer.Ordinal),
                Redirects = redirects.ToDictionary(r => r.From, r => r.To, StringComparer.Ordinal),
                Media = media.ToDictionary(m => m.Id, m => new MediaIndexEntry(m.Width, m.Height), StringComparer.Ordinal),
                GitHub = github,
            };
        }
        finally
        {
            if (ownConnection)
            {
                await transaction!.RollbackAsync(); // read-only: nothing to commit
                await connection.DisposeAsync();
            }
        }
    }
}
