using System.Net;
using Ari.Platform.Api;
using Ari.Platform.Crypto;
using Ari.Platform.Database;
using Ari.Platform.Edge;
using Ari.Platform.Jobs;
using Ari.Platform.Outbox;
using Ari.Platform.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace Ari.Platform;

/// <summary>Everything shared that features reach for, resolvable from DI.</summary>
public sealed record ApiContext(Env Env, Db Db, Sealed Sealed, TokenIssuer Tokens, RateLimits Limits, MailQueue Queue);

public sealed class ApiHost
{
    /// <summary>
    /// Assembles the identical server the production Program runs, so tests build it on
    /// real sockets with their own config and fakes. Feature areas attach through
    /// <paramref name="configure"/> (services) and <paramref name="map"/> (endpoints).
    /// </summary>
    public static WebApplication Build(
        Env env,
        Action<IServiceCollection>? configure = null,
        Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        var db = new Db(env.DataDir);
        var sealedValues = new Sealed(env.Keys);
        var tokens = new TokenIssuer(env.Keys);
        var limits = new RateLimits(disabled: !env.Production
            && Environment.GetEnvironmentVariable("ARI_DISABLE_RATE_LIMITS") == "1");
        var outbox = new MailQueue(db);
        var context = new ApiContext(env, db, sealedValues, tokens, limits, outbox);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;

            void Listen(int port, Surface surface)
            {
                kestrel.Listen(IPAddress.Any, port, listen =>
                {
                    RejectionHeaders.Use(listen);
                    listen.Use(next => async (Microsoft.AspNetCore.Connections.ConnectionContext connection) =>
                    {
                        connection.Stamp(surface);
                        await next(connection);
                    });
                });
            }

            Listen(env.PublicPort, env.SeparateAdmin ? Surface.Public : Surface.Combined);
            if (env.SeparateAdmin && env.AdminPort is int admin) Listen(admin, Surface.Admin);
            Listen(env.InternalPort, Surface.Internal);
        });

        // Defaults that feature areas may replace by registering later.
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton<Env>(env);
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(sealedValues);
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(limits);
        builder.Services.AddSingleton<IOutbox>(outbox);
        builder.Services.AddSingleton(outbox);
        builder.Services.AddSingleton<MailSender>();
        builder.Services.AddSingleton<ISmtpConfig>(NullSmtpConfig.Instance);
        builder.Services.AddSingleton<IRedirectSource>(NullRedirectSource.Instance);
        builder.Services.AddSingleton<IPageHitCounter>(NullPageHitCounter.Instance);
        builder.Services.AddSingleton<IHealthReporter>(BasicHealthReporter.Instance);
        builder.Services.AddHttpForwarder();
        builder.Services.AddSingleton<PageProxy>();

        configure?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseMiddleware<EdgeMiddleware>();
        app.UseWhen(
            c => c.Request.Path.StartsWithSegments("/api"),
            branch => branch.UseMiddleware<ApiMiddleware>());

        map?.Invoke(app);

        app.MapGet("/api/health", (IHealthReporter health) => Results.Json(health.Report()));

        // Pages go to the web container last: anything not matched above is proxied.
        app.MapFallback(PageProxyRoute);

        var logger = app.Services.GetRequiredService<ILogger<ProgramMarker>>();
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            _ = ShutdownHooks.RunAll(logger);
        });

        return app;

        async Task PageProxyRoute(HttpContext http)
        {
            var surface = http.Get();
            if (surface == Surface.Internal)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var proxy = http.RequestServices.GetRequiredService<PageProxy>();
            await proxy.Invoke(http);
        }
    }
}

public sealed record ProgramMarker;

/// <summary>
/// Each feature area implements this and is discovered by reflection, so builders only
/// ever add files under Features/ and never touch Program.cs. Order decides registration
/// order of hosted services: content first, then the rest.
/// </summary>
public interface IFeature
{
    /// <summary>Lower registers and starts earlier. 10-content, 20-auth, 30-analytics, 40-github, 50-media, 60-messages, 70-settings, 80-users, 90-audit, 100-admin.</summary>
    int Order { get; }

    void Register(IServiceCollection services);

    void Map(WebApplication app);
}

/// <summary>A CLI command, discovered by reflection so feature areas own their own.</summary>
public interface ICliMode
{
    /// <summary>The first positional argument that selects this mode.</summary>
    string Name { get; }

    Task<int> Run(Env env, string[] args);
}

/// <summary>Health is reported by whichever feature owns the content snapshot.</summary>
public interface IHealthReporter
{
    object Report();
}

public sealed class BasicHealthReporter : IHealthReporter
{
    public static readonly BasicHealthReporter Instance = new();
    public object Report() => new { status = "ok" };
}

public sealed class NullSmtpConfig : ISmtpConfig
{
    public static readonly NullSmtpConfig Instance = new();
    public (string Host, int Port, string User, string Password, string FromAddress, string FromName, bool StartTls)? Read() => null;
}

public sealed class NullRedirectSource : IRedirectSource
{
    public static readonly NullRedirectSource Instance = new();
    public string? TargetFor(string canonicalPath) => null;
}

public sealed class NullPageHitCounter : IPageHitCounter
{
    public static readonly NullPageHitCounter Instance = new();
    public void Count(string path) { }
}
