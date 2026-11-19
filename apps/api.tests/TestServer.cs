using System.Net;
using System.Text;
using System.Text.Json;
using Ari.Features.Auth;
using Ari.Platform;
using Ari.Platform.Crypto;
using Ari.Platform.Database;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AriApi.Tests;

/// <summary>
/// The identical server Program runs, assembled the same way: features discovered by
/// reflection, real sockets in the 810x range, own /tmp/auth-wt data dir. Cookies are
/// handled by hand so the exact names, paths and attributes stay visible to assertions.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    public WebApplication App = null!;
    public Env Env = null!;
    public Db Db = null!;
    public PasswordHasher Hasher = null!;

    public int AdminPort;
    public int PublicPort;
    public int InternalPort;

    public string AdminOrigin => $"http://localhost:{AdminPort}";
    public string PublicOrigin => $"http://localhost:{PublicPort}";

    public static async Task<TestServer> Start(string name, bool rateLimitsDisabled)
    {
        var held = HoldPorts(3);
        var taken = held.Keys.OrderBy(p => p).ToArray();
        var (admin, publicPort, internalPort) = (taken[0], taken[1], taken[2]);
        var dataDir = $"/tmp/auth-wt/{name}-{Guid.NewGuid():N}";
        Directory.CreateDirectory(dataDir);
        var env = new Env(
            publicOrigin: $"http://localhost:{publicPort}",
            adminOrigin: $"http://localhost:{admin}",
            adminPort: admin,
            publicPort: publicPort,
            internalPort: internalPort,
            webOrigin: "http://127.0.0.1:1",
            dataDir: dataDir,
            mediaDir: Path.Combine(dataDir, "media"),
            keys: KeyRing.Load(null, "test:" + Convert.ToBase64String(new byte[32]), production: false),
            trustedProxies: new List<(IPAddress, int)>(),
            production: false,
            webauthnRpId: "localhost",
            geoDbPath: null);

        var previous = Environment.GetEnvironmentVariable("ARI_DISABLE_RATE_LIMITS");
        Environment.SetEnvironmentVariable("ARI_DISABLE_RATE_LIMITS", rateLimitsDisabled ? "1" : null);
        TestServer server;
        try
        {
            var features = typeof(ProgramMarker).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(IFeature).IsAssignableFrom(t))
                .Select(t => (IFeature)Activator.CreateInstance(t)!)
                .OrderBy(f => f.Order)
                .ToList();
            var app = ApiHost.Build(env,
                configure: services =>
                {
                    foreach (var feature in features) feature.Register(services);
                },
                map: mapped =>
                {
                    foreach (var feature in features) feature.Map(mapped);
                });
            server = new TestServer
            {
                App = app,
                Env = env,
                Db = app.Services.GetRequiredService<Db>(),
                Hasher = app.Services.GetRequiredService<PasswordHasher>(),
                AdminPort = admin,
                PublicPort = publicPort,
                InternalPort = internalPort,
            };
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARI_DISABLE_RATE_LIMITS", previous);
            foreach (var listener in held.Values) listener.Stop();
        }

        _ = server.App.RunAsync();
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var response = await probe.GetAsync($"http://localhost:{publicPort}/healthz");
                if (response.StatusCode == HttpStatusCode.OK) return server;
            }
            catch { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Server did not come up.");
    }

    private static Dictionary<int, System.Net.Sockets.TcpListener> HoldPorts(int count)
    {
        var held = new Dictionary<int, System.Net.Sockets.TcpListener>();
        for (var port = 8100; port <= 8109 && held.Count < count; port++)
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                held[port] = listener;
            }
            catch (System.Net.Sockets.SocketException) { }
        }
        if (held.Count < count)
        {
            foreach (var listener in held.Values) listener.Stop();
            throw new InvalidOperationException("Not enough free ports in 8100-8109.");
        }
        return held;
    }

    public async ValueTask DisposeAsync() => await App.DisposeAsync();

    // ---- seeding -----------------------------------------------------------

    public const string DefaultPassword = "staple-horseplay-ninth-quartz";

    public long CreateUser(string email, string? password = DefaultPassword, string role = "staff",
        string permissionsJson = "[]", bool active = true)
    {
        using var connection = Db.App();
        return Db.Write(connection, transaction => connection.QuerySingle<long>(
            """
            INSERT INTO users(email, name, role, permissions, active, password_hash, created_at)
            VALUES(@email, @name, @role, @permissions, @active, @hash, @now)
            RETURNING id;
            """,
            new
            {
                email,
                name = email.Split('@')[0],
                role,
                permissions = permissionsJson,
                active = active ? 1 : 0,
                hash = password is null ? null : Hasher.Hash(password),
                now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, transaction));
    }

    public T Query<T>(string sql, object? parameters = null)
    {
        using var connection = Db.App();
        return connection.QuerySingle<T>(sql, parameters);
    }

    public System.Collections.Generic.IReadOnlyList<T> QueryList<T>(string sql, object? parameters = null)
    {
        using var connection = Db.App();
        return connection.Query<T>(sql, parameters).ToList();
    }

    public int Execute(string sql, object? parameters = null)
    {
        using var connection = Db.App();
        return connection.Execute(sql, parameters);
    }

    // ---- http ---------------------------------------------------------------

    /// <summary>A hand-rolled cookie jar: Set-Cookie headers in, Cookie headers out,
    /// honouring each cookie's path, exactly like a browser would.</summary>
    public sealed class Jar
    {
        private readonly Dictionary<string, (string Value, string Path)> _cookies = new(StringComparer.Ordinal);

        public void Store(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;
            foreach (var header in cookies)
            {
                var parts = header.Split(';');
                var pair = parts[0].Split('=', 2);
                if (pair.Length != 2) continue;
                var path = "/";
                foreach (var attribute in parts[1..])
                {
                    var split = attribute.Split('=', 2);
                    if (split[0].Trim().Equals("Path", StringComparison.OrdinalIgnoreCase) && split.Length == 2)
                        path = split[1].Trim();
                }
                if (pair[1].Length == 0) _cookies.Remove(pair[0].Trim());
                else _cookies[pair[0].Trim()] = (pair[1], path);
            }
        }

        public string? this[string name] => _cookies.TryGetValue(name, out var cookie) ? cookie.Value : null;

        /// <summary>Plants a cookie by hand, for replaying captured tokens.</summary>
        public void Set(string name, string value, string path) => _cookies[name] = (value, path);

        public void Clear() => _cookies.Clear();

        public void Apply(HttpRequestMessage request, string path)
        {
            var matching = _cookies
                .Where(pair => path == pair.Value.Path || (pair.Value.Path != "/" && path.StartsWith(pair.Value.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
                    || (pair.Value.Path == "/" && path.StartsWith('/')))
                .Select(pair => pair.Key + "=" + pair.Value.Value)
                .ToList();
            if (matching.Count > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", matching));
        }
    }

    public async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Message)> Send(
        string method,
        string path,
        string? json = null,
        Jar? jar = null,
        string? origin = null,
        bool browserFetch = true,
        int? port = null)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(new HttpMethod(method), $"http://localhost:{port ?? AdminPort}{path}");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        else if (browserFetch) request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        jar?.Apply(request, path);
        using var response = await client.SendAsync(request);
        jar?.Store(response);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body, response);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Message)> SendJson(
        string method, string path, string? json = null, Jar? jar = null, string? origin = null, int? port = null)
    {
        var (status, body, message) = await Send(method, path, json, jar, origin, port: port);
        var element = body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
        return (status, element, message);
    }
}
