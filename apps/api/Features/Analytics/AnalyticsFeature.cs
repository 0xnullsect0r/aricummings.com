using Ari.Platform;
using Ari.Platform.Edge;
using Ari.Platform.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ari.Features.Analytics;

/// <summary>
/// First-party analytics, Order 30. Discovered by reflection from Program; nothing
/// outside this folder is touched. Modules on per ledger (everything except
/// fingerprint and identity, which do not exist here).
/// </summary>
public sealed class AnalyticsFeature : IFeature
{
    public int Order => 30;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<AnalyticsClock>();
        services.AddSingleton<AnalyticsQueries>();
        services.AddSingleton<BotFilter>();
        services.AddSingleton<UserAgentParser>();
        // ARI_GEODB_PATH from Env: when the file is absent this is the null resolver and
        // every geo field stays null. Never a crash, never a third-party call.
        services.AddSingleton(sp => GeoResolver.Create(sp.GetRequiredService<Env>().GeoDbPath));
        services.AddSingleton<AnalyticsStore>();
        services.AddSingleton<IPageHitCounter>(sp => sp.GetRequiredService<AnalyticsStore>());
        services.AddSingleton<BeaconEndpoint>();
        services.AddSingleton<AnalyticsReports>();

        // The no-op guard loses to any earlier or explicit registration.
        services.TryAddSingleton<IAnalyticsGuard>(OpenAnalyticsGuard.Instance);

        services.AddSingleton<WeeklyEmailJob>();
        services.AddSingleton<RetentionJob>();
        services.AddSingleton(sp => new JobOptions(
            "analytics-weekly-email", "0 8 * * 1-3", ZoneId(),
            ct => sp.GetRequiredService<WeeklyEmailJob>().RunAsync(ct), Alert: true));
        services.AddSingleton(sp => new JobOptions(
            "analytics-retention", "30 3 * * *", ZoneId(),
            ct => sp.GetRequiredService<RetentionJob>().RunAsync(ct), Alert: true));

        // The shared Platform job host, registered once so features cannot double it.
        if (!services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobHost)))
            services.AddHostedService<JobHost>();

        // After the job host on purpose: hosted services stop in reverse, so the analytics
        // writer flushes and stops before jobs that may still read or enqueue events, and
        // the ShutdownHooks drain runs after both.
        services.AddHostedService(sp => sp.GetRequiredService<AnalyticsStore>());
    }

    public void Map(WebApplication app)
    {
        app.MapPost("/api/analytics/beacon", (HttpRequest request) => BeaconEndpoint.Post(request));
        AnalyticsReports.MapAll(app);
    }

    private static string ZoneId()
    {
        var id = Environment.GetEnvironmentVariable("ARI_ANALYTICS_TZ");
        if (string.IsNullOrWhiteSpace(id)) return "UTC";
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return id;
        }
        catch (Exception)
        {
            return "UTC";
        }
    }
}
