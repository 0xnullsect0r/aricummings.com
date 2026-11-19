using System.Security.Claims;
using System.Text.Json;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Ari.Platform.Edge;
using Ari.Platform.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Ari.Features.Content;

/// <summary>All content routes: the admin API, the redirects API and the internal snapshot surface.</summary>
public static class ContentEndpoints
{
    public static void Map(WebApplication app)
    {
        // The internal snapshot endpoints are served by InternalContentMiddleware (below):
        // the edge middleware answers the internal listener itself and never reaches the
        // router, so these paths are handled before it. The /api routes are normal.

        app.MapGet("/api/admin/content", async (HttpContext http, Db db, ContentStore store, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            await using var connection = db.App();
            var documents = await store.List(connection);
            await http.Response.WriteAsJsonAsync(new
            {
                documents = documents.Select(d => new
                {
                    d.Key,
                    d.Version,
                    d.UpdatedAt,
                    hasDraft = d.HasDraft,
                    hasPublished = d.HasPublished,
                    draftDiffers = d.DraftDiffers,
                    // Parsed JSON, so the SEO screen can read live page values.
                    published = d.PublishedJson is null ? (JsonElement?)null : JsonDocument.Parse(d.PublishedJson).RootElement.Clone(),
                }),
            });
        });

        app.MapGet("/api/admin/content/{key}/draft", async (HttpContext http, string key, Db db, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            await using var connection = db.App();
            var store = http.RequestServices.GetRequiredService<ContentStore>();
            var document = await store.Get(connection, key);
            if (document is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsJsonAsync(new { error = "No such document.", code = "not_found" });
                return;
            }
            await http.Response.WriteAsJsonAsync(new
            {
                key = document.Key,
                version = document.Version,
                draft = JsonDocument.Parse(document.Draft).RootElement.Clone(),
            });
        });

        app.MapPut("/api/admin/content/{key}/draft", async (HttpContext http, string key, Db db, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            string body;
            try
            {
                using var reader = new StreamReader(http.Request.Body);
                body = await reader.ReadToEndAsync();
            }
            catch (Exception)
            {
                throw new ApiException(400, "Could not read the request body.", "bad_body");
            }
            using var parsed = JsonDocument.Parse(body);
            if (!parsed.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
                throw new ApiException(400, "'content' must be an object.", "invalid_fields");
            var normalized = ContentStore.ValidateAndNormalize(key, content);

            var store = http.RequestServices.GetRequiredService<ContentStore>();
            var version = await store.SaveDraft(key, normalized, http.Actor(), http.Connection.RemoteIpAddress?.ToString());
            await http.Response.WriteAsJsonAsync(new { key, version });
        });

        app.MapPost("/api/admin/content/{key}/publish", async (HttpContext http, string key, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.publish")) return;
            string? note = null;
            if (http.Request.ContentLength is > 0)
            {
                using var reader = new StreamReader(http.Request.Body);
                using var parsed = JsonDocument.Parse(await reader.ReadToEndAsync());
                if (parsed.RootElement.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String)
                    note = noteElement.GetString();
            }
            var store = http.RequestServices.GetRequiredService<ContentStore>();
            var version = await store.Publish(key, note, http.Actor(), http.Connection.RemoteIpAddress?.ToString());
            await http.Response.WriteAsJsonAsync(new { key, version });
        });

        app.MapPost("/api/admin/content/{key}/unpublish", async (HttpContext http, string key, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.publish")) return;
            var store = http.RequestServices.GetRequiredService<ContentStore>();
            var version = await store.Unpublish(key, http.Actor(), http.Connection.RemoteIpAddress?.ToString());
            await http.Response.WriteAsJsonAsync(new { key, version });
        });

        app.MapPost("/api/admin/content/{key}/preview", async (HttpContext http, string key, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            var store = http.RequestServices.GetRequiredService<ContentStore>();
            var env = http.RequestServices.GetRequiredService<Env>();
            var tokens = http.RequestServices.GetRequiredService<TokenIssuer>();
            var (url, token) = await store.PreviewLink(key, env, tokens);
            await http.Response.WriteAsJsonAsync(new { key, url, token });
        });

        app.MapGet("/api/admin/content/{key}/revisions", async (HttpContext http, string key, Db db, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            await using var connection = db.App();
            var store = http.RequestServices.GetRequiredService<ContentStore>();
            if (await store.Get(connection, key) is null)
                throw new ApiException(404, "No such document.", "not_found");
            var revisions = await store.Revisions(connection, key);
            await http.Response.WriteAsJsonAsync(new
            {
                revisions = revisions.Select(r => new { r.Version, r.Note, r.Author, publishedAt = r.PublishedAt }),
            });
        });

        app.MapPost("/api/admin/content/{key}/rollback/{version:long}", async (HttpContext http, string key, long version, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.publish")) return;
            var store = http.RequestServices.GetRequiredService<ContentStore>();
            var next = await store.Rollback(key, version, null, http.Actor(), http.Connection.RemoteIpAddress?.ToString());
            await http.Response.WriteAsJsonAsync(new { key, version = next });
        });

        app.MapGet("/api/admin/redirects", async (HttpContext http, Db db, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            await using var connection = db.App();
            var store = http.RequestServices.GetRequiredService<RedirectStore>();
            var redirects = await store.List(connection);
            await http.Response.WriteAsJsonAsync(new
            {
                redirects = redirects.Select(r => new { r.Id, from = r.From, to = r.To }),
            });
        });

        app.MapPost("/api/admin/redirects", async (HttpContext http, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            using var reader = new StreamReader(http.Request.Body);
            using var parsed = JsonDocument.Parse(await reader.ReadToEndAsync());
            var from = ReadString(parsed, "from");
            var to = ReadString(parsed, "to");
            var store = http.RequestServices.GetRequiredService<RedirectStore>();
            var created = await store.Create(from, to, http.Actor(), http.Connection.RemoteIpAddress?.ToString());
            await http.Response.WriteAsJsonAsync(new { created.Id, from = created.From, to = created.To });
        });

        app.MapDelete("/api/admin/redirects/{id:long}", async (HttpContext http, long id, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "content.edit")) return;
            var store = http.RequestServices.GetRequiredService<RedirectStore>();
            if (!await store.Delete(id, http.Actor(), http.Connection.RemoteIpAddress?.ToString()))
                throw new ApiException(404, "No such redirect.", "not_found");
            http.Response.StatusCode = StatusCodes.Status204NoContent;
        });
    }

    private static string ReadString(JsonDocument document, string field)
    {
        if (!document.RootElement.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.String
            || (value.GetString() ?? "").Length == 0)
            throw new ApiException(400, $"'{field}' is required.", "invalid_fields");
        return value.GetString()!;
    }
}

/// <summary>
/// Serves /internal/content and /internal/content/version on the internal listener. The
/// edge middleware owns that listener's surface rules (everything not /internal is refused
/// there, and /internal is refused everywhere else), so these two paths are answered here,
/// before it. GET and HEAD only.
/// </summary>
public sealed class InternalContentMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context)
    {
        if (context.Get() == Surface.Internal
            && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            var path = context.Request.Path.Value ?? "";
            if (path == "/internal/content" || path == "/internal/content/")
            {
                await ServeContent(context);
                return;
            }
            if (path == "/internal/content/version")
            {
                await ServeVersion(context);
                return;
            }
        }
        await next(context);
    }

    private static async Task ServeContent(HttpContext http)
    {
        var snapshots = http.RequestServices.GetRequiredService<ContentSnapshotService>();

        if (http.Request.Query.TryGetValue("preview", out var previewValues))
        {
            var tokens = http.RequestServices.GetRequiredService<TokenIssuer>();
            await ServePreviewCall(http, snapshots, tokens, previewValues.ToString());
            return;
        }

        var current = snapshots.Current;
        var etag = current.ETag;
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "no-cache";
        var given = http.Request.Headers.IfNoneMatch.ToString();
        if (given.Length > 0 && (given == etag || given.Split(',').Select(v => v.Trim()).Contains(etag)))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        http.Response.ContentLength = current.Json.Length;
        if (HttpMethods.IsHead(http.Request.Method)) return;
        await http.Response.Body.WriteAsync(current.Json);
    }

    private static async Task ServeVersion(HttpContext http)
    {
        var snapshots = http.RequestServices.GetRequiredService<ContentSnapshotService>();
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        var body = System.Text.Encoding.UTF8.GetBytes($"{{\"version\":{snapshots.Current.Version}}}");
        http.Response.ContentLength = body.Length;
        if (HttpMethods.IsHead(http.Request.Method)) return;
        await http.Response.Body.WriteAsync(body);
    }

    /// <summary>
    /// A preview token buys one document's draft, in place of its published form, in a
    /// snapshot built for this request only. Expired or invalid links get a plain 403.
    /// </summary>
    private static async Task ServePreviewCall(HttpContext http, ContentSnapshotService snapshots, TokenIssuer tokens, string token)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Robots-Tag"] = "noindex";
        // The token arrives on a query string: treat any malformed value as expired.
        ClaimsPrincipal? principal = null;
        try
        {
            principal = await tokens.Read(token, TokenAudience.Preview);
        }
        catch (Exception)
        {
            principal = null;
        }
        var key = principal?.FindFirst("key")?.Value;
        if (key is null)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync("""{"error":"This preview link has expired.","code":"preview_expired"}""");
            return;
        }
        var snapshot = await snapshots.BuildPreview(key);
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(snapshot.Json);
    }
}

/// <summary>Installs the internal content middleware in front of the whole pipeline.</summary>
public sealed class InternalContentStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
    {
        builder.UseMiddleware<InternalContentMiddleware>();
        next(builder);
    };
}
