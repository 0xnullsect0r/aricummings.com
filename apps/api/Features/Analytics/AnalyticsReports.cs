using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ari.Features.Analytics;

/// <summary>
/// Admin read routes: summary, pages, referrers, clicks, geo, funnel, export.csv.
/// Every route is behind <see cref="IAnalyticsGuard"/> (analytics.read; export also
/// analytics.export) and reads through a query_only connection, never the writer's.
/// JSON answers go through Results.Json, whose web encoder escapes &lt; so nothing
/// stored can become markup inside a JSON-LD-shaped payload.
/// </summary>
public sealed class AnalyticsReports
{
    private readonly AnalyticsQueries _queries;
    private readonly AnalyticsClock _clock;
    private readonly GeoResolver _geo;

    public AnalyticsReports(AnalyticsQueries queries, AnalyticsClock clock, GeoResolver geo)
    {
        _queries = queries;
        _clock = clock;
        _geo = geo;
    }

    public static void MapAll(WebApplication app)
    {
        Map(app, "/api/admin/analytics/summary", "analytics.read", static r => r.Summary);
        Map(app, "/api/admin/analytics/pages", "analytics.read", static r => r.Pages);
        Map(app, "/api/admin/analytics/referrers", "analytics.read", static r => r.Referrers);
        Map(app, "/api/admin/analytics/clicks", "analytics.read", static r => r.Clicks);
        Map(app, "/api/admin/analytics/geo", "analytics.read", static r => r.Geo);
        Map(app, "/api/admin/analytics/funnel", "analytics.read", static r => r.Funnel);
        Map(app, "/api/admin/analytics/export.csv", "analytics.export", static r => r.Export);
    }

    /// <summary>
    /// Parameters are injected (not a bare HttpContext) so the handler can never be
    /// mistaken for a RequestDelegate, which would discard the IResult.
    /// </summary>
    private static void Map(WebApplication app, string path, string permission,
        Func<AnalyticsReports, Func<HttpRequest, Task<IResult>>> pick)
    {
        app.MapGet(path, async (HttpRequest request, IAnalyticsGuard guard, AnalyticsReports reports) =>
        {
            if (await guard.Deny(request.HttpContext, permission) is { } status)
                return Results.StatusCode(status);
            return await pick(reports)(request);
        });
    }

    private static (int Days, long From, long To) Window(HttpRequest request, AnalyticsClock clock)
    {
        var days = 7;
        if (int.TryParse(request.Query["days"].ToString(), out var asked)) days = Math.Clamp(asked, 1, 90);
        var now = DateTimeOffset.UtcNow;
        var from = clock.StartOfDay(now, days - 1);
        return (days, from.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds());
    }

    // ---- summary: totals, per-day series, journey entry/exit pages ------------

    private sealed class DayBucket
    {
        public int Views;
        public readonly HashSet<string> Visitors = new(StringComparer.Ordinal);
    }

    private async Task<IResult> Summary(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var totals = await connection.QuerySingleAsync<(long Views, long Visitors, long Journeys, long ActiveSum, long ActiveCount, long ScrollSum, long ScrollCount)>(
            """
            SELECT COUNT(*), COUNT(DISTINCT visitor), COUNT(DISTINCT session_id),
                   COALESCE(SUM(active_ms), 0), COALESCE(SUM(active_ms IS NOT NULL), 0),
                   COALESCE(SUM(scroll_pct), 0), COALESCE(SUM(scroll_pct IS NOT NULL), 0)
            FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to;
            """, new { from, to });

        var newVisitors = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM (
                SELECT visitor, MIN(at) AS first_at FROM visits WHERE bot = 0
                GROUP BY visitor HAVING first_at BETWEEN @from AND @to);
            """, new { from, to });

        var converted = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(DISTINCT session_id) FROM visits WHERE bot = 0 AND converted = 1 AND at BETWEEN @from AND @to;",
            new { from, to });

        var contact = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM click_events WHERE kind = 'conversion' AND at BETWEEN @from AND @to;",
            new { from, to });

        var outbound = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM click_events WHERE kind = 'outbound' AND at BETWEEN @from AND @to;",
            new { from, to });

        var botHits = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM bot_hits WHERE at BETWEEN @from AND @to;", new { from, to });

        var perDayRows = await connection.QueryAsync<(long At, string Visitor)>(
            "SELECT at, visitor FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to;", new { from, to });
        var dayList = _clock.Days(days);
        var viewsPerDay = dayList.ToDictionary(d => d, _ => new DayBucket(), StringComparer.Ordinal);
        foreach (var (at, visitor) in perDayRows)
        {
            var key = _clock.Day(DateTimeOffset.FromUnixTimeMilliseconds(at));
            if (viewsPerDay.TryGetValue(key, out var bucket))
            {
                bucket.Views++;
                bucket.Visitors.Add(visitor);
            }
        }

        var hitsRows = await connection.QueryAsync<(string Day, long Hits)>(
            "SELECT day, hits FROM daily_totals WHERE day IN @dayList;", new { dayList });
        var hitsPerDay = dayList.ToDictionary(d => d, _ => 0L, StringComparer.Ordinal);
        foreach (var (day, hits) in hitsRows) hitsPerDay[day] = hits;

        var entries = await connection.QueryAsync<(string Path, long Count)>(EntryExitSql("MIN"), new { from, to });
        var exits = await connection.QueryAsync<(string Path, long Count)>(EntryExitSql("MAX"), new { from, to });

        return Results.Json(new
        {
            days,
            from = dayList[0],
            to = dayList[^1],
            views = totals.Views,
            visitors = totals.Visitors,
            newVisitors,
            journeys = totals.Journeys,
            convertedJourneys = converted,
            contactConversions = contact,
            outboundConversions = outbound,
            avgActiveMs = totals.ActiveCount == 0 ? 0 : totals.ActiveSum / totals.ActiveCount,
            avgScrollPct = totals.ScrollCount == 0 ? 0 : totals.ScrollSum / totals.ScrollCount,
            botHits,
            viewsPerDay = dayList.Select(d => new { day = d, views = viewsPerDay[d].Views, visitors = viewsPerDay[d].Visitors.Count }),
            hitsPerDay = dayList.Select(d => new { day = d, hits = hitsPerDay[d] }),
            topEntryPages = entries.Select(e => new { path = e.Path, count = e.Count }),
            topExitPages = exits.Select(e => new { path = e.Path, count = e.Count })
        });
    }

    private static string EntryExitSql(string extreme)
    {
        var earlier = extreme == "MIN"
            ? "o.at < v.at OR (o.at = v.at AND o.id < v.id)"
            : "o.at > v.at OR (o.at = v.at AND o.id > v.id)";
        return $"""
            SELECT path, COUNT(*) FROM (
                SELECT v.session_id, v.path FROM visits v
                WHERE v.bot = 0 AND v.at BETWEEN @from AND @to
                  AND NOT EXISTS (SELECT 1 FROM visits o WHERE o.session_id = v.session_id AND o.bot = 0
                                  AND ({earlier}))
                GROUP BY v.session_id, v.path)
            GROUP BY path ORDER BY COUNT(*) DESC LIMIT 10;
            """;
    }

    // ---- pages: views, engagement, journey entry/exit per page ---------------

    private async Task<IResult> Pages(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var pages = await connection.QueryAsync<(string Path, long Views, long Visitors, long? AvgActive, long? AvgScroll, long Entries, long Exits, long Conversions)>(
            """
            SELECT path, COUNT(*), COUNT(DISTINCT visitor),
                   CAST(AVG(active_ms) AS INTEGER), CAST(AVG(scroll_pct) AS INTEGER),
                   SUM(CASE WHEN id IN (SELECT MIN(id) FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to
                                        GROUP BY session_id) THEN 1 ELSE 0 END),
                   SUM(CASE WHEN id IN (SELECT MAX(id) FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to
                                        GROUP BY session_id) THEN 1 ELSE 0 END),
                   COUNT(DISTINCT CASE WHEN converted = 1 THEN session_id END)
            FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to
            GROUP BY path ORDER BY COUNT(*) DESC LIMIT 100;
            """, new { from, to });

        var transitions = await connection.QueryAsync<(string From, string To, long Count)>(
            """
            SELECT a.path, b.path, COUNT(*) FROM visits a
            JOIN visits b ON b.session_id = a.session_id AND (b.at > a.at OR (b.at = a.at AND b.id > a.id))
            WHERE a.bot = 0 AND b.bot = 0 AND a.at BETWEEN @from AND @to
              AND NOT EXISTS (SELECT 1 FROM visits m WHERE m.session_id = a.session_id AND m.bot = 0
                              AND (m.at > a.at OR (m.at = a.at AND m.id > a.id))
                              AND (m.at < b.at OR (m.at = b.at AND m.id < b.id)))
            GROUP BY a.path, b.path ORDER BY COUNT(*) DESC LIMIT 10;
            """, new { from, to });

        return Results.Json(new
        {
            days,
            pages = pages.Select(p => new
            {
                path = p.Path,
                views = p.Views,
                visitors = p.Visitors,
                avgActiveMs = p.AvgActive ?? 0,
                avgScrollPct = p.AvgScroll ?? 0,
                entries = p.Entries,
                exits = p.Exits,
                convertedJourneys = p.Conversions
            }),
            topNextPages = transitions.Select(t => new { from = t.From, to = t.To, count = t.Count })
        });
    }

    // ---- sources: referring hosts and campaign tags ---------------------------

    private async Task<IResult> Referrers(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var sources = await connection.QueryAsync<(string Source, long Visits, long Visitors)>(
            """
            SELECT COALESCE(referrer, '(direct)'), COUNT(*), COUNT(DISTINCT visitor)
            FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to
            GROUP BY referrer ORDER BY COUNT(*) DESC LIMIT 20;
            """, new { from, to });

        var campaigns = await connection.QueryAsync<(string Campaign, long Visits)>(
            """
            SELECT campaign, COUNT(*) FROM visits
            WHERE bot = 0 AND at BETWEEN @from AND @to AND campaign IS NOT NULL AND campaign != ''
            GROUP BY campaign ORDER BY COUNT(*) DESC LIMIT 20;
            """, new { from, to });

        return Results.Json(new
        {
            days,
            referrers = sources.Select(s => new { source = s.Source, visits = s.Visits, visitors = s.Visitors }),
            campaigns = campaigns.Select(c => new { campaign = c.Campaign, visits = c.Visits })
        });
    }

    // ---- click maps -----------------------------------------------------------

    private async Task<IResult> Clicks(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var clicks = await connection.QueryAsync<(string Label, string Target, string Kind, long Count, long Journeys, long LastAt)>(
            """
            SELECT label, COALESCE(target, ''), kind, COUNT(*), COUNT(DISTINCT session), MAX(at)
            FROM click_events WHERE at BETWEEN @from AND @to
            GROUP BY label, target, kind ORDER BY COUNT(*) DESC LIMIT 100;
            """, new { from, to });

        var sections = await connection.QueryAsync<(string Section, long Count)>(
            """
            SELECT COALESCE(section, '(none)'), COUNT(*) FROM click_events
            WHERE at BETWEEN @from AND @to AND kind = 'click'
            GROUP BY section ORDER BY COUNT(*) DESC LIMIT 50;
            """, new { from, to });

        return Results.Json(new
        {
            days,
            clicks = clicks.Select(c => new
            {
                label = c.Label,
                target = c.Target,
                kind = c.Kind,
                count = c.Count,
                journeys = c.Journeys,
                lastAt = c.LastAt
            }),
            sections = sections.Select(s => new { section = s.Section, count = s.Count })
        });
    }

    // ---- geo ------------------------------------------------------------------

    private async Task<IResult> Geo(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var places = await connection.QueryAsync<(string Country, string Region, string City, string Network, long Views, long Visitors)>(
            """
            SELECT COALESCE(country, '(unknown)'), COALESCE(region, ''), COALESCE(city, ''), COALESCE(network, ''),
                   COUNT(*), COUNT(DISTINCT visitor)
            FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to
            GROUP BY country, region, city, network ORDER BY COUNT(*) DESC LIMIT 100;
            """, new { from, to });

        return Results.Json(new
        {
            days,
            geo = _geo.Enabled,
            attribution = _geo.Enabled ? GeoResolver.Attribution : (string?)null,
            places = places.Select(p => new
            {
                country = p.Country,
                region = p.Region,
                city = p.City,
                network = p.Network,
                views = p.Views,
                visitors = p.Visitors
            })
        });
    }

    // ---- funnel: view -> work page -> contact ---------------------------------

    private async Task<IResult> Funnel(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var visited = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(DISTINCT session_id) FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to;",
            new { from, to });
        var work = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(DISTINCT session_id) FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to AND path LIKE '/work/%';",
            new { from, to });
        // "Opened contact": the contact form was sent, a mailto link was clicked, or a
        // click landed in a contact-labelled section.
        var contact = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(DISTINCT session) FROM click_events
            WHERE at BETWEEN @from AND @to
              AND (kind = 'conversion' OR target LIKE 'mailto:%' OR section = 'contact');
            """, new { from, to });
        var sent = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(DISTINCT v.session_id) FROM visits v
            WHERE v.bot = 0 AND v.at BETWEEN @from AND @to
              AND v.converted = 1
              AND EXISTS (SELECT 1 FROM click_events c WHERE c.kind = 'conversion'
                          AND c.session = v.session_id AND c.at BETWEEN @from AND @to);
            """, new { from, to });

        return Results.Json(new
        {
            days,
            steps = new object[]
            {
                new { step = "visited", label = "Visited", count = visited },
                new { step = "work", label = "Viewed a work page", count = work },
                new { step = "contact", label = "Opened contact", count = contact },
                new { step = "sent", label = "Sent", count = sent }
            }
        });
    }

    // ---- export.csv -----------------------------------------------------------

    private async Task<IResult> Export(HttpRequest request)
    {
        var (days, from, to) = Window(request, _clock);
        await using var connection = await _queries.OpenAsync();

        var rows = await connection.QueryAsync(
            """
            SELECT at, visitor, session_id, path, title, referrer, campaign, ip, browser, os, device,
                   screen, language, timezone, country, region, city, network, active_ms, scroll_pct, converted
            FROM visits WHERE bot = 0 AND at BETWEEN @from AND @to ORDER BY at;
            """, new { from, to });

        var writer = new StringWriter();
        writer.WriteCsvRow(new[]
        {
            "day", "time", "visitor", "journey", "path", "title", "referrer", "campaign", "browser", "os",
            "device", "screen", "language", "timezone", "country", "region", "city", "network",
            "active_ms", "scroll_pct", "converted"
        });
        foreach (dynamic row in rows)
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)row.at);
            writer.WriteCsvRow(new[]
            {
                _clock.Day(at),
                at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                (string)row.visitor,
                (string?)row.session_id,
                (string)row.path,
                (string?)row.title,
                (string?)row.referrer,
                (string?)row.campaign,
                (string?)row.browser,
                (string?)row.os,
                (string?)row.device,
                (string?)row.screen,
                (string?)row.language,
                (string?)row.timezone,
                (string?)row.country,
                (string?)row.region,
                (string?)row.city,
                (string?)row.network,
                row.active_ms is null ? "" : ((long)row.active_ms).ToString(),
                row.scroll_pct is null ? "" : ((long)row.scroll_pct).ToString(),
                (long)row.converted == 1 ? "1" : "0"
            });
        }

        var fromDay = _clock.Day(DateTimeOffset.FromUnixTimeMilliseconds(from)).Replace("-", "");
        var toDay = _clock.Day(DateTimeOffset.UtcNow).Replace("-", "");
        var response = request.HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"analytics-{fromDay}-{toDay}.csv\"";
        await response.WriteAsync(writer.ToString());
        return Results.Empty;
    }
}

/// <summary>
/// CSV writing for untrusted values: quotes doubled, and any value starting with
/// =, +, -, @, tab or CR is prefixed with an apostrophe so a spreadsheet cannot run it
/// as a formula. Everything goes through the escaper; visitor data can contain anything.
/// </summary>
public static class CsvWriterExtensions
{
    public static void WriteCsvRow(this TextWriter writer, string?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) writer.Write(',');
            writer.Write(Escape(cells[i]));
        }
        writer.Write("\r\n");
    }

    public static string Escape(string? value)
    {
        if (value is null) return "\"\"";
        var dangerous = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
        var prefixed = dangerous ? "'" + value : value;
        return "\"" + prefixed.Replace("\"", "\"\"") + "\"";
    }
}
