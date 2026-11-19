using Ari.Features.Auth;
using Ari.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ari.Features;

/// <summary>
/// The integrator's port wiring, in one file: the auth area's session check implements
/// the guard interface the content and media endpoints were built against. Order 15 puts
/// it after content (10) so this registration wins over the no-op default.
/// </summary>
public sealed class WiringFeature : IFeature
{
    public int Order => 15;

    public void Register(IServiceCollection services)
    {
        services.TryAddTransient<StaffResolver>();
        services.AddSingleton<Content.IRouteGuards>(sp => new AuthRouteGuards(
            sp.GetRequiredService<StaffResolver>(),
            sp.GetRequiredService<AuthOptions>()));
    }

    public void Map(WebApplication app) { }

    /// <summary>
    /// IRouteGuards over the auth session resolution. Refusals write the real error
    /// (401 not signed in, 403 lacking the permission) so the SPA's refresh flow works.
    /// </summary>
    private sealed class AuthRouteGuards(StaffResolver resolver, AuthOptions options) : Content.IRouteGuards
    {
        public bool RequireStaff(HttpContext context) => Resolve(context) is not null;

        public bool RequirePermission(HttpContext context, string permission)
        {
            var staff = Resolve(context);
            return staff is not null && staff.HasPermission(permission);
        }

        private StaffContext? Resolve(HttpContext context)
        {
            var staff = context.Staff();
            if (staff is not null)
            {
                context.Items["Ari.Actor"] = staff.UserId;
                return staff;
            }

            staff = resolver.Resolve(context).GetAwaiter().GetResult();
            if (staff is null)
            {
                CookieJar.WipeAccess(context, options);
                Write(context, 401, "not_signed_in", "Sign in required.");
                return null;
            }
            context.Items[Guards.StaffItemKey] = staff;
            context.Items["Ari.Actor"] = staff.UserId;
            return staff;
        }

        private static void Write(HttpContext context, int status, string code, string message)
        {
            if (context.Response.HasStarted) return;
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            _ = context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = message, code }));
        }
    }
}
