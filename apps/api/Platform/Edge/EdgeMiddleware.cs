using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace Ari.Platform.Edge;

/// <summary>Ports the rest of the app implements for the edge.</summary>
public interface IRedirectSource
{
    /// <summary>Redirect target for a canonical path, or null.</summary>
    string? TargetFor(string canonicalPath);
}

/// <summary>Counts a page hit for the daily total before anything can filter it.</summary>
public interface IPageHitCounter
{
    void Count(string path);
}

/// <summary>
/// Runs first on every request. Security headers, raw-target rules, forwarded-header
/// rewriting, surface routing, path refusal and page-hit counting all live here so
/// nothing can arrive past a rule by missing a middleware.
/// </summary>
public sealed class EdgeMiddleware(
    RequestDelegate next,
    Env env,
    IRedirectSource redirects,
    IPageHitCounter hits)
{
    private static readonly string[] StripAlways =
    {
        "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port",
        "X-Real-IP", "Forwarded", "X-Site-Surface", "x-middleware-subrequest",
    };

    public async Task Invoke(HttpContext context)
    {
        var response = context.Response;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        var feature = context.Features.Get<IHttpRequestFeature>();
        var rawTarget = feature?.RawTarget ?? context.Request.Path.Value ?? "/";
        var rawPathOnly = rawTarget.Split('?', '#')[0];

        // Exactly /healthz: no query, no case variants, no extra segments. The web
        // container's own health route stays private behind this exact spelling.
        if (rawTarget.Equals("/healthz", StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/plain";
            await response.WriteAsync("ok");
            return;
        }
        if (rawPathOnly.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        RewriteForwarded(context);

        var canonical = Paths.Canonical(rawTarget);
        var surface = context.Get();

        if (canonical is null)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Encoded spellings of /healthz (e.g. /%68ealthz) resolve here and must also
        // refuse; the exact literal was already answered above.
        if (canonical == "/healthz")
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The internal surface serves internal paths only; everywhere else refuses them,
        // checked on both the raw and the decoded spelling.
        var isInternalPath = canonical.StartsWith("/internal/", StringComparison.Ordinal) || canonical == "/internal";
        if (surface == Surface.Internal)
        {
            if (!isInternalPath)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        else if (isInternalPath || RawLooksInternal(rawPathOnly))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        switch (surface)
        {
            case Surface.Public:
                await ApplyPublic(context, canonical);
                return;
            case Surface.Admin:
                await ApplyAdmin(context, canonical);
                return;
            case Surface.Internal:
                // Only internal paths reach here (refused above); endpoints serve them.
                await next(context);
                return;
            case Surface.Combined:
                CountHit(context, canonical);
                await next(context);
                return;
            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private async Task ApplyPublic(HttpContext context, string canonical)
    {
        var response = context.Response;
        // Staff routes never exist on the public surface, before routing.
        if (canonical.StartsWith("/admin", StringComparison.Ordinal)
            || canonical.StartsWith("/api/admin", StringComparison.Ordinal)
            || canonical.StartsWith("/api/auth", StringComparison.Ordinal))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (context.Request.Method == HttpMethods.Get
            && !canonical.StartsWith("/api/", StringComparison.Ordinal)
            && !canonical.StartsWith("/media/", StringComparison.Ordinal))
        {
            var target = redirects.TargetFor(canonical);
            if (target is not null)
            {
                response.StatusCode = StatusCodes.Status301MovedPermanently;
                response.Headers.Location = target;
                response.Headers.CacheControl = "no-store";
                return;
            }
        }

        CountHit(context, canonical);
        await next(context);
    }

    private async Task ApplyAdmin(HttpContext context, string canonical)
    {
        var response = context.Response;
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        if (canonical == "/robots.txt")
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/plain";
            await response.WriteAsync("Disallow: /\n");
            return;
        }

        // Only what the admin needs passes; customer APIs and any other file stay 404 here.
        // "/" is the shell itself, rewritten below to /admin.
        var allowed =
            canonical == "/"
            || canonical.StartsWith("/admin", StringComparison.Ordinal)
            || canonical.StartsWith("/api/admin", StringComparison.Ordinal)
            || canonical.StartsWith("/api/auth", StringComparison.Ordinal)
            || canonical.StartsWith("/_next/", StringComparison.Ordinal)
            || canonical.StartsWith("/media/", StringComparison.Ordinal)
            || IsAdminAsset(canonical);
        if (!allowed)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Clean URLs: /users is rewritten to /admin/users so the address bar stays clean.
        // PathString.Add keeps the value single-decoded.
        var path = context.Request.Path;
        var pathValue = path.Value ?? "/";
        if (!pathValue.StartsWith("/admin", StringComparison.Ordinal)
            && !pathValue.StartsWith("/api", StringComparison.Ordinal)
            && !pathValue.StartsWith("/_next", StringComparison.Ordinal)
            && !pathValue.StartsWith("/media", StringComparison.Ordinal)
            && !IsAdminAsset(canonical))
        {
            context.Request.Path = new PathString("/admin").Add(path);
        }

        await next(context);
    }

    private static bool IsAdminAsset(string canonical) =>
        canonical is "/favicon.ico" or "/favicon.svg" or "/apple-touch-icon.png" or "/icon.png"
        || canonical.StartsWith("/icons/", StringComparison.Ordinal);

    private void CountHit(HttpContext context, string canonical)
    {
        if (context.Request.Method != HttpMethods.Get) return;
        if (canonical.StartsWith("/api/", StringComparison.Ordinal)) return;
        if (canonical.StartsWith("/media/", StringComparison.Ordinal)) return;
        if (canonical.StartsWith("/_next/", StringComparison.Ordinal)) return;
        if (canonical.StartsWith("/admin", StringComparison.Ordinal)) return;
        hits.Count(canonical);
    }

    private void RewriteForwarded(HttpContext context)
    {
        var request = context.Request;
        var peer = context.Connection.RemoteIpAddress;

        // Read the proxy's client IP before stripping, and only when the peer is trusted.
        var forwarded = request.Headers["X-Forwarded-For"].ToString();
        var trusted = peer is not null && env.TrustedProxies.Any(cidr =>
            cidr.Address.AddressFamily == peer.AddressFamily
            && BytesEqual(cidr.Address.GetAddressBytes(), Mask(peer.GetAddressBytes(), cidr.Prefix)));

        string? clientIp = null;
        if (trusted)
        {
            var first = forwarded.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var parsed)) clientIp = parsed.ToString();
        }

        request.Headers.Remove("X-Forwarded-For");
        foreach (var header in StripAlways) request.Headers.Remove(header);

        if (clientIp is not null) request.Headers["X-Forwarded-For"] = clientIp;
        else if (peer is not null) request.Headers["X-Forwarded-For"] = peer.ToString();

        var origin = context.Get() == Surface.Admin ? env.AdminOrigin : env.PublicOrigin;
        var (scheme, host, port) = Env.ParseOrigin(origin);
        request.Headers["X-Forwarded-Proto"] = scheme;
        request.Headers["X-Forwarded-Host"] = port is 443 or 80 ? host : $"{host}:{port}";

        request.Headers["X-Site-Surface"] = context.Get() switch
        {
            Surface.Admin => "admin",
            _ => "public",
        };
    }

    private static bool RawLooksInternal(string rawPath) =>
        rawPath.StartsWith("/internal", StringComparison.OrdinalIgnoreCase)
        || rawPath.Contains("%2finternal", StringComparison.OrdinalIgnoreCase)
        || rawPath.Contains("%2Finternal", StringComparison.OrdinalIgnoreCase);

    private static byte[] Mask(byte[] address, int prefix)
    {
        var masked = new byte[address.Length];
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes && i < masked.Length; i++) masked[i] = address[i];
        var remainder = prefix % 8;
        if (remainder > 0 && fullBytes < masked.Length)
            masked[fullBytes] = (byte)(address[fullBytes] & (byte)(0xFF << (8 - remainder) & 0xFF));
        return masked;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }
}
