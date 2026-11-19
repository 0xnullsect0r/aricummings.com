using Ari.Platform;
using Ari.Platform.Database;
using Ari.Platform.Edge;
using Ari.Platform.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Ari.Features.Content;

/// <summary>
/// The content feature: documents, revisions, redirects, the snapshot, previews and
/// seeding. Order 10: registers first so later features (auth's real guards, GitHub's
/// cache) replace the defaults it installs.
/// </summary>
public sealed class ContentFeature : IFeature
{
    public int Order => 10;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter, InternalContentStartupFilter>();
        services.AddSingleton<IRouteGuards>(NoopRouteGuards.Instance);
        services.AddSingleton<ContentSnapshotService>();
        services.AddSingleton<ContentStore>();
        services.AddSingleton<RedirectStore>();
        services.AddSingleton<IHealthReporter, ContentHealthReporter>();
        services.AddSingleton<IRedirectSource, SnapshotRedirectSource>();
        // The shared job host: the earliest feature registers it; later features only add
        // JobOptions singletons (GitHub does) so schedules are never duplicated.
        services.AddHostedService<JobHost>();
        services.AddHostedService<ContentBoot>();
    }

    public void Map(WebApplication app) => ContentEndpoints.Map(app);
}
