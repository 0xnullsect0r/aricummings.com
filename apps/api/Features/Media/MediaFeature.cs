using Ari.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Ari.Features.Media;

/// <summary>
/// Raises the request-body ceiling for the one upload route before the API pipeline can
/// refuse it, and only there: every other /api route keeps the shared 1 MiB default.
/// </summary>
public sealed class MediaBodyLimitStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
    {
        builder.Use(rest => async context =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.StartsWithSegments("/api/admin/media"))
            {
                context.Items["Ari.BodyLimit"] = (long)MediaImage.MaxUploadBytes;
                context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = MediaImage.MaxUploadBytes;
            }
            await rest(context);
        });
        next(builder);
    };
}

/// <summary>
/// The media feature: upload pipeline, media library, immutable serving and the build-media
/// CLI. Order 50: content (10) and GitHub (40) are already registered.
/// </summary>
public sealed class MediaFeature : IFeature
{
    public int Order => 50;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<IStartupFilter, MediaBodyLimitStartupFilter>();
        services.AddSingleton<MediaProcessor>();
        services.AddSingleton<MediaStore>();
        services.AddSingleton<IMediaStore>(sp => sp.GetRequiredService<MediaStore>());
    }

    public void Map(WebApplication app) => MediaEndpoints.Map(app);
}
