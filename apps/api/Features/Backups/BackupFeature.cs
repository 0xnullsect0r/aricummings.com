using System.Formats.Tar;
using System.Globalization;
using System.Text.RegularExpressions;
using Ari.Platform;
using Ari.Platform.Database;
using Ari.Platform.Jobs;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ari.Features.Backups;

/// <summary>
/// Nightly copy of both databases and the media folder into BACKUP_DIR/nightly, keeping
/// the latest 14 days. It only ever prunes its own folder: the backups volume can hold
/// other files that must survive. The app key is deliberately not included.
/// </summary>
public sealed partial class NightlyBackupJob(Db db, string? backupDir, ILogger<NightlyBackupJob> logger)
{
    public const int KeepDays = 14;

    [GeneratedRegex(@"^(app|analytics|media)-(\d{4}-\d{2}-\d{2})\.(db|tar)$")]
    private static partial Regex OwnFile();

    public Task Run(CancellationToken cancellation) => RunFor(DateOnly.FromDateTime(DateTime.UtcNow), cancellation);

    public async Task RunFor(DateOnly day, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(backupDir)) return;
        var folder = Path.Combine(backupDir, "nightly");
        Directory.CreateDirectory(folder);
        var stamp = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await CopyDatabase(db.App, Path.Combine(folder, $"app-{stamp}.db"), cancellation);
        await CopyDatabase(db.Analytics, Path.Combine(folder, $"analytics-{stamp}.db"), cancellation);
        await ArchiveMedia(Path.Combine(Path.GetDirectoryName(db.AppPath)!, "media"), Path.Combine(folder, $"media-{stamp}.tar"), cancellation);
        Prune(folder);
        logger.LogInformation("Nightly backup {Stamp} complete", stamp);
    }

    private static async Task CopyDatabase(Func<SqliteConnection> open, string target, CancellationToken cancellation)
    {
        var temp = target + ".tmp";
        File.Delete(temp);
        try
        {
            await using (var connection = open())
                await connection.ExecuteAsync(new CommandDefinition("VACUUM INTO @temp;", new { temp }, commandTimeout: 300, cancellationToken: cancellation));

            var check = new SqliteConnectionStringBuilder { DataSource = temp, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            await using (var copy = new SqliteConnection(check))
            {
                await copy.OpenAsync(cancellation);
                var result = await copy.ExecuteScalarAsync<string>("PRAGMA integrity_check;");
                if (result != "ok") throw new InvalidOperationException($"Backup failed its integrity check: {result}");
            }
            Finish(temp, target);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static async Task ArchiveMedia(string source, string target, CancellationToken cancellation)
    {
        Directory.CreateDirectory(source);
        var temp = target + ".tmp";
        try
        {
            await TarFile.CreateFromDirectoryAsync(source, temp, includeBaseDirectory: false, cancellation);
            Finish(temp, target);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void Finish(string temp, string target)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, target, overwrite: true);
    }

    private static void Prune(string folder)
    {
        var own = Directory.GetFiles(folder)
            .Select(path => (path, match: OwnFile().Match(Path.GetFileName(path))))
            .Where(item => item.match.Success)
            .ToList();
        var keep = own.Select(item => item.match.Groups[2].Value).Distinct()
            .OrderDescending(StringComparer.Ordinal).Take(KeepDays).ToHashSet();
        foreach (var (path, match) in own)
            if (!keep.Contains(match.Groups[2].Value)) File.Delete(path);
    }
}

public sealed class BackupFeature : IFeature
{
    public int Order => 95;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(sp => new NightlyBackupJob(
            sp.GetRequiredService<Db>(),
            Environment.GetEnvironmentVariable("ARI_BACKUP_DIR"),
            sp.GetRequiredService<ILogger<NightlyBackupJob>>()));
        services.AddSingleton(sp => new JobOptions(
            Name: "nightly-backup",
            Cron: "45 2 * * *",
            TimeZone: null,
            Work: cancellation => sp.GetRequiredService<NightlyBackupJob>().Run(cancellation),
            Alert: true));
    }

    public void Map(WebApplication app)
    {
    }
}
