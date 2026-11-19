using Ari.Platform.Api;

namespace Ari.Features.Auth;

/// <summary>
/// Route filters every admin endpoint attaches. RequireStaff loads the session and
/// user from the database on the way in; the rest narrow it. 401 means sign in (the
/// SPA tries refresh once), 403 means signed in but not allowed, and step-up answers
/// 403 with code step_up_required when the identity proof is older than 10 minutes.
/// </summary>
public static class Guards
{
    public const string StaffItemKey = "Ari.Staff";

    /// <summary>The resolved staff context, set by RequireStaff. Null when the route has none.</summary>
    public static StaffContext? Staff(this HttpContext http) =>
        http.Items.TryGetValue(StaffItemKey, out var value) && value is StaffContext staff ? staff : null;

    public static IEndpointFilter RequireStaff { get; } = new StaffFilter();
    public static IEndpointFilter RequireOwner { get; } = new OwnerFilter();
    public static IEndpointFilter RequireStepUp { get; } = new StepUpFilter();

    /// <summary>RequireStaff plus one named permission; owners hold everything implicitly.</summary>
    public static IEndpointFilter RequirePermission(string permission) => new PermissionFilter(permission);

    private static async Task<StaffContext> Resolve(HttpContext http)
    {
        var existing = http.Staff();
        if (existing is not null) return existing;

        var resolver = http.RequestServices.GetRequiredService<StaffResolver>();
        var options = http.RequestServices.GetRequiredService<AuthOptions>();
        var staff = await resolver.Resolve(http);
        if (staff is null)
        {
            // A refused session must leave no cookies behind; keep the refresh cookie so
            // the SPA's single retry can still recover from an expired access token.
            CookieJar.WipeAccess(http, options);
            throw new ApiException(401, "Sign in required.", "not_signed_in");
        }
        http.Items[StaffItemKey] = staff;
        return staff;
    }

    private sealed class StaffFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            await Resolve(context.HttpContext);
            return await next(context);
        }
    }

    private sealed class OwnerFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var staff = await Resolve(context.HttpContext);
            if (!staff.IsOwner) throw new ApiException(403, "Owners only.", "forbidden");
            return await next(context);
        }
    }

    private sealed class PermissionFilter(string permission) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var staff = await Resolve(context.HttpContext);
            if (!staff.HasPermission(permission)) throw new ApiException(403, "You do not have that permission.", "forbidden");
            return await next(context);
        }
    }

    private sealed class StepUpFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var staff = await Resolve(context.HttpContext);
            if (!staff.StepUpFresh(SessionService.Now()))
                throw new ApiException(403, "Confirm your identity again to continue.", "step_up_required");
            return await next(context);
        }
    }
}

/// <summary>The flat permission dictionary for this site, in display order. Owners hold
/// everything implicitly; these are what staff accounts can be granted.</summary>
public static class Permissions
{
    public static readonly (string Key, string Description)[] All =
    {
        ("content.edit", "Edit drafts and content"),
        ("content.publish", "Publish content live"),
        ("media.manage", "Upload and manage media"),
        ("messages.read", "Read the message inbox"),
        ("messages.manage", "Archive and delete messages"),
        ("analytics.read", "View statistics"),
        ("analytics.export", "Export analytics data"),
        ("settings.manage", "Change site settings"),
        ("audit.read", "Read the audit log"),
        ("users.manage", "Manage staff users"),
    };

    public static IReadOnlyList<string> Keys => All.Select(p => p.Key).ToArray();

    public static bool IsKnown(string key) => All.Any(p => p.Key == key);
}
