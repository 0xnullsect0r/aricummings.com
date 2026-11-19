using System.Net;
using Dapper;
using Xunit;

namespace AriApi.Tests.Analytics;

/// <summary>The buffered writer: daily totals, and the flush-on-shutdown the smoke test proves.</summary>
public sealed class WriterTests : AnalyticsHarness
{
    [Fact]
    public async Task Page_hit_counter_counts_pages_only()
    {
        using var client = Client();
        // Every GET 502s (web is down), but the edge counts the hit before the proxy runs.
        await client.GetAsync($"http://localhost:{PublicPort}/");               // counted
        await client.GetAsync($"http://localhost:{PublicPort}/work/enclave");   // counted
        await client.GetAsync($"http://localhost:{PublicPort}/healthz");        // probe, not a page
        await client.GetAsync($"http://localhost:{PublicPort}/api/health");     // api
        await client.GetAsync($"http://localhost:{PublicPort}/logo.png");       // asset
        await FlushAsync();

        await using var db = AnalyticsDb();
        var totals = db.Query<(string Day, long Hits)>("SELECT day, hits FROM daily_totals;").ToList();
        Assert.Single(totals);
        Assert.Equal(2L, totals[0].Hits);
    }

    [Fact]
    public async Task Buffered_events_survive_a_shutdown_inside_the_drain_window()
    {
        // Send a beacon and stop the app WITHOUT waiting for the 2-second drain tick:
        // the hosted stop must flush what is still buffered before it returns.
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(
            Beacon("visitor0001", "journey0001", """{"t":"view","path":"/"}""")));
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
        App = null!;

        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={System.IO.Path.Combine(DataDir, "analytics.db")};Mode=ReadOnly");
        connection.Open();
        try
        {
            Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM visits;"));
            Assert.Equal(2L, connection.ExecuteScalar<long>("SELECT received FROM ingest_stats;")); // 1 + 1 view event
        }
        finally
        {
            connection.Dispose();
        }
    }
}
