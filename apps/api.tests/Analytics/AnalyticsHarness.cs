using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Ari.Platform;
using Ari.Platform.Crypto;
using Ari.Features.Analytics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AriApi.Tests.Analytics;

/// <summary>
/// One app per test, built exactly like Program does: the same reflection discovery,
/// the same ApiHost, on real sockets. Ports come from the 8120-8129 range this area
/// owns and the collection serializes tests so they never overlap.
/// </summary>
[Collection("analytics")]
public abstract class AnalyticsHarness : IAsyncLifetime
{
    public const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    protected virtual Action<IServiceCollection>? ExtraServices => null;

    public WebApplication App = null!;
    public int PublicPort;
    public int AdminPort;
    public int InternalPort;
    public string DataDir = null!;

    public async Task InitializeAsync()
    {
        (PublicPort, AdminPort, InternalPort) = AnalyticsPorts.Take();
        DataDir = Path.Combine("/tmp", "analytics-wt", Guid.NewGuid().ToString("N"));
        var env = new Env(
            publicOrigin: $"http://localhost:{PublicPort}",
            adminOrigin: $"http://localhost:{AdminPort}",
            adminPort: AdminPort,
            publicPort: PublicPort,
            internalPort: InternalPort,
            webOrigin: "http://127.0.0.1:1", // nothing listens: page proxy 502s, hits still count
            dataDir: DataDir,
            mediaDir: Path.Combine(DataDir, "media"),
            keys: KeyRing.Load(null, $"test:{Convert.ToBase64String(new byte[32])}", production: false),
            trustedProxies: new List<(IPAddress, int)>(),
            production: false,
            webauthnRpId: null,
            geoDbPath: null);

        // Exactly Program.cs's discovery, so the feature proves itself end to end.
        var features = typeof(ProgramMarker).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IFeature).IsAssignableFrom(t))
            .Select(t => (IFeature)Activator.CreateInstance(t)!)
            .OrderBy(f => f.Order)
            .ToList();

        App = ApiHost.Build(env,
            configure: services =>
            {
                foreach (var feature in features) feature.Register(services);
                // These suites exercise analytics logic; auth's suite covers the real
                // permission enforcement, so the open guard is re-registered last and
                // wins, exactly as before the wiring merge.
                services.AddSingleton<Ari.Features.Analytics.IAnalyticsGuard>(Ari.Features.Analytics.OpenAnalyticsGuard.Instance);
                ExtraServices?.Invoke(services);
            },
            map: mapped =>
            {
                foreach (var feature in features) feature.Map(mapped);
            });
        _ = App.RunAsync();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = await probe.GetAsync($"http://localhost:{PublicPort}/healthz");
                if (response.StatusCode == HttpStatusCode.OK) break;
            }
            catch { }
            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        if (App is null) return;
        // The way SIGTERM does it in production: stop (hosted services flush), then dispose.
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }

    public async Task FlushAsync()
    {
        var store = App.Services.GetRequiredService<AnalyticsStore>();
        Assert.True(await store.FlushAsync(TimeSpan.FromSeconds(5)), "writer did not go idle in time");
    }

    public SqliteConnection AnalyticsDb()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(DataDir, "analytics.db")};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    public SqliteConnection AppDb()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(DataDir, "app.db")};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    public HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>POSTs one beacon the way the site client does: text body, same-origin header.</summary>
    public async Task<HttpStatusCode> PostBeaconAsync(string json, string userAgent = ChromeUserAgent)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{PublicPort}/api/analytics/beacon");
        request.Headers.TryAddWithoutValidation("Origin", $"http://localhost:{PublicPort}");
        if (userAgent.Length > 0) request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Content = new StringContent(json, Encoding.UTF8, "text/plain");
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    public async Task<(HttpStatusCode Status, string Body, string? ContentDisposition)> GetAdminAsync(string path)
    {
        using var client = Client();
        using var response = await client.GetAsync($"http://localhost:{AdminPort}{path}");
        return (response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentDisposition?.ToString());
    }

    public static string Beacon(string visitor, string journey, string eventsJson,
        string rootJson = "\"sw\":1920,\"sh\":1080,\"lang\":\"en-US\",\"tz\":\"America/Los_Angeles\"") =>
        $$"""{"v":1,"visitor":"{{visitor}}","journey":"{{journey}}",{{rootJson}},"events":[{{eventsJson}}]}""";
}

/// <summary>Fixed allocation inside this area's port range, skipping ports already taken.</summary>
internal static class AnalyticsPorts
{
    private static int _next = 8120;
    private static readonly object Gate = new();

    public static (int Public, int Admin, int Internal) Take()
    {
        lock (Gate)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var p = _next;
                _next += 3;
                if (_next > 8127) _next = 8120;
                if (Bindable(p) && Bindable(p + 1) && Bindable(p + 2)) return (p, p + 1, p + 2);
            }
            return (8120, 8121, 8122);
        }
    }

    private static bool Bindable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
