using System.Net;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ari.Features.Analytics;
using Xunit;

namespace AriApi.Tests.Analytics;

/// <summary>Retention deletes and the weekly owner email's send-once guarantee.</summary>
public sealed class JobsTests : AnalyticsHarness
{
    private SqliteConnection AnalyticsWriter()
    {
        var connection = new SqliteConnection(
            $"Data Source={System.IO.Path.Combine(DataDir, "analytics.db")};Mode=ReadWriteCreate");
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task Retention_prunes_old_events_nulls_old_ips_and_never_touches_audit()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var day = 86_400_000L;
        await using (var db = AnalyticsWriter())
        {
            db.Execute("INSERT INTO visits(visitor, at, path, ip, session_id) VALUES('vOld', @at, '/', '1.1.1.1', 'j1'), ('vMid', @at, '/', '2.2.2.2', 'j2'), ('vNew', @at, '/', '3.3.3.3', 'j3');",
                new { at = now - 500 * day });
            db.Execute("UPDATE visits SET at = @at WHERE visitor = 'vMid';", new { at = now - 31 * day });
            db.Execute("UPDATE visits SET at = @at WHERE visitor = 'vNew';", new { at = now - 1 * day });
            db.Execute("INSERT INTO click_events(visitor, at, path, label) VALUES('vOld', @at, '/', 'old');", new { at = now - 500 * day });
            db.Execute("INSERT INTO bot_hits(at, ip, path) VALUES(@at, '9.9.9.9', '/');", new { at = now - 500 * day });
        }

        // The audit log lives in app.db (Auth's schema owns it there); make a sentinel row
        // and prove the analytics pruner leaves it alone.
        await using (var app = new SqliteConnection($"Data Source={System.IO.Path.Combine(DataDir, "app.db")};Mode=ReadWriteCreate"))
        {
            await app.OpenAsync();
            await app.ExecuteAsync("CREATE TABLE IF NOT EXISTS audit_log(at INTEGER, actor INTEGER, action TEXT, target TEXT, detail TEXT, ip TEXT);");
            await app.ExecuteAsync("INSERT INTO audit_log(at, actor, action, target, detail, ip) VALUES(@at, 1, 'test', 'x', 'y', 'z');",
                new { at = now - 500 * day });
        }

        var job = App.Services.GetRequiredService<RetentionJob>();
        await job.RunAsync(CancellationToken.None);

        await using (var db = AnalyticsDb())
        {
            var visitors = db.Query<string>("SELECT visitor FROM visits ORDER BY visitor;").ToList();
            Assert.Equal(new[] { "vMid", "vNew" }, visitors); // 500 days gone
            var ips = db.Query<(string Visitor, string? Ip)>("SELECT visitor, ip FROM visits ORDER BY visitor;");
            Assert.Equal("vMid", ips.First().Visitor);
            Assert.Null(ips.First().Ip); // IP pruned at 30 days, row kept
            Assert.Equal("3.3.3.3", ips.Last().Ip);
            Assert.Equal(0L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM click_events;"));
            Assert.Equal(0L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM bot_hits;"));
        }

        await using (var app = AppDb())
        {
            Assert.Equal(1L, app.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log;"));
        }
    }

    [Fact]
    public async Task Weekly_email_sends_once_per_period_even_after_a_rerun()
    {
        // Pin the clock to a Tuesday so the Monday-Wednesday window is open.
        var fakeNow = DateTimeOffset.UtcNow;
        var backToTuesday = ((int)fakeNow.DayOfWeek + 7 - 2) % 7;
        fakeNow = fakeNow.AddDays(-backToTuesday);

        var job = App.Services.GetRequiredService<WeeklyEmailJob>();
        job.UtcNowForTests = () => fakeNow;

        // One visit inside the week being reported (3 days before the fake Tuesday).
        await using (var db = AnalyticsWriter())
        {
            db.Execute("INSERT INTO visits(visitor, at, path, ip, session_id, converted, bot) VALUES('ownerVisitor', @at, '/work/enclave', '1.1.1.1', 'j1', 1, 0);",
                new { at = fakeNow.AddDays(-3).ToUnixTimeMilliseconds() });
        }

        await using (var app = new SqliteConnection($"Data Source={System.IO.Path.Combine(DataDir, "app.db")};Mode=ReadWriteCreate"))
        {
            await app.OpenAsync();
            await app.ExecuteAsync(
                "INSERT INTO users(email, name, role, active, created_at) VALUES('owner@example.com', 'Owner', 'owner', 1, @now);",
                new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }

        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None); // a server that was down reruns it

        await using (var app = AppDb())
        {
            var outbox = app.Query<(string Kind, string Payload)>("SELECT kind, payload FROM outbox;").ToList();
            Assert.Single(outbox);
            Assert.Equal("mail", outbox[0].Kind);
            Assert.Contains("owner@example.com", outbox[0].Payload);
            Assert.Contains("Visitors", outbox[0].Payload);
            Assert.Contains("work/enclave", outbox[0].Payload);
        }

        await using (var db = AnalyticsDb())
        {
            Assert.Equal(1L, db.ExecuteScalar<long>("SELECT COUNT(*) FROM weekly_reports;"));
        }
    }
}
