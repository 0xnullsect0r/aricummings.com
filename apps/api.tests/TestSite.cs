using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ari.Platform;
using Ari.Platform.Crypto;
using Ari.Platform.Database;
using Microsoft.AspNetCore.Builder;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AriApi.Tests;

/// <summary>
/// Builds the identical server Program runs — features discovered by reflection — on real
/// sockets inside this worktree's port range (8110-8119), with its own data dir under
/// /tmp/content-wt.
/// </summary>
public sealed class TestSite : IAsyncDisposable
{
    private static int _nextPort = 8110;

    public readonly int PublicPort;
    public readonly int AdminPort;
    public readonly int InternalPort;
    public readonly string DataDir;
    public readonly Db Db;
    public readonly Env Env;
    private readonly WebApplication _app;
    private readonly HttpClient _admin;
    private readonly HttpClient _public;
    private readonly HttpClient _internal;

    static TestSite() => Environment.SetEnvironmentVariable("ARI_TEST_DISABLE_GITHUB_SYNC", "1");

    public TestSite(string name, string? existingDataDir = null)
    {
        PublicPort = TakePort();
        AdminPort = TakePort();
        InternalPort = TakePort();
        DataDir = existingDataDir ?? $"/tmp/content-wt/{name}-{Guid.NewGuid():N}";
        var env = new Env(
            publicOrigin: $"http://localhost:{PublicPort}",
            adminOrigin: $"http://localhost:{AdminPort}",
            adminPort: AdminPort,
            publicPort: PublicPort,
            internalPort: InternalPort,
            webOrigin: "http://127.0.0.1:1",
            dataDir: DataDir,
            mediaDir: Path.Combine(DataDir, "media"),
            keys: KeyRing.Load(null, $"test:{Convert.ToBase64String(new byte[32])}", production: false),
            trustedProxies: new List<(IPAddress, int)>(),
            production: false,
            webauthnRpId: null,
            geoDbPath: null);
        Db = new Db(DataDir);
        Env = env;

        var features = typeof(ProgramMarker).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IFeature).IsAssignableFrom(t))
            .Select(t => (IFeature)Activator.CreateInstance(t)!)
            .OrderBy(f => f.Order)
            .ToList();
        _app = ApiHost.Build(env,
            configure: services =>
            {
                foreach (var feature in features) feature.Register(services);
            },
            map: mapped =>
            {
                foreach (var feature in features) feature.Map(mapped);
            });
        _ = _app.RunAsync();
        WaitReady();

        _admin = Client($"http://localhost:{AdminPort}", $"http://localhost:{AdminPort}");
        _public = Client($"http://localhost:{PublicPort}", $"http://localhost:{PublicPort}");
        _internal = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public HttpClient Admin => _admin;
    public HttpClient Public => _public;
    public HttpClient Internal => _internal;
    public string InternalBase => $"http://localhost:{InternalPort}";

    private static int TakePort()
    {
        // Try the next port in the range; fall forward when something still holds it.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = _nextPort;
            _nextPort = _nextPort >= 8119 ? 8110 : _nextPort + 1;
            try
            {
                using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }
        throw new InvalidOperationException("No free port in 8110-8119.");
    }

    private void WaitReady()
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var response = probe.GetAsync($"http://localhost:{PublicPort}/api/health").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return;
            }
            catch
            {
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException($"Site on {PublicPort} never became ready.");
    }

    private static HttpClient Client(string baseUrl, string origin)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    public async Task<HttpResponseMessage> PutJsonAsync(string path, object body) =>
        await Admin.PutAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    public async Task<HttpResponseMessage> PostJsonAsync(string path, object? body = null) =>
        await Admin.PostAsync(path, body is null
            ? null
            : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    public static async Task<JsonDocument> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _public.Dispose();
        _internal.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>Plain assertions on JSON bodies.</summary>
public static class JsonDocumentExtensions
{
    public static string String(this JsonDocument document, string property) =>
        document.RootElement.GetProperty(property).GetString()!;
}
