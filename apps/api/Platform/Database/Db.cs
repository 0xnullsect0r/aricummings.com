using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Platform.Database;

/// <summary>
/// Two SQLite databases: app.db for everything the site owns, analytics.db for the
/// constant event writes, so they never contend. WAL mode on both, and every connection
/// gets the same pragmas.
/// </summary>
public sealed class Db
{
    public string AppPath { get; }
    public string AnalyticsPath { get; }

    public Db(string dataDir)
    {
        AppPath = Path.Combine(dataDir, "app.db");
        AnalyticsPath = Path.Combine(dataDir, "analytics.db");
        Migrate(AppPath, "Ari.Migrations.App");
        Migrate(AnalyticsPath, "Ari.Migrations.Analytics");
    }

    public SqliteConnection App() => Open(AppPath);

    public SqliteConnection Analytics() => Open(AnalyticsPath);

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();
        foreach (var pragma in new[]
                 {
                     "PRAGMA journal_mode=WAL;",
                     "PRAGMA busy_timeout=2000;",
                     "PRAGMA synchronous=NORMAL;",
                     "PRAGMA foreign_keys=ON;",
                     "PRAGMA journal_size_limit=67108864;",
                 })
            connection.Execute(pragma);
        return connection;
    }

    /// <summary>
    /// Takes the write lock up front with BEGIN IMMEDIATE, so a read-then-write sequence
    /// cannot interleave with another writer. Throws rolls back; success commits.
    /// </summary>
    public static async Task<T> Write<T>(SqliteConnection connection, Func<SqliteTransaction, Task<T>> work)
    {
        // The async overload has no deferred flag; the sync BeginTransaction is an
        // in-process call, so this is not a blocking I/O wait.
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var result = await work(transaction);
        await transaction.CommitAsync();
        return result;
    }

    /// <summary>Sync twin for callers inside sync CLI paths.</summary>
    public static T Write<T>(SqliteConnection connection, Func<SqliteTransaction, T> work)
    {
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var result = work(transaction);
        transaction.Commit();
        return result;
    }

    private static void Migrate(string path, string resourcePrefix)
    {
        using var connection = Open(path);
        connection.Execute("""
            CREATE TABLE IF NOT EXISTS migrations(
                name TEXT PRIMARY KEY,
                applied_at INTEGER NOT NULL
            );
            """);
        var applied = connection.Query<string>("SELECT name FROM migrations;").ToHashSet(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(resourcePrefix + ".", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var name = resource[(resourcePrefix.Length + 1)..];
            if (applied.Contains(name)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Migration {name} is missing its stream.");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
            connection.Execute(sql, transaction: transaction);
            connection.Execute("INSERT INTO migrations(name, applied_at) VALUES (@name, @now);",
                new { name, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }, transaction);
            transaction.Commit();
        }
    }
}
