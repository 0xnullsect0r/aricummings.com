using System.Text.Json;
using Ari.Features.Content;
using Ari.Platform;
using Ari.Platform.Crypto;
using Ari.Platform.Database;
using Ari.Platform.Jobs;
using Ari.Platform.Tokens;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ari.Features.GitHub;

/// <summary>
/// The port the content snapshot reads derived GitHub data through. Null when never
/// fetched; the snapshot then omits the strip and never fails.
/// </summary>
public interface IGitHubCache
{
    GitHubDerived? Read();
}

/// <summary>Raw JSON exactly as fetched, with the fetch time.</summary>
public sealed record GitHubDerived(string ProfileJson, string ReposJson, long FetchedAt);

/// <summary>Reads the two github_cache rows written by the sync job.</summary>
public sealed class GitHubCache(Db db) : IGitHubCache
{
    public GitHubDerived? Read()
    {
        using var connection = db.App();
        var rows = connection.Query<(string Id, long FetchedAt, string Data)>(
            "SELECT id, fetched_at, data FROM github_cache WHERE id IN ('profile','repos');").ToDictionary(r => r.Id);
        if (!rows.TryGetValue("profile", out var profile) || !rows.TryGetValue("repos", out var repos))
            return null;
        // Both rows are written in one transaction; report the older of the two so a
        // half-updated pair never looks fresher than it is.
        return new GitHubDerived(profile.Data, repos.Data, Math.Min(profile.FetchedAt, repos.FetchedAt));
    }
}

/// <summary>
/// Every 30 minutes: fetch the profile and repo list server-side, with the optional token
/// from the encrypted github_token setting (GITHUB_TOKEN env as a first-boot fallback),
/// store both rows, and reload the snapshot. Fails soft: on error the job is recorded as
/// failed and the snapshot keeps the last good build.
/// </summary>
public sealed class GitHubSyncJob(Db db, ContentSnapshotService snapshots, Sealed sealedValues, ILogger<GitHubSyncJob> logger)
{
    internal const string ProfileUrl = "https://api.github.com/users/0xnullsect0r";
    internal const string ReposUrl = "https://api.github.com/users/0xnullsect0r/repos?per_page=100&sort=updated";

    public async Task Run(CancellationToken cancellation)
    {
        // Hermetic tests: never reach the real API from a test process.
        if (Environment.GetEnvironmentVariable("ARI_TEST_DISABLE_GITHUB_SYNC") == "1") return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("aricummings.com github-sync");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var token = ReadToken();
        if (token is { Length: > 0 })
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var profile = await http.GetStringAsync(ProfileUrl, cancellation);
        var repos = await http.GetStringAsync(ReposUrl, cancellation);
        ValidateJson(profile, "profile");
        ValidateJson(repos, "repos");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = db.App();
        await Db.Write(connection, async transaction =>
        {
            foreach (var (id, data) in new[] { ("profile", profile), ("repos", repos) })
                await connection.ExecuteAsync("""
                    INSERT INTO github_cache(id, fetched_at, data) VALUES(@id, @now, @data)
                    ON CONFLICT(id) DO UPDATE SET fetched_at=@now, data=@data;
                    """,
                    new { id, now, data }, transaction);
            return 0;
        });

        await snapshots.Reload();
        logger.LogInformation("GitHub sync complete at {At}", now);
    }

    /// <summary>Settings win; the env var only helps a fresh server connect first.</summary>
    private string? ReadToken()
    {
        using var connection = db.App();
        var row = connection.QuerySingleOrDefault<(string Value, int Encrypted)?>(
            "SELECT value, encrypted FROM settings WHERE key='github_token';");
        if (row is not null)
        {
            var (value, encrypted) = row.Value;
            return encrypted == 1 ? sealedValues.DecryptString(value, "setting:github_token") : value;
        }
        return Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }

    private static void ValidateJson(string body, string what)
    {
        try
        {
            using var _ = JsonDocument.Parse(body);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"GitHub {what} response was not JSON: {error.Message}");
        }
    }
}

/// <summary>Order 40: after content, whose snapshot builder reads the cache lazily.</summary>
public sealed class GitHubFeature : IFeature
{
    public int Order => 40;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IGitHubCache, GitHubCache>();
        services.AddSingleton<GitHubSyncJob>();
        services.AddSingleton(sp => new JobOptions(
            Name: "github-sync",
            Cron: "*/30 * * * *",
            TimeZone: null,
            Work: cancellation => sp.GetRequiredService<GitHubSyncJob>().Run(cancellation),
            Alert: false)); // fails soft: the snapshot keeps the last good build
    }

    public void Map(WebApplication app)
    {
        // No HTTP surface of its own: the snapshot (content) and health carry the data.
    }
}
