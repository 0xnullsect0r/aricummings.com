using System.Net;
using System.Text.Json;
using Dapper;
using Xunit;

namespace AriApi.Tests.Analytics;

/// <summary>Journey grouping (entry/exit/path) and conversion counting through the report routes.</summary>
public sealed class JourneyConversionTests : AnalyticsHarness
{
    private async Task SeedJourney()
    {
        // One journey: home -> enclave -> home, contact form sent on the last page.
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(
            Beacon("visitor0001", "journeyAAAA", """{"t":"view","path":"/","referrer":"https://duckduckgo.com/"}""")));
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(
            Beacon("visitor0001", "journeyAAAA", """{"t":"view","path":"/work/enclave"},{"t":"active","ms":10000},{"t":"scroll","pct":50}""")));
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(
            Beacon("visitor0001", "journeyAAAA", """{"t":"view","path":"/"},{"t":"click","label":"Email","target":"mailto:hi@example.com","x":10,"y":20,"section":"contact"},{"t":"conversion"}""")));
        // A second journey that only looked at the home page and left via an outbound link.
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(
            Beacon("visitor0002", "journeyBBBB", """{"t":"view","path":"/"},{"t":"outbound","label":"GitHub","target":"https://github.com/x"}""")));
        await FlushAsync();
    }

    [Fact]
    public async Task Entry_exit_and_conversion_flags_are_written()
    {
        await SeedJourney();
        await using var db = AnalyticsDb();
        var journey = db.Query<(string Path, long Converted)>(
            "SELECT path, converted FROM visits WHERE session_id='journeyAAAA' ORDER BY id;").ToList();
        Assert.Equal(new[] { "/", "/work/enclave", "/" }, journey.Select(j => j.Path));
        Assert.All(journey, j => Assert.Equal(1L, j.Converted)); // conversion marked the whole journey
        var outbound = db.Query<(string Path, string Kind)>(
            "SELECT path, kind FROM click_events WHERE session='journeyBBBB';").ToList();
        Assert.Single(outbound);
        Assert.Equal("/", outbound[0].Path);
        Assert.Equal("outbound", outbound[0].Kind);
    }

    [Fact]
    public async Task Summary_reports_journeys_entry_and_exit_pages()
    {
        await SeedJourney();
        var (status, body, _) = await GetAdminAsync("/api/admin/analytics/summary?days=7");
        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(4L, root.GetProperty("views").GetInt64());
        Assert.Equal(2L, root.GetProperty("visitors").GetInt64());
        Assert.Equal(2L, root.GetProperty("journeys").GetInt64());
        Assert.Equal(2L, root.GetProperty("convertedJourneys").GetInt64());
        Assert.Equal(1L, root.GetProperty("contactConversions").GetInt64());
        Assert.Equal(1L, root.GetProperty("outboundConversions").GetInt64());

        var entries = root.GetProperty("topEntryPages").EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal("/", entries[0].GetProperty("path").GetString());
        Assert.Equal(2L, entries[0].GetProperty("count").GetInt64());

        // Exits group by path: both journeys left from the home page.
        var exits = root.GetProperty("topExitPages").EnumerateArray().ToList();
        Assert.Single(exits);
        Assert.Equal("/", exits[0].GetProperty("path").GetString());
        Assert.Equal(2L, exits[0].GetProperty("count").GetInt64());

        Assert.Equal(7, root.GetProperty("viewsPerDay").GetArrayLength());
        Assert.Equal(7, root.GetProperty("hitsPerDay").GetArrayLength());
    }

    [Fact]
    public async Task Funnel_counts_view_work_contact_sent()
    {
        await SeedJourney();
        var (status, body, _) = await GetAdminAsync("/api/admin/analytics/funnel?days=7");
        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        var steps = document.RootElement.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(4, steps.Count);
        Assert.Equal("visited", steps[0].GetProperty("step").GetString());
        Assert.Equal(2L, steps[0].GetProperty("count").GetInt64());
        Assert.Equal("work", steps[1].GetProperty("step").GetString());
        Assert.Equal(1L, steps[1].GetProperty("count").GetInt64());
        Assert.Equal("contact", steps[2].GetProperty("step").GetString());
        // the mailto click (journey A) plus the conversion (journey A) share one session
        Assert.Equal(1L, steps[2].GetProperty("count").GetInt64());
        Assert.Equal("sent", steps[3].GetProperty("step").GetString());
        Assert.Equal(1L, steps[3].GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task Outbound_click_alone_counts_as_a_conversion()
    {
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(
            Beacon("visitor0009", "journey0009", """{"t":"view","path":"/"},{"t":"outbound","label":"GitHub","target":"https://github.com/x"}""")));
        await FlushAsync();
        var (status, body, _) = await GetAdminAsync("/api/admin/analytics/summary");
        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(1L, document.RootElement.GetProperty("outboundConversions").GetInt64());
        Assert.Equal(1L, document.RootElement.GetProperty("convertedJourneys").GetInt64());
        Assert.Equal(0L, document.RootElement.GetProperty("contactConversions").GetInt64());
    }
}
