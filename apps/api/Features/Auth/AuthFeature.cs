using Ari.Platform;
using Ari.Platform.Jobs;
using Ari.Platform.Outbox;
using Fido2NetLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ari.Features.Auth;

/// <summary>
/// Sign-in, second factors, sessions, passkeys, invites, resets and the account
/// routes. Also owns the shared job host: the outbox mail job runs every minute,
/// the pruner daily.
/// </summary>
public sealed class AuthFeature : IFeature
{
    public int Order => 20;

    public void Register(IServiceCollection services)
    {
        var options = AuthOptions.Load();
        services.AddSingleton(options);
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<StaffTokens>();
        services.AddSingleton<SealedValues>();
        services.AddSingleton(RateGate.Load());
        services.AddSingleton<SessionService>();
        services.AddSingleton<StaffResolver>();
        services.AddSingleton<SignInService>();
        services.AddSingleton<TotpService>();
        services.AddSingleton<LinkService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<PasskeyService>();
        services.AddSingleton<IFido2>(sp =>
        {
            var env = sp.GetRequiredService<Env>();
            return new Fido2NetLib.Fido2(new Fido2Configuration
            {
                ServerDomain = env.WebauthnRpId ?? Env.ParseOrigin(env.AdminOrigin).Host,
                ServerName = "aricummings.com",
                Origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { env.AdminOrigin },
            }, null!);
        });

        // The shared job host and its two platform jobs. Other areas register more
        // JobOptions; only this feature adds the hosted service.
        services.AddSingleton<OutboxJob>();
        services.AddSingleton<PruneJob>();
        services.AddSingleton(sp => new JobOptions(
            "outbox-mail", "* * * * *", null,
            ct => sp.GetRequiredService<OutboxJob>().Run(ct), Alert: true));
        services.AddSingleton(sp => new JobOptions(
            "prune", "17 3 * * *", null,
            ct => sp.GetRequiredService<PruneJob>().Run(ct), Alert: false));
        services.AddHostedService<JobHost>();
    }

    public void Map(WebApplication app)
    {
        app.MapPost("/api/auth/signin", async (HttpContext http, SignInService service) => await service.SignIn(http));
        app.MapPost("/api/auth/2fa", async (HttpContext http, SignInService service) => await service.SecondFactor(http));
        app.MapPost("/api/auth/signout", async (HttpContext http, SignInService service) => await service.SignOut(http));
        app.MapPost("/api/auth/stepup", async (HttpContext http, SignInService service) => await service.StepUp(http))
            .AddEndpointFilter(Guards.RequireStaff);
        app.MapPost("/api/auth/refresh", async (HttpContext http, SignInService service) => await service.Refresh(http));
        app.MapGet("/api/auth/me", async (HttpContext http, AccountService service) => await service.Me(http))
            .AddEndpointFilter(Guards.RequireStaff);

        // Passkey sign-in (usernameless, discoverable).
        app.MapPost("/api/auth/passkey/options", async (HttpContext http, PasskeyService service) => await service.SignInOptions(http));
        app.MapPost("/api/auth/passkey/verify", async (HttpContext http, PasskeyService service) => await service.SignInVerify(http));

        // Account: password.
        app.MapGet("/api/auth/account/password", (HttpContext http, AccountService service) => service.PasswordRulesGet(http))
            .AddEndpointFilter(Guards.RequireStaff);
        app.MapPut("/api/auth/account/password", async (HttpContext http, AccountService service) => await service.PasswordChange(http))
            .AddEndpointFilter(Guards.RequireStaff);

        // Account: TOTP and recovery codes.
        app.MapPost("/api/auth/account/totp/start", async (HttpContext http, AccountService service) => await service.TotpStart(http))
            .AddEndpointFilter(Guards.RequireStaff)
            .AddEndpointFilter(Guards.RequireStepUp);
        app.MapPost("/api/auth/account/totp/confirm", async (HttpContext http, AccountService service) => await service.TotpConfirm(http))
            .AddEndpointFilter(Guards.RequireStaff);
        app.MapGet("/api/auth/account/totp/recovery", async (HttpContext http, AccountService service) => await service.TotpRecovery(http))
            .AddEndpointFilter(Guards.RequireStaff)
            .AddEndpointFilter(Guards.RequireStepUp);

        // Account: sessions.
        app.MapGet("/api/auth/account/sessions", async (HttpContext http, AccountService service) => await service.SessionsList(http))
            .AddEndpointFilter(Guards.RequireStaff);
        app.MapDelete("/api/auth/account/sessions/{id}", async (HttpContext http, string id, AccountService service) => await service.SessionRevoke(http, id))
            .AddEndpointFilter(Guards.RequireStaff);

        // Account: passkeys.
        app.MapGet("/api/auth/account/passkeys", async (HttpContext http, PasskeyService service) => await service.List(http))
            .AddEndpointFilter(Guards.RequireStaff);
        app.MapPost("/api/auth/account/passkeys/register", async (HttpContext http, PasskeyService service) => await service.RegisterOptions(http))
            .AddEndpointFilter(Guards.RequireStaff)
            .AddEndpointFilter(Guards.RequireStepUp);
        app.MapPost("/api/auth/account/passkeys/register/verify", async (HttpContext http, PasskeyService service) => await service.RegisterVerify(http))
            .AddEndpointFilter(Guards.RequireStaff)
            .AddEndpointFilter(Guards.RequireStepUp);
        app.MapDelete("/api/auth/account/passkeys/{id}", async (HttpContext http, long id, PasskeyService service) => await service.Delete(http, id))
            .AddEndpointFilter(Guards.RequireStaff)
            .AddEndpointFilter(Guards.RequireStepUp);

        // Link endpoints: invite acceptance and self-service password reset.
        app.MapPost("/api/auth/invite/accept", async (HttpContext http, AccountService service) => await service.InviteAccept(http));
        app.MapPost("/api/auth/reset/request", async (HttpContext http, AccountService service) => await service.ResetRequest(http));
        app.MapPost("/api/auth/reset/confirm", async (HttpContext http, AccountService service) => await service.ResetConfirm(http));
    }
}
