using System.Text.Json;
using Ari.Platform.Edge;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;

namespace Ari.Platform.Api;

/// <summary>
/// The pipeline every /api request passes through, as a second layer behind the edge:
/// no-store, staff-route refusal on the router's view of the path, trailing-slash
/// redirect, literal-case refusal, cross-site write check, body limit, one error shape.
/// </summary>
public sealed class ApiMiddleware(RequestDelegate next, Env env, ILogger<ApiMiddleware> logger)
{
    private const long DefaultBodyLimit = 1024 * 1024; // 1 MiB

    public async Task Invoke(HttpContext context)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";

        var surface = context.Get();

        // The router's view of the path, checked again behind the edge.
        var path = context.Request.Path.Value ?? "/";
        if (surface == Surface.Public)
        {
            if (path.StartsWith("/api/admin", StringComparison.Ordinal) || path.StartsWith("/api/auth", StringComparison.Ordinal))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        // Refuse the wrong case on literal segments: area names are lowercase.
        var apiRemainder = path.StartsWith("/api", StringComparison.Ordinal) ? path[4..] : path;
        foreach (var segment in apiRemainder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Any(char.IsUpper))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        // Trailing slash on API paths: redirect to the canonical form.
        if (path.Length > 1 && path.EndsWith('/') && path.StartsWith("/api", StringComparison.Ordinal))
        {
            var trimmed = path.TrimEnd('/');
            response.StatusCode = StatusCodes.Status301MovedPermanently;
            response.Headers.Location = trimmed + (context.Request.QueryString.HasValue ? context.Request.QueryString.Value : "");
            return;
        }

        if (!await CrossSiteCheck(context, env, surface)) return;

        if (context.Request.ContentLength is > DefaultBodyLimit && !context.Items.ContainsKey("Ari.BodyLimit"))
        {
            response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await response.WriteAsJsonAsync(new { error = "Request body too large.", code = "body_too_large" });
            return;
        }

        try
        {
            await next(context);
        }
        catch (ApiException error)
        {
            if (response.HasStarted) return;
            response.StatusCode = error.Status;
            response.ContentType = "application/json";
            await response.WriteAsync(JsonSerializer.Serialize(new { error = error.Message, code = error.Code }));
        }
        catch (Exception error)
        {
            logger.LogError(error, "Unhandled error on {Method} {Path}", context.Request.Method, context.Request.Path);
            if (response.HasStarted) return;
            response.StatusCode = StatusCodes.Status500InternalServerError;
            response.ContentType = "application/json";
            await response.WriteAsync(JsonSerializer.Serialize(new { error = "Something went wrong", code = "internal" }));
        }
    }

    /// <summary>
    /// The public site and the admin are the same site to a browser; only the origin tells
    /// them apart. With an Origin header it must equal this surface's origin; without one,
    /// Sec-Fetch-Site must be same-origin. Neither means not from a page: refused.
    /// Webhooks are exempt; they verify a signature instead.
    /// </summary>
    private static async Task<bool> CrossSiteCheck(HttpContext context, Env env, Surface surface)
    {
        if (context.Request.Method == HttpMethods.Get
            || context.Request.Method == HttpMethods.Head
            || context.Request.Path.StartsWithSegments("/api/webhooks"))
            return true;

        var expected = surface == Surface.Admin ? env.AdminOrigin : env.PublicOrigin;
        var (scheme, host, port) = Env.ParseOrigin(expected);

        if (context.Request.Headers.Origin.Count > 0)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                && parsed.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase)
                && parsed.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                && parsed.Port == port)
                return true;

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":"cross_site","code":"cross_site"}""");
            return false;
        }

        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)) return true;

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":"cross_site","code":"cross_site"}""");
        return false;
    }
}
