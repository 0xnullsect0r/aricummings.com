using Ari.Platform;
using Ari.Platform.Database;
using Ari.Platform.Outbox;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ari.Features.Analytics;

/// <summary>
/// Weekly owner email: visitors, new visitors, pageviews, reading time with change on
/// last week, then top 5 pages, top 5 sources and the funnel. Runs Monday to Wednesday
/// at 8 in site time; the period is recorded in weekly_reports, so a server that was
/// down Monday still sends it and nothing sends twice. Mail goes through the Platform
/// outbox; the job enqueues one message per active owner.
/// </summary>
public sealed class WeeklyEmailJob
{
    private readonly Db _db;
    private readonly AnalyticsClock _clock;
    private readonly AnalyticsQueries _queries;
    private readonly IOutbox _outbox;
    private readonly ILogger<WeeklyEmailJob> _logger;

    public WeeklyEmailJob(Db db, AnalyticsClock clock, AnalyticsQueries queries, IOutbox outbox, ILogger<WeeklyEmailJob> logger)
    {
        _db = db;
        _clock = clock;
        _queries = queries;
        _outbox = outbox;
        _logger = logger;
    }

    /// <summary>Tests pin the clock; the Monday-Wednesday window must be deterministic.</summary>
    internal Func<DateTimeOffset>? UtcNowForTests { get; set; }

    public async Task RunAsync(CancellationToken cancellation)
    {
        var now = UtcNowForTests?.Invoke() ?? DateTimeOffset.UtcNow;
        var monday = _clock.MondayOf(now); // site-local Monday of the running week
        var localNow = TimeZoneInfo.ConvertTime(now, _clock.Zone);

        // Monday to Wednesday: the week that just finished. Later is stale, skip.
        if (localNow.Date > monday.AddDays(2)) return;

        // Monday 00:00 site-local, as UTC instants.
        var start = monday.AddDays(-7);
        var weekStart = new DateTimeOffset(start, _clock.Zone.GetUtcOffset(start));
        var weekEnd = new DateTimeOffset(monday, _clock.Zone.GetUtcOffset(monday));
        var previousStart = weekStart.AddDays(-7);
        var period = _clock.IsoWeek(weekStart + TimeSpan.FromHours(12));

        await using (var analytics = new SqliteConnection($"Data Source={_db.AnalyticsPath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await analytics.OpenAsync();
            await analytics.ExecuteAsync("PRAGMA busy_timeout=5000;");
            var already = await analytics.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM weekly_reports WHERE period = @period;", new { period });
            if (already > 0) return;
        }

        var current = await Numbers(weekStart.ToUnixTimeMilliseconds(), weekEnd.ToUnixTimeMilliseconds() - 1);
        var previous = await Numbers(previousStart.ToUnixTimeMilliseconds(), weekStart.ToUnixTimeMilliseconds() - 1);

        await using (var app = _db.App())
        {
            var owners = (await app.QueryAsync<string>(
                "SELECT email FROM users WHERE role = 'owner' AND active = 1;")).ToList();
            if (owners.Count == 0)
            {
                _logger.LogWarning("Weekly analytics report {Period}: no active owners, not recorded", period);
                return;
            }

            var text = Compose(period, current, previous);
            foreach (var owner in owners)
            {
                await _outbox.Enqueue("mail", new Dictionary<string, string>
                {
                    ["to"] = owner,
                    ["subject"] = $"aricummings.com analytics, week {period}",
                    ["text"] = text
                });
            }
        }

        await using (var analytics = new SqliteConnection($"Data Source={_db.AnalyticsPath};Mode=ReadWriteCreate;Pooling=False"))
        {
            await analytics.OpenAsync();
            await analytics.ExecuteAsync("PRAGMA busy_timeout=5000;");
            await analytics.ExecuteAsync(
                "INSERT INTO weekly_reports(period, sent_at) VALUES(@period, @now);",
                new { period, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        }
        _logger.LogInformation("Weekly analytics report {Period} queued", period);
    }

    private async Task<WeekNumbers> Numbers(long from, long to)
    {
        await using var connection = await _queries.OpenAsync();
        var totals = await connection.QuerySingleAsync<(long Visitors, long NewVisitors, long Views, long ActiveSum, long ActiveCount, long Conversions)>(
            """
            SELECT COUNT(DISTINCT visitor),
                   (SELECT COUNT(*) FROM (SELECT visitor, MIN(at) f FROM visits WHERE bot = 0 GROUP BY visitor HAVING f BETWEEN @from AND @to)),
                   COUNT(*), COALESCE(SUM(active_ms), 0), COALESCE(SUM(active_ms IS NOT NULL), 0),
                   COUNT(DISTINCT CASE WHEN converted = 1 THEN session_id END)
            FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to;
            """, new { from, to });

        var pages = await connection.QueryAsync<(string Path, long Count)>(
            """
            SELECT path, COUNT(*) FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to
            GROUP BY path ORDER BY COUNT(*) DESC LIMIT 5;
            """, new { from, to });

        var sources = await connection.QueryAsync<(string Source, long Count)>(
            """
            SELECT COALESCE(referrer, '(direct)'), COUNT(DISTINCT session_id) FROM visits
            WHERE bot = 0 AND at BETWEEN @from AND @to
            GROUP BY referrer ORDER BY COUNT(DISTINCT session_id) DESC LIMIT 5;
            """, new { from, to });

        var funnel = await connection.QueryAsync<(string Step, long Count)>(
            """
            SELECT 'work', COUNT(DISTINCT session_id) FROM visits
              WHERE bot = 0 AND at BETWEEN @from AND @to AND path LIKE '/work/%'
            UNION ALL
            SELECT 'contact', COUNT(DISTINCT session) FROM click_events
              WHERE at BETWEEN @from AND @to AND (kind = 'conversion' OR target LIKE 'mailto:%' OR section = 'contact');
            """, new { from, to });

        return new WeekNumbers(
            totals.Visitors, totals.NewVisitors, totals.Views,
            totals.ActiveCount == 0 ? 0 : totals.ActiveSum / totals.ActiveCount,
            totals.Conversions,
            pages.Select(p => (p.Path, p.Count)).ToList(),
            sources.Select(s => (s.Source, s.Count)).ToList(),
            funnel.ToDictionary(f => f.Step, f => f.Count));
    }

    private static string Compose(string period, WeekNumbers current, WeekNumbers previous) =>
        $"""
         aricummings.com analytics, week {period}

         Visitors      {current.Visitors,6}  {Change(current.Visitors, previous.Visitors)}
         New visitors  {current.NewVisitors,6}  {Change(current.NewVisitors, previous.NewVisitors)}
         Pageviews     {current.Views,6}  {Change(current.Views, previous.Views)}
         Reading time  {ReadingTime(current.AvgActiveMs),6}  {Change(current.AvgActiveMs, previous.AvgActiveMs)}
         Conversions   {current.Conversions,6}

         Top pages
         {TopList(current.Pages)}

         Top sources
         {TopList(current.Sources)}

         Funnel
           Visited            {current.Visitors}
           Viewed a work page {current.Funnel.GetValueOrDefault("work")}
           Opened contact     {current.Funnel.GetValueOrDefault("contact")}
           Sent               {current.Conversions}

         IP geolocation by MaxMind (GeoLite2), when enabled.
         """;

    private static string Change(long current, long previous)
    {
        if (previous == 0) return current == 0 ? "—" : "new";
        var pct = (current - previous) * 100 / previous;
        return pct >= 0 ? $"+{pct}%" : $"{pct}%";
    }

    private static string ReadingTime(long activeMs)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, activeMs));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    private static string TopList(IReadOnlyList<(string Name, long Count)> rows)
    {
        if (rows.Count == 0) return "  (none)";
        return string.Join("\n", rows.Select(r => $"  {r.Name,-40} {r.Count,6}"));
    }

    private sealed record WeekNumbers(
        long Visitors,
        long NewVisitors,
        long Views,
        long AvgActiveMs,
        long Conversions,
        IReadOnlyList<(string Path, long Count)> Pages,
        IReadOnlyList<(string Source, long Count)> Sources,
        IReadOnlyDictionary<string, long> Funnel);
}

/// <summary>
/// Retention, as the privacy notice promises: full IPs are pruned from visits 30 days
/// after collect (geo columns stay), raw event rows go after the retention window
/// (ARI_ANALYTICS_RETENTION_DAYS, 400 by default). Daily totals, ingest_stats and
/// weekly_reports are aggregates and stay. The audit log lives in the OTHER database
/// (app.db) and is never pruned; this job opens analytics.db only.
/// </summary>
public sealed class RetentionJob
{
    private static readonly TimeSpan IpRetention = TimeSpan.FromDays(30);

    private readonly Db _db;
    private readonly ILogger<RetentionJob> _logger;

    public RetentionJob(Db db, ILogger<RetentionJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        var days = 400;
        if (int.TryParse(Environment.GetEnvironmentVariable("ARI_ANALYTICS_RETENTION_DAYS"), out var configured))
            days = Math.Clamp(configured, 7, 3650);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ipCutoff = now - (long)IpRetention.TotalMilliseconds;
        var eventCutoff = now - TimeSpan.FromDays(days).TotalMilliseconds;

        await using var connection = new SqliteConnection($"Data Source={_db.AnalyticsPath};Mode=ReadWriteCreate;Pooling=False");
        await connection.OpenAsync();
        foreach (var pragma in new[]
                 {
                     "PRAGMA journal_mode=WAL;",
                     "PRAGMA busy_timeout=5000;",
                     "PRAGMA synchronous=NORMAL;",
                     "PRAGMA journal_size_limit=67108864;",
                 })
            await connection.ExecuteAsync(pragma);

        // Immediate transaction: the whole window deletes or none of it.
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
        var ips = await connection.ExecuteAsync(
            "UPDATE visits SET ip = NULL, ipv6_net = NULL WHERE ip IS NOT NULL AND at < @cutoff;",
            new { cutoff = ipCutoff }, transaction);
        var visits = await connection.ExecuteAsync(
            "DELETE FROM visits WHERE at < @cutoff;", new { cutoff = (long)eventCutoff }, transaction);
        var clicks = await connection.ExecuteAsync(
            "DELETE FROM click_events WHERE at < @cutoff;", new { cutoff = (long)eventCutoff }, transaction);
        var bots = await connection.ExecuteAsync(
            "DELETE FROM bot_hits WHERE at < @cutoff;", new { cutoff = (long)eventCutoff }, transaction);
        await transaction.CommitAsync(cancellation);

        _logger.LogInformation(
            "Analytics retention: pruned IPs on {Ips} visits, deleted {Visits} visits, {Clicks} clicks, {Bots} bot hits",
            ips, visits, clicks, bots);
    }
}
