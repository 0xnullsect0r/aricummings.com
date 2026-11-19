using System.Net;
using Ari.Platform.Crypto;

namespace Ari.Platform;

/// <summary>All configuration, read once at startup. A bad setting stops the boot.</summary>
public sealed class Env
{
    public string PublicOrigin { get; private init; } = null!;
    public string AdminOrigin { get; private init; } = null!;
    public int? AdminPort { get; private init; }
    public int PublicPort { get; private init; }
    public int InternalPort { get; private init; }
    public string WebOrigin { get; private init; } = null!;
    public string DataDir { get; private init; } = null!;
    public string MediaDir { get; private init; } = null!;
    public KeyRing Keys { get; private init; } = null!;
    public List<(IPAddress Address, int Prefix)> TrustedProxies { get; private init; } = null!;
    public bool Production { get; private init; }
    public string? WebauthnRpId { get; private init; }
    public string? GeoDbPath { get; private init; }

    public bool SeparateAdmin => AdminPort is not null;

    /// <summary>Tests build the same configuration directly, without environment variables.</summary>
    internal Env(
        string publicOrigin,
        string adminOrigin,
        int? adminPort,
        int publicPort,
        int internalPort,
        string webOrigin,
        string dataDir,
        string mediaDir,
        KeyRing keys,
        List<(IPAddress Address, int Prefix)> trustedProxies,
        bool production,
        string? webauthnRpId,
        string? geoDbPath)
    {
        PublicOrigin = publicOrigin;
        AdminOrigin = adminOrigin;
        AdminPort = adminPort;
        PublicPort = publicPort;
        InternalPort = internalPort;
        WebOrigin = webOrigin;
        DataDir = dataDir;
        MediaDir = mediaDir;
        Keys = keys;
        TrustedProxies = trustedProxies;
        Production = production;
        WebauthnRpId = webauthnRpId;
        GeoDbPath = geoDbPath;
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(mediaDir);
    }

    private Env() { }

    public static Env Load()
    {
        var production = Environment.GetEnvironmentVariable("ARI_ENV") == "production";

        string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Missing required setting {name}.");
            return value;
        }

        var publicOrigin = NormalizeOrigin(Required("PUBLIC_ORIGIN"));
        var adminPort = Environment.GetEnvironmentVariable("ADMIN_PORT");
        var adminOriginRaw = Environment.GetEnvironmentVariable("ADMIN_ORIGIN")
            ?? (adminPort is null ? null : Required("ADMIN_ORIGIN"));

        // Test flags must never survive into production.
        if (production)
        {
            foreach (var flag in new[] { "ARI_INSECURE_COOKIES", "ARI_DISABLE_RATE_LIMITS", "ARI_TEST_AUTH_BYPASS", "ARI_TEST_ANALYTICS_ALL" })
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(flag)))
                    throw new InvalidOperationException($"{flag} is set; refusing to start in production.");
        }

        var dataDir = Environment.GetEnvironmentVariable("ARI_DATA_DIR") ?? "data";
        Directory.CreateDirectory(dataDir);
        var mediaDir = Path.Combine(dataDir, "media");
        Directory.CreateDirectory(mediaDir);

        var keys = KeyRing.Load(
            Environment.GetEnvironmentVariable("APP_KEY_FILE"),
            Environment.GetEnvironmentVariable("APP_KEYS"),
            production);

        List<(IPAddress, int)> proxies = new();
        var cidrs = Environment.GetEnvironmentVariable("TRUSTED_PROXY_CIDRS") ?? "";
        foreach (var raw in cidrs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
                throw new InvalidOperationException($"TRUSTED_PROXY_CIDRS entry '{raw}' is not a full CIDR (e.g. 10.0.0.0/8). Refusing to start.");
            if (prefix < 0 || prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32))
                throw new InvalidOperationException($"TRUSTED_PROXY_CIDRS entry '{raw}' has an out-of-range prefix.");
            proxies.Add((address, prefix));
        }

        return new Env
        {
            Production = production,
            PublicOrigin = publicOrigin,
            PublicPort = int.Parse(Environment.GetEnvironmentVariable("ARI_PUBLIC_PORT") ?? "8080"),
            AdminPort = adminPort is null || adminPort.Length == 0 ? null : int.Parse(adminPort),
            AdminOrigin = adminOriginRaw is null ? publicOrigin : NormalizeOrigin(adminOriginRaw),
            InternalPort = int.Parse(Environment.GetEnvironmentVariable("ARI_INTERNAL_PORT") ?? "8082"),
            WebOrigin = Environment.GetEnvironmentVariable("ARI_WEB_ORIGIN") ?? "http://127.0.0.1:3000",
            DataDir = dataDir,
            MediaDir = mediaDir,
            Keys = keys,
            TrustedProxies = proxies,
            WebauthnRpId = Environment.GetEnvironmentVariable("WEBAUTHN_RP_ID"),
            GeoDbPath = Environment.GetEnvironmentVariable("ARI_GEODB_PATH"),
        };
    }

    /// <summary>Parses an origin to scheme, host and port so comparisons never trip on trailing slashes or default ports.</summary>
    public static (string Scheme, string Host, int Port) ParseOrigin(string origin)
    {
        var value = origin.TrimEnd('/');
        var scheme = value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var rest = value[(scheme.Length + 3)..];
        var host = rest;
        int port = scheme == "https" ? 443 : 80;
        if (rest.Contains(':'))
        {
            var colon = rest.LastIndexOf(':');
            host = rest[..colon];
            port = int.Parse(rest[(colon + 1)..]);
        }
        return (scheme, host.ToLowerInvariant(), port);
    }

    private static string NormalizeOrigin(string origin) => origin.TrimEnd('/');
}
