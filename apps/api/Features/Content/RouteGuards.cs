using Ari.Platform.Api;
using Microsoft.AspNetCore.Http;

namespace Ari.Features.Content;

/// <summary>
/// Route guards for the content and media endpoints. The auth area implements this with the
/// real session check and registers its implementation after the content feature, so it wins
/// in DI; until then the no-op below lets a bare server run (dev and tests).
///
/// Implementations stamp the acting user's id on <see cref="HttpContext.Items"/> under
/// "Ari.Actor" (long?) and write their own 401/403 response when they refuse.
/// </summary>
public interface IRouteGuards
{
    /// <summary>True when the caller is signed in as staff.</summary>
    bool RequireStaff(HttpContext context);

    /// <summary>True when the caller holds the given permission (owners hold all).</summary>
    bool RequirePermission(HttpContext context, string permission);
}

/// <summary>Allows everything and stamps a null actor. Replaced by the auth area.</summary>
public sealed class NoopRouteGuards : IRouteGuards
{
    public static readonly NoopRouteGuards Instance = new();

    public bool RequireStaff(HttpContext context)
    {
        context.Items["Ari.Actor"] = null;
        return true;
    }

    public bool RequirePermission(HttpContext context, string permission)
    {
        context.Items["Ari.Actor"] = null;
        return true;
    }
}

public static class RouteGuardExtensions
{
    /// <summary>Refused calls that the guard did not already answer get the one error shape.</summary>
    public static bool Allowed(this IRouteGuards guards, HttpContext context, string permission)
    {
        if (guards.RequirePermission(context, permission)) return true;
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            _ = context.Response.WriteAsync("""{"error":"forbidden","code":"forbidden"}""");
        }
        return false;
    }

    public static long? Actor(this HttpContext context) => context.Items.TryGetValue("Ari.Actor", out var value) && value is long id ? id : null;
}
