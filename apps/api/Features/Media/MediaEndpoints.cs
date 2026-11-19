using System.Text.Json;
using Ari.Features.Content;
using Ari.Platform;
using Ari.Platform.Api;
using Ari.Platform.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Ari.Features.Media;

/// <summary>Upload, list, alt-text and delete on the admin API; immutable serving under /media.</summary>
public static class MediaEndpoints
{
    /// <summary>/media/&lt;id&gt;/&lt;n|og&gt;.&lt;ext&gt; — the strict name rule from the pipeline contract.</summary>
    public static readonly System.Text.RegularExpressions.Regex NamePattern =
        new("^(?:[0-9]{1,4}|og)\\.(avif|webp|jpg)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static void Map(WebApplication app)
    {
        app.MapMethods("/media/{id}/{name}", new[] { "GET", "HEAD" }, async (HttpContext http, string id, string name) =>
        {
            if (!MediaIds.Pattern().IsMatch(id) || !NamePattern.IsMatch(name))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var env = http.RequestServices.GetRequiredService<Env>();
            var path = Path.Combine(env.MediaDir, id, name);
            if (!File.Exists(path))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = name.EndsWith(".avif", StringComparison.Ordinal) ? "image/avif"
                : name.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp"
                : "image/jpeg";
            // Content-addressed files never change.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers.ETag = $"\"{id}-{name}\"";
            http.Response.ContentLength = new FileInfo(path).Length;
            if (HttpMethods.IsHead(http.Request.Method)) return;
            await using var file = File.OpenRead(path);
            await file.CopyToAsync(http.Response.Body);
        });

        app.MapGet("/api/admin/media", async (HttpContext http, Db db, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "media.manage")) return;
            await using var connection = db.App();
            var store = http.RequestServices.GetRequiredService<MediaStore>();
            var media = await store.List(connection);
            var inUse = new Dictionary<string, bool>();
            foreach (var row in media)
                inUse[row.Id] = await store.IsInUse(connection, row.Id);
            await http.Response.WriteAsJsonAsync(new
            {
                media = media.Select(m => new
                {
                    m.Id,
                    m.Original,
                    m.Width,
                    m.Height,
                    m.Bytes,
                    m.Alt,
                    m.CreatedAt,
                    inUse = inUse[m.Id],
                    url = $"/media/{m.Id}/og.jpg",
                }),
            });
        });

        app.MapPost("/api/admin/media", async (HttpContext http, IRouteGuards guards) =>
        {
            // The permission check runs before the size limit, so a stranger learns
            // nothing about either.
            if (!guards.Allowed(http, "media.manage")) return;
            if (http.Request.ContentLength is > MediaImage.MaxUploadBytes)
                throw new ApiException(413, "Images are limited to 40 MB.", "too_large");

            var (file, originalName, alt) = await ReadMultipart(http);
            var processor = http.RequestServices.GetRequiredService<MediaProcessor>();
            var image = await processor.ProcessAsync(file, originalName, http.RequestAborted);

            var db = http.RequestServices.GetRequiredService<Db>();
            await using var connection = db.App();
            await Db.Write(connection, async transaction =>
            {
                var store = http.RequestServices.GetRequiredService<MediaStore>();
                await store.Insert(connection, transaction, image, alt, http.Actor());
                AuditLog.Record(connection, transaction, http.Actor(), "media.uploaded", image.Id, image.OriginalName,
                    http.Connection.RemoteIpAddress?.ToString());
                return 0;
            });
            var snapshots = http.RequestServices.GetRequiredService<ContentSnapshotService>();
            await snapshots.Reload();

            http.Response.StatusCode = StatusCodes.Status201Created;
            await http.Response.WriteAsJsonAsync(new
            {
                id = image.Id,
                width = image.Width,
                height = image.Height,
                alt,
                url = $"/media/{image.Id}/og.jpg",
            });
        });

        app.MapMethods("/api/admin/media/{id}", new[] { "PATCH" }, async (HttpContext http, string id, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "media.manage")) return;
            using var reader = new StreamReader(http.Request.Body);
            using var parsed = JsonDocument.Parse(await reader.ReadToEndAsync());
            if (!parsed.RootElement.TryGetProperty("alt", out var altElement) || altElement.ValueKind != JsonValueKind.String)
                throw new ApiException(400, "'alt' must be text.", "invalid_fields");
            var alt = altElement.GetString() ?? "";
            if (alt.Length > 500)
                throw new ApiException(400, "'alt' must be at most 500 characters.", "invalid_fields");

            var db = http.RequestServices.GetRequiredService<Db>();
            var store = http.RequestServices.GetRequiredService<MediaStore>();
            await using var connection = db.App();
            if (await store.Find(connection, id) is null)
                throw new ApiException(404, "No such media.", "not_found");
            await store.SetAlt(id, alt);
            AuditLog.Record(connection, null, http.Actor(), "media.alt_updated", id, "", http.Connection.RemoteIpAddress?.ToString());
            await http.Response.WriteAsJsonAsync(new { id, alt });
        });

        app.MapDelete("/api/admin/media/{id}", async (HttpContext http, string id, IRouteGuards guards) =>
        {
            if (!guards.Allowed(http, "media.manage")) return;
            var db = http.RequestServices.GetRequiredService<Db>();
            var store = http.RequestServices.GetRequiredService<MediaStore>();
            await using var connection = db.App();
            if (await store.Find(connection, id) is null)
                throw new ApiException(404, "No such media.", "not_found");
            if (await store.IsInUse(connection, id))
                throw new ApiException(409, "That image is used by a document or an old revision.", "media_in_use");

            if (!await store.Delete(id))
                throw new ApiException(404, "No such media.", "not_found");
            AuditLog.Record(connection, null, http.Actor(), "media.deleted", id, "", http.Connection.RemoteIpAddress?.ToString());
            var snapshots = http.RequestServices.GetRequiredService<ContentSnapshotService>();
            await snapshots.Reload();
            http.Response.StatusCode = StatusCodes.Status204NoContent;
        });
    }

    private const long StreamLimit = MediaImage.MaxUploadBytes + 64 * 1024;

    /// <summary>Reads the multipart body by hand: one "file" part, one optional "alt" part.</summary>
    private static async Task<(byte[] File, string Name, string? Alt)> ReadMultipart(HttpContext http)
    {
        var contentType = http.Request.ContentType;
        if (contentType is null || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "Upload as multipart/form-data with a 'file' part.", "bad_upload");
        var mediaType = MediaTypeHeaderValue.Parse(contentType);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).ToString();
        if (string.IsNullOrEmpty(boundary))
            throw new ApiException(400, "The multipart body has no boundary.", "bad_upload");

        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = StreamLimit;
        var reader = new MultipartReader(boundary, http.Request.Body)
        {
            BodyLengthLimit = StreamLimit,
        };

        byte[]? file = null;
        var fileName = "upload";
        string? alt = null;
        MultipartSection? section;
        while (await reader.ReadNextSectionAsync(http.RequestAborted) is { } next)
        {
            section = next;
            ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition);
            var name = disposition?.Name.Value;
            if (name == "file")
            {
                fileName = disposition?.FileNameStar.Value ?? disposition?.FileName.Value ?? "upload";
                file = await ReadCapped(section.Body, MediaImage.MaxUploadBytes, http.RequestAborted);
            }
            else if (name == "alt")
            {
                using var text = new StreamReader(section.Body);
                alt = (await text.ReadToEndAsync(http.RequestAborted)).Trim();
                if (alt.Length == 0) alt = null;
                if (alt is { Length: > 500 })
                    throw new ApiException(400, "'alt' must be at most 500 characters.", "invalid_fields");
            }
        }
        if (file is null)
            throw new ApiException(400, "No 'file' part in the upload.", "bad_upload");
        return (file, fileName, alt);
    }

    /// <summary>Copies a section body with a hard cap, so a chunked upload can't dodge the limit.</summary>
    private static async Task<byte[]> ReadCapped(Stream body, long cap, CancellationToken cancellation)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(chunk, cancellation)) > 0)
        {
            total += read;
            if (total > cap)
                throw new ApiException(413, "Images are limited to 40 MB.", "too_large");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellation);
        }
        return buffer.ToArray();
    }
}
