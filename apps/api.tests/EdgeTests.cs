using System.Net;
using System.Text.Json;
using Ari.Platform;
using Ari.Platform.Crypto;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace AriApi.Tests;

/// <summary>
/// Builds the identical server on real sockets with its own config, exactly as
/// production, then checks the edge rules the smoke test also covers.
/// </summary>
public sealed class EdgeTests : IAsyncLifetime
{
    private WebApplication? _app;
    private int _publicPort;
    private int _adminPort;

    public async Task InitializeAsync()
    {
        _publicPort = FreePort();
        _adminPort = FreePort();
        var internalPort = FreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), "ari-tests-" + Guid.NewGuid().ToString("N")[..8]);
        var env = new Env(
            publicOrigin: $"http://localhost:{_publicPort}",
            adminOrigin: $"http://localhost:{_adminPort}",
            adminPort: _adminPort,
            publicPort: _publicPort,
            internalPort: internalPort,
            webOrigin: "http://127.0.0.1:1", // nothing listens: page proxy must 502, not hang
            dataDir: dataDir,
            mediaDir: Path.Combine(dataDir, "media"),
            keys: KeyRing.Load(null, $"test:{Convert.ToBase64String(new byte[32])}", production: false),
            trustedProxies: new List<(IPAddress, int)>(),
            production: false,
            webauthnRpId: null,
            geoDbPath: null);

        _app = ApiHost.Build(env, configure: null, map: null);
        _ = _app.RunAsync();
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = await probe.GetAsync($"http://localhost:{_publicPort}/healthz");
                if (response.StatusCode == HttpStatusCode.OK) break;
            }
            catch { }
            await Task.Delay(100);
        }
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private async Task<HttpStatusCode> Get(int port, string path)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await client.GetAsync($"http://localhost:{port}{path}");
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
    }

    [Fact]
    public async Task Healthz_exact_only()
    {
        Assert.Equal(HttpStatusCode.OK, await Get(_publicPort, "/healthz"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/healthz?x=1"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/HEALTHZ"));
        // HttpClient unescapes %68 before sending, so encoded spellings go over a raw
        // socket — the same way curl --path-as-is checks them.
        Assert.Equal("404", await RawStatus(_publicPort, "/%68ealthz"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/healthz/"));
    }

    private static async Task<string> RawStatus(int port, string rawPath)
    {
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        using var stream = socket.GetStream();
        var request = $"GET {rawPath} HTTP/1.1\r\nHost: localhost:{port}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream);
        var statusLine = await reader.ReadLineAsync();
        return statusLine?.Split(' ')[1] ?? "none";
    }

    [Fact]
    public async Task Admin_paths_refused_on_public_surface()
    {
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/admin"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/ADMIN"));
        // Raw-socket checks: HttpClient normalizes these spellings before sending.
        Assert.Equal("404", await RawStatus(_publicPort, "/%61dmin"));
        Assert.Equal("404", await RawStatus(_publicPort, "//admin"));
        Assert.Equal("404", await RawStatus(_publicPort, "/x/../admin"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/api/admin/users"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/api/auth/me"));
    }

    [Fact]
    public async Task Internal_paths_refused_outside_internal_surface()
    {
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/internal/content"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_publicPort, "/%69nternal/content"));
        Assert.Equal(HttpStatusCode.NotFound, await Get(_adminPort, "/internal/content"));
    }

    [Fact]
    public async Task Security_headers_on_every_response()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"http://localhost:{_publicPort}/admin");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("camera=(), microphone=(), geolocation=()", response.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task Admin_surface_noindex_and_robots()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var robots = await client.GetAsync($"http://localhost:{_adminPort}/robots.txt");
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        Assert.Equal("Disallow: /\n", await robots.Content.ReadAsStringAsync());
        Assert.Equal("noindex, nofollow", robots.Headers.GetValues("X-Robots-Tag").Single());
    }

    [Fact]
    public async Task Api_health_reports_ok()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"http://localhost:{_publicPort}/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Page_proxy_answers_502_when_web_is_down()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        using var response = await client.GetAsync($"http://localhost:{_publicPort}/");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Admin_surface_root_passes_the_edge_and_reaches_the_proxy()
    {
        // "/" is the admin shell, rewritten to /admin; with web down it answers 502 —
        // not 404, which would mean the edge refused the path.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        using var response = await client.GetAsync($"http://localhost:{_adminPort}/");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
