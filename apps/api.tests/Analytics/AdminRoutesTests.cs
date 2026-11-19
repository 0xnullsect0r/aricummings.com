using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ari.Features.Analytics;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AriApi.Tests.Analytics;

/// <summary>Admin read routes: shapes the SPA renders, CSV formula escaping, guards.</summary>
public sealed class AdminRoutesTests : AnalyticsHarness
{
    private async Task SeedAsync()
    {
        // The title and campaign are visitor-controlled: whatever survives validation must
        // still be escaped as a CSV formula downstream.
        var beacon = Beacon("visitor0001", "journey0001", string.Join(",",
            """{"t":"view","path":"/","title":"=SUM(A1:A2)","referrer":"https://news.ycombinator.com/","campaign":"@risk"}""",
            """{"t":"click","label":"+1","target":"https://github.com/x","x":1,"y":2,"section":"footer"}"""));
        Assert.Equal(HttpStatusCode.NoContent, await PostBeaconAsync(beacon));
        await FlushAsync();
    }

    [Fact]
    public async Task Pages_referrers_clicks_geo_shapes()
    {
        await SeedAsync();
        foreach (var (path, array) in new[]
                 {
                     ("/api/admin/analytics/pages?days=7", "pages"),
                     ("/api/admin/analytics/referrers?days=7", "referrers"),
                     ("/api/admin/analytics/referrers?days=7", "campaigns"),
                     ("/api/admin/analytics/clicks?days=7", "clicks"),
                     ("/api/admin/analytics/geo?days=7", "places"),
                 })
        {
            var (status, body, _) = await GetAdminAsync(path);
            Assert.Equal(HttpStatusCode.OK, status);
            using var document = JsonDocument.Parse(body);
            Assert.Equal(7, document.RootElement.GetProperty("days").GetInt32());
            Assert.True(document.RootElement.TryGetProperty(array, out _), $"{path} has {array}");
        }

        var (geoStatus, geoBody, _) = await GetAdminAsync("/api/admin/analytics/geo");
        using var geo = JsonDocument.Parse(geoBody);
        Assert.Equal(HttpStatusCode.OK, geoStatus);
        Assert.False(geo.RootElement.GetProperty("geo").GetBoolean()); // no mmdb
        Assert.Null(geo.RootElement.GetProperty("attribution").GetString()); // no attribution promise
        var place = geo.RootElement.GetProperty("places").EnumerateArray().Single();
        Assert.Equal("(unknown)", place.GetProperty("country").GetString());
        Assert.Equal(1L, place.GetProperty("views").GetInt64());
    }

    [Fact]
    public async Task Referrers_list_sources_and_campaigns()
    {
        await SeedAsync();
        var (_, body, _) = await GetAdminAsync("/api/admin/analytics/referrers");
        using var document = JsonDocument.Parse(body);
        var source = document.RootElement.GetProperty("referrers").EnumerateArray().Single();
        Assert.Equal("news.ycombinator.com", source.GetProperty("source").GetString());
        Assert.Equal(1L, source.GetProperty("visits").GetInt64());
        var campaign = document.RootElement.GetProperty("campaigns").EnumerateArray().Single();
        Assert.Equal("@risk", campaign.GetProperty("campaign").GetString());
    }

    [Fact]
    public async Task Export_csv_is_an_attachment_with_formula_escaping()
    {
        await SeedAsync();
        var (status, body, disposition) = await GetAdminAsync("/api/admin/analytics/export.csv?days=7");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(disposition);
        Assert.Contains("attachment", disposition);

        var lines = body.Split("\r\n");
        Assert.Equal("day,time,visitor,journey,path,title,referrer,campaign,browser,os,device,screen,language,timezone,country,region,city,network,active_ms,scroll_pct,converted",
            lines[0].Replace("\"", ""));
        var dataLine = lines[1];
        Assert.Contains("'=SUM(A1:A2)", dataLine); // formula prefixed
        Assert.Contains("'@risk", dataLine);       // formula prefixed
        Assert.DoesNotContain("\"=SUM", dataLine); // never un-prefixed
    }

    [Fact]
    public async Task Admin_routes_refuse_the_public_surface()
    {
        using var client = Client();
        using var response = await client.GetAsync($"http://localhost:{PublicPort}/api/admin/analytics/summary");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Guard_default_allows_and_a_real_guard_can_deny()
    {
        var (status, _, _) = await GetAdminAsync("/api/admin/analytics/summary");
        Assert.Equal(HttpStatusCode.OK, status); // the registered no-op default allows

        var denying = new DenyingApp();
        await denying.InitializeAsync();
        try
        {
            var (denied, _, _) = await denying.GetAdminAsync("/api/admin/analytics/summary");
            Assert.Equal(HttpStatusCode.Forbidden, denied);
        }
        finally
        {
            await denying.DisposeAsync();
        }
    }

    private sealed class DenyingGuard : IAnalyticsGuard
    {
        public Task<int?> Deny(HttpContext context, string permission) => Task.FromResult<int?>(StatusCodes.Status403Forbidden);
    }

    private sealed class DenyingApp : AnalyticsHarness
    {
        protected override Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? ExtraServices =>
            services => services.AddSingleton<IAnalyticsGuard>(new DenyingGuard());
    }
}
