using System.Net;
using System.Text.Json;
using Dapper;
using Xunit;

namespace AriApi.Tests;

/// <summary>
/// The media pipeline end to end: upload, the rendition set on disk, immutable serving with
/// HEAD, alt text, delete refusal while in use (draft, published or revision), the strict
/// URL name rule, and the build-media CLI.
/// </summary>
public sealed class MediaTests : IAsyncLifetime
{
    private readonly TestSite _site = new("media");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _site.DisposeAsync();

    private static async Task<(string Id, int Width, int Height)> Upload(TestSite site, int width, int height)
    {
        using var noise = NetVips.Image.Gaussnoise(width, height, mean: 128, sigma: 90);
        using var image = noise.Cast(NetVips.Enums.BandFormat.Uchar);
        var jpeg = image.JpegsaveBuffer(q: 90);
        using var content = new MultipartFormDataContent($"----{Guid.NewGuid():N}")
        {
            { new ByteArrayContent(jpeg), "file", "photo.jpg" },
        };
        using var upload = await site.Admin.PostAsync("/api/admin/media", content);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var body = await TestSite.Json(upload);
        return (body.RootElement.GetProperty("id").GetString()!, width, height);
    }

    [Fact]
    public async Task Upload_produces_the_full_rendition_set_and_serves_it_immutable()
    {
        var (id, width, height) = await Upload(_site, 900, 700);
        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.Equal(width, width);

        var directory = Path.Combine(_site.DataDir, "media", id);
        // 900 wide: renditions at 400 and 800, never larger than the source.
        Assert.True(File.Exists(Path.Combine(directory, "400.avif")));
        Assert.True(File.Exists(Path.Combine(directory, "400.webp")));
        Assert.True(File.Exists(Path.Combine(directory, "800.avif")));
        Assert.True(File.Exists(Path.Combine(directory, "800.webp")));
        Assert.False(File.Exists(Path.Combine(directory, "1200.avif")));
        Assert.True(File.Exists(Path.Combine(directory, "blur.webp")));
        Assert.True(File.Exists(Path.Combine(directory, "og.jpg")));
        // Nothing else is served and nothing leaks from the temp folder.
        Assert.Equal(6, Directory.GetFiles(directory).Length);
        Assert.False(Directory.Exists(Path.Combine(_site.DataDir, "media", ".tmp"))
            && Directory.EnumerateDirectories(Path.Combine(_site.DataDir, "media", ".tmp")).Any());

        using var served = await _site.Public.GetAsync($"/media/{id}/400.webp");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/webp", served.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", served.Headers.CacheControl!.ToString());
        Assert.True(served.Content.Headers.ContentLength > 0);

        using var head = new HttpRequestMessage(HttpMethod.Head, $"/media/{id}/og.jpg");
        using var headed = await _site.Public.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, headed.StatusCode);
        Assert.Empty(await headed.Content.ReadAsByteArrayAsync());
        Assert.True(headed.Content.Headers.ContentLength > 0);

        using var jpegServed = await _site.Public.GetAsync($"/media/{id}/og.jpg");
        Assert.Equal("image/jpeg", jpegServed.Content!.Headers.ContentType!.MediaType);

        // The decoded renditions have the right dimensions and are sRGB.
        using (var avif = NetVips.Image.NewFromFile(Path.Combine(directory, "400.avif"), access: NetVips.Enums.Access.Sequential))
        {
            Assert.Equal(400, avif.Width);
            Assert.Equal(311, avif.Height);
        }
        using (var og = NetVips.Image.NewFromFile(Path.Combine(directory, "og.jpg"), access: NetVips.Enums.Access.Sequential))
        {
            Assert.Equal(900, og.Width);
            Assert.Equal(700, og.Height);
        }

        // The media index in the snapshot knows the widths.
        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var body = await TestSite.Json(snapshot);
        var entry = body.RootElement.GetProperty("media").GetProperty(id);
        Assert.Equal(900, entry.GetProperty("w").GetInt32());
        Assert.Equal(700, entry.GetProperty("h").GetInt32());
        Assert.True(entry.GetProperty("og").GetBoolean());
        Assert.Equal(2, entry.GetProperty("widths").GetArrayLength());
        Assert.Equal(400, entry.GetProperty("widths")[0].GetInt32());
    }

    [Fact]
    public async Task Uploads_larger_than_the_shared_api_body_limit_go_through()
    {
        // A 1400x1400 noise JPEG is well over the 1 MiB the API pipeline allows by default;
        // only the media route's own 40 MB ceiling applies.
        var (id, _, _) = await Upload(_site, 1400, 1400);
        Assert.Matches("^[0-9a-f]{16}$", id);
    }

    [Fact]
    public async Task Alt_text_can_be_patched()
    {
        var (id, _, _) = await Upload(_site, 500, 400);
        using var patch = await _site.Admin.PatchAsync($"/api/admin/media/{id}",
            new StringContent(JsonSerializer.Serialize(new { alt = "a noise field" }), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        using var list = await _site.Admin.GetAsync("/api/admin/media");
        using var body = await TestSite.Json(list);
        var row = body.RootElement.GetProperty("media").EnumerateArray().Single(m => m.GetProperty("id").GetString() == id);
        Assert.Equal("a noise field", row.GetProperty("alt").GetString());
        Assert.False(row.GetProperty("inUse").GetBoolean());
    }

    [Fact]
    public async Task Delete_is_refused_while_any_document_or_revision_references_the_id()
    {
        var (id, _, _) = await Upload(_site, 500, 400);
        var draft = new
        {
            content = new
            {
                title = "Kept",
                status = "pre-release",
                category = "oss",
                year = "2026",
                summary = "s",
                sections = new object[] { new { type = "photos", items = new[] { new { image = id } } } },
            },
        };
        using var save = await _site.PutJsonAsync("/api/admin/content/work:kept/draft", draft);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Referenced by a draft alone.
        using var refused = await _site.Admin.DeleteAsync($"/api/admin/media/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        using var publish = await _site.PostJsonAsync("/api/admin/content/work:kept/publish");
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var stillRefused = await _site.Admin.DeleteAsync($"/api/admin/media/{id}");
        Assert.Equal(HttpStatusCode.Conflict, stillRefused.StatusCode);

        // Referenced only by an old revision: a rollback must never point at a deleted image.
        var without = new
        {
            content = new
            {
                title = "Kept",
                status = "pre-release",
                category = "oss",
                year = "2026",
                summary = "s",
            },
        };
        using var rewrite = await _site.PutJsonAsync("/api/admin/content/work:kept/draft", without);
        Assert.Equal(HttpStatusCode.OK, rewrite.StatusCode);
        using var republish = await _site.PostJsonAsync("/api/admin/content/work:kept/publish");
        Assert.Equal(HttpStatusCode.OK, republish.StatusCode);
        using var revisionRefused = await _site.Admin.DeleteAsync($"/api/admin/media/{id}");
        Assert.Equal(HttpStatusCode.Conflict, revisionRefused.StatusCode);

        // Unknown id.
        using var unknown = await _site.Admin.DeleteAsync("/api/admin/media/0000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_row_and_files_once_nothing_references_the_id()
    {
        var (id, _, _) = await Upload(_site, 500, 400);
        using var deleted = await _site.Admin.DeleteAsync($"/api/admin/media/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_site.DataDir, "media", id)));

        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var body = await TestSite.Json(snapshot);
        Assert.False(body.RootElement.GetProperty("media").TryGetProperty(id, out _));
    }

    [Fact]
    public async Task Serving_refuses_everything_outside_the_strict_name_rule()
    {
        var (id, _, _) = await Upload(_site, 500, 400);
        foreach (var name in new[] { "blur.webp", "400.png", "400.avif.gif", "og.jpeg", "x.avif", "24000.avif", "400.jpg.html" })
        {
            using var refused = await _site.Public.GetAsync($"/media/{id}/{name}");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
        foreach (var badId in new[] { "not-an-id", "00000000000000000", "AAAAAAAAAAAAAAAA" })
        {
            using var refused = await _site.Public.GetAsync($"/media/{badId}/400.webp");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
        // Escapes out of the media folder stay 404.
        using var escape = await _site.Public.GetAsync($"/media/{id}/..%2F..%2Fapp.db");
        Assert.Equal(HttpStatusCode.NotFound, escape.StatusCode);
    }

    [Fact]
    public void The_url_name_rule_is_exactly_numbers_1_to_4_digits_or_og()
    {
        foreach (var good in new[] { "0.avif", "400.avif", "800.webp", "1200.jpg", "2400.avif", "4000.avif", "og.jpg", "og.webp", "og.avif" })
            Assert.True(Ari.Features.Media.MediaEndpoints.NamePattern.IsMatch(good), good);
        foreach (var bad in new[]
                 {
                     "blur.webp", "400.png", "400", "og", "400.avif.png", "-1.avif", "24000.avif", "og.jpg.exe", "400.gif",
                 })
            Assert.False(Ari.Features.Media.MediaEndpoints.NamePattern.IsMatch(bad), bad);
    }

    [Fact]
    public void Formats_are_detected_from_bytes()
    {
        Assert.Equal(Ari.Features.Media.MediaFormat.Jpeg, Ari.Features.Media.MediaImage.Detect(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Png, Ari.Features.Media.MediaImage.Detect(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Webp, Ari.Features.Media.MediaImage.Detect(
            new byte[] { 0x52, 0x49, 0x46, 0x46, 0xBC, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20 }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Avif, Ari.Features.Media.MediaImage.Detect(
            new byte[] { 0, 0, 0, 0x1C, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, 0x66, 0, 0, 0, 0, 0x61, 0x76, 0x69, 0x66, 0x6D, 0x69, 0x66, 0x31, 0, 0, 0, 0 }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Heic, Ari.Features.Media.MediaImage.Detect(
            new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 0, 0, 0, 0, 0x6D, 0x69, 0x66, 0x31, 0x68, 0x65, 0x69, 0x63, 0, 0, 0, 0 }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Tiff, Ari.Features.Media.MediaImage.Detect(
            new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Tiff, Ari.Features.Media.MediaImage.Detect(
            new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08 }));
        Assert.Equal(Ari.Features.Media.MediaFormat.Unknown, Ari.Features.Media.MediaImage.Detect(
            "GIF89a\0\0\0"u8.ToArray()));

        Assert.Throws<Ari.Platform.Api.ApiException>(() => Ari.Features.Media.MediaImage.AssertAcceptable(
            new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 0, 0, 0, 0, 0x6D, 0x69, 0x66, 0x31, 0x68, 0x65, 0x69, 0x63, 0, 0, 0, 0 }));
        var thrown = Assert.Throws<Ari.Platform.Api.ApiException>(() =>
            Ari.Features.Media.MediaImage.AssertAcceptable("GIF89a"u8.ToArray()));
        Assert.Equal("bad_format", thrown.Code);
    }

    [Fact]
    public async Task The_same_upload_twice_is_the_same_id()
    {
        using var noise = NetVips.Image.Gaussnoise(420, 300, mean: 128, sigma: 90);
        using var image = noise.Cast(NetVips.Enums.BandFormat.Uchar);
        var jpeg = image.JpegsaveBuffer(q: 90);

        async Task<string> Send()
        {
            using var content = new MultipartFormDataContent($"----{Guid.NewGuid():N}")
            {
                { new ByteArrayContent(jpeg), "file", "photo.jpg" },
            };
            using var upload = await _site.Admin.PostAsync("/api/admin/media", content);
            return (await TestSite.Json(upload)).RootElement.GetProperty("id").GetString()!;
        }

        Assert.Equal(await Send(), await Send());
    }

    [Fact]
    public async Task Build_media_cli_processes_a_folder_into_the_library()
    {
        var folder = Path.Combine("/tmp/content-wt", $"cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using (var noise = NetVips.Image.Gaussnoise(460, 320, mean: 128, sigma: 90))
            using (var image = noise.Cast(NetVips.Enums.BandFormat.Uchar))
            {
                File.WriteAllBytes(Path.Combine(folder, "one.jpg"), image.JpegsaveBuffer(q: 90));
                File.WriteAllBytes(Path.Combine(folder, "two.png"), image.PngsaveBuffer());
            }
            File.WriteAllBytes(Path.Combine(folder, "three.gif"), "GIF89a"u8.ToArray());

            Environment.SetEnvironmentVariable("ARI_BUILD_MEDIA_DIR", folder);
            var cli = new Ari.Features.Media.BuildMediaCli();
            var exit = await cli.Run(_site.Env, Array.Empty<string>());
            Assert.Equal(0, exit);

            await using var connection = _site.Db.App();
            var rows = connection.Query<string>("SELECT id FROM media ORDER BY id;").ToList();
            Assert.Equal(2, rows.Count);
            foreach (var id in rows)
            {
                Assert.Matches("^[0-9a-f]{16}$", id);
                Assert.True(File.Exists(Path.Combine(_site.DataDir, "media", id, "400.avif")));
                Assert.True(File.Exists(Path.Combine(_site.DataDir, "media", id, "og.jpg")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARI_BUILD_MEDIA_DIR", null);
            Directory.Delete(folder, recursive: true);
        }
    }
}
