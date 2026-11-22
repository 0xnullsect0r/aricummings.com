using Ari.Features.Backups;
using Ari.Platform.Database;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AriApi.Tests;

public class BackupTests
{
    [Fact]
    public async Task Writes_private_checked_copies_and_keeps_only_its_own_14_days()
    {
        var root = Directory.CreateTempSubdirectory("ari-backup-test").FullName;
        try
        {
            var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
            Directory.CreateDirectory(Path.Combine(data, "media"));
            await File.WriteAllTextAsync(Path.Combine(data, "media", "a.txt"), "media");
            var backups = Directory.CreateDirectory(Path.Combine(root, "backups")).FullName;
            var foreign = Directory.CreateDirectory(Path.Combine(backups, "db")).FullName;
            await File.WriteAllTextAsync(Path.Combine(foreign, "app-2020-01-01.db"), "not ours");
            var nightly = Directory.CreateDirectory(Path.Combine(backups, "nightly")).FullName;
            await File.WriteAllTextAsync(Path.Combine(nightly, "notes.txt"), "not ours");

            var job = new NightlyBackupJob(new Db(data), backups, NullLogger<NightlyBackupJob>.Instance);
            var start = new DateOnly(2026, 11, 1);
            for (var day = 0; day < 16; day++) await job.RunFor(start.AddDays(day), CancellationToken.None);

            Assert.Equal(14, Directory.GetFiles(nightly, "app-*.db").Length);
            Assert.Equal(14, Directory.GetFiles(nightly, "analytics-*.db").Length);
            Assert.Equal(14, Directory.GetFiles(nightly, "media-*.tar").Length);
            Assert.False(File.Exists(Path.Combine(nightly, "app-2026-11-02.db")));
            Assert.True(File.Exists(Path.Combine(nightly, "app-2026-11-03.db")));
            Assert.True(File.Exists(Path.Combine(nightly, "notes.txt")));
            Assert.True(File.Exists(Path.Combine(foreign, "app-2020-01-01.db")));
            Assert.Empty(Directory.GetFiles(nightly, "*.tmp"));

            var newest = Path.Combine(nightly, "app-2026-11-16.db");
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(newest));
            await using var copy = new SqliteConnection($"Data Source={newest};Mode=ReadOnly;Pooling=False");
            await copy.OpenAsync();
            Assert.Equal("ok", await copy.ExecuteScalarAsync<string>("PRAGMA integrity_check;"));
            Assert.True(await copy.ExecuteScalarAsync<int>("select count(*) from sqlite_master where name='users';") == 1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
