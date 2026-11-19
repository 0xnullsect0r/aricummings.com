using System.Reflection;
using System.Text.Json;
using Ari.Features.GitHub;
using Ari.Platform;
using Ari.Platform.Database;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Content;

/// <summary>
/// First-boot content from the JSON embedded in the image. Seeds only an empty documents
/// table; a bad seed throws and fails the boot, so the release health check catches it.
/// </summary>
public static class Seeder
{
    public static void Seed(Db db)
    {
        using var connection = db.App();
        Seed(connection, snapshots: null);
    }

    public static void Seed(SqliteConnection connection, ContentSnapshotService? snapshots)
    {
        var existing = connection.ExecuteScalar<long>("SELECT COUNT(*) FROM documents;");
        if (existing > 0) return;

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Ari.Seed.seed-content.json")
            ?? throw new InvalidOperationException("seed-content.json is missing from the image.");
        using var reader = new StreamReader(stream);
        using var parsed = JsonDocument.Parse(reader.ReadToEnd());
        if (!parsed.RootElement.TryGetProperty("documents", out var documents)
            || documents.ValueKind != JsonValueKind.Object
            || documents.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Object))
            throw new InvalidOperationException("seed-content.json has no documents object.");

        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var property in documents.EnumerateObject())
        {
            var key = property.Name;
            var normalized = ContentStore.ValidateAndNormalize(key, property.Value);
            connection.Execute("""
                INSERT INTO documents(key, draft, published, version, updated_at, updated_by)
                VALUES(@key, @draft, @draft, 1, @now, NULL);
                """,
                new { key, draft = normalized, now }, transaction);
            connection.Execute("""
                INSERT INTO document_revisions(key, version, content, note, author, published_at)
                VALUES(@key, 1, @content, 'seed', NULL, @now);
                """,
                new { key, content = normalized, now }, transaction);
        }
        transaction.Commit();
        snapshots?.Reload(connection, null).GetAwaiter().GetResult();
    }
}

/// <summary>
/// Seeds the first boot and builds the first snapshot synchronously: /api/health then
/// reports a real contentVersion, which proves content really loaded.
/// </summary>
public sealed class ContentBoot(Db db, ContentSnapshotService snapshots) : IHostedService
{
    public Task StartAsync(CancellationToken cancellation)
    {
        Seeder.Seed(db);
        snapshots.Reload().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellation) => Task.CompletedTask;
}

/// <summary>Health from the content feature: the snapshot version and the last GitHub sync.</summary>
public sealed class ContentHealthReporter(ContentSnapshotService snapshots, IServiceProvider services) : IHealthReporter
{
    public object Report()
    {
        long? githubSyncedAt = null;
        try
        {
            githubSyncedAt = services.GetService<IGitHubCache>()?.Read()?.FetchedAt;
        }
        catch (Exception)
        {
            githubSyncedAt = null;
        }
        return new { status = "ok", contentVersion = snapshots.Current.Version, githubSyncedAt };
    }
}
