using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Forwarder;

namespace Ari.Platform.Edge;

/// <summary>
/// Forwards pages to the web container: no proxy, no auto-redirect, no cookie handling,
/// connect timeout 5 s, activity timeout 30 s. If web is down the visitor gets a plain
/// 502 immediately and /api/health stays up.
/// </summary>
public sealed class PageProxy(IHttpForwarder forwarder, Env env, ILogger<PageProxy> logger)
{
    private HttpMessageInvoker? _client;
    private ForwarderRequestConfig? _config;

    public async Task Invoke(HttpContext context)
    {
        _client ??= new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        _config ??= new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(30) };

        var error = await forwarder.SendAsync(context, env.WebOrigin, _client, _config,
            static (context, request) =>
            {
                request.Headers.Host = null; // let the handler derive it from the destination
                return ValueTask.CompletedTask;
            });

        if (error != ForwarderError.None)
        {
            if (error != ForwarderError.ResponseBodyCanceled)
                logger.LogWarning("Page proxy error {Error} for {Path}", error, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("The site is starting up. Try again in a moment.");
            }
        }
    }
}
