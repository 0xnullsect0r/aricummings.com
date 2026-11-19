using System.Net;
using System.Text.Json;
using Dapper;
using Xunit;

namespace AriApi.Tests;

/// <summary>
/// The full content lifecycle against the real server: draft, publish (with the
/// missing-media refusal), rollback as a new version, unpublish, preview links, the
/// snapshot's ETag/304 behaviour, redirects, seed loading and the GitHub cache join.
/// </summary>
public sealed class ContentFlowTests : IAsyncLifetime
{
    private readonly TestSite _site = new("flow");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _site.DisposeAsync();

    private static string WorkDocument(string summary) => JsonSerializer.Serialize(new
    {
        title = "Flow Thing",
        status = "pre-release",
        category = "oss",
        year = "2026",
        summary,
        sections = Array.Empty<object>(),
    });

    [Fact]
    public async Task Draft_save_publish_and_the_snapshot_round_trip()
    {
        using var save = await _site.PutJsonAsync("/api/admin/content/work:flowthing/draft",
            new { content = JsonSerializer.Deserialize<JsonDocument>(WorkDocument("first")) });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var saved = await TestSite.Json(save);
        Assert.Equal(0, saved.RootElement.GetProperty("version").GetInt64());
        Assert.True(save.Headers.CacheControl!.NoStore); // every /api response is no-store

        using var publish = await _site.PostJsonAsync("/api/admin/content/work:flowthing/publish", new { note = "first" });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var published = await TestSite.Json(publish);
        Assert.Equal(1, published.RootElement.GetProperty("version").GetInt64());

        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        using var body = await TestSite.Json(snapshot);
        var document = body.RootElement.GetProperty("documents").GetProperty("work:flowthing");
        Assert.Equal("first", document.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Publish_refuses_blocks_pointing_at_missing_media()
    {
        var draft = new
        {
            content = new
            {
                title = "Media Thing",
                status = "pre-release",
                category = "oss",
                year = "2026",
                summary = "s",
                sections = new object[]
                {
                    new { type = "photos", items = new[] { new { image = "deadbeefdeadbeef", alt = "gone" } } },
                },
            },
        };
        using var save = await _site.PutJsonAsync("/api/admin/content/work:mediathing/draft", draft);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        using var publish = await _site.PostJsonAsync("/api/admin/content/work:mediathing/publish");
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        using var error = await TestSite.Json(publish);
        Assert.Equal("missing_media", error.RootElement.GetProperty("code").GetString());
        Assert.Contains("deadbeefdeadbeef", error.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Publish_works_once_the_media_exists()
    {
        var id = await UploadImageAsync(_site);
        var draft = new
        {
            content = new
            {
                title = "Media Thing",
                status = "pre-release",
                category = "oss",
                year = "2026",
                summary = "s",
                sections = new object[]
                {
                    new { type = "photos", items = new[] { new { image = id, alt = "present" } } },
                },
            },
        };
        using var save = await _site.PutJsonAsync("/api/admin/content/work:mediathing2/draft", draft);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var publish = await _site.PostJsonAsync("/api/admin/content/work:mediathing2/publish");
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
    }

    internal static async Task<string> UploadImageAsync(TestSite site)
    {
        using var noise = NetVips.Image.Gaussnoise(900, 700, mean: 128, sigma: 90);
        using var image = noise.Cast(NetVips.Enums.BandFormat.Uchar);
        var jpeg = image.JpegsaveBuffer(q: 85);
        using var content = new MultipartFormDataContent($"----{Guid.NewGuid():N}")
        {
            { new ByteArrayContent(jpeg), "file", "noise.jpg" },
        };
        using var upload = await site.Admin.PostAsync("/api/admin/media", content);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        using var body = await TestSite.Json(upload);
        return body.RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Rollback_is_a_new_version_with_old_content()
    {
        await SaveDraftPublish("work:rollbacker", "first");
        await SaveDraftPublish("work:rollbacker", "second");

        using var before = await _site.Admin.GetAsync("/api/admin/content/work:rollbacker/revisions");
        using var revisionsBody = await TestSite.Json(before);
        var revisions = revisionsBody.RootElement.GetProperty("revisions");
        Assert.Equal(2, revisions.GetArrayLength());

        using var rollback = await _site.PostJsonAsync("/api/admin/content/work:rollbacker/rollback/1");
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        using var rolled = await TestSite.Json(rollback);
        Assert.Equal(3, rolled.RootElement.GetProperty("version").GetInt64());

        using var revisionsAfter = await _site.Admin.GetAsync("/api/admin/content/work:rollbacker/revisions");
        using var after = await TestSite.Json(revisionsAfter);
        Assert.Equal(3, after.RootElement.GetProperty("revisions").GetArrayLength());

        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var body = await TestSite.Json(snapshot);
        Assert.Equal("first", body.RootElement.GetProperty("documents").GetProperty("work:rollbacker")
            .GetProperty("summary").GetString());
    }

    private async Task SaveDraftPublish(string key, string summary)
    {
        using var save = await _site.PutJsonAsync($"/api/admin/content/{key}/draft",
            new { content = JsonSerializer.Deserialize<JsonDocument>(WorkDocument(summary)) });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var publish = await _site.PostJsonAsync($"/api/admin/content/{key}/publish");
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
    }

    [Fact]
    public async Task Unpublish_removes_the_document_from_the_snapshot()
    {
        await SaveDraftPublish("work:vanisher", "bye");
        using var unpublish = await _site.PostJsonAsync("/api/admin/content/work:vanisher/unpublish");
        Assert.Equal(HttpStatusCode.OK, unpublish.StatusCode);

        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var body = await TestSite.Json(snapshot);
        Assert.False(body.RootElement.GetProperty("documents").TryGetProperty("work:vanisher", out _));
    }

    [Fact]
    public async Task Snapshot_version_is_steady_without_changes_and_answering_304()
    {
        using var first = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        var etag = first.Headers.ETag!.Tag;
        Assert.False(string.IsNullOrEmpty(etag));
        using var firstBody = await TestSite.Json(first);
        var version = firstBody.RootElement.GetProperty("version").GetInt64();
        Assert.Equal(etag, $"\"{version}\"");

        using var versionOnly = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content/version");
        using var versionBody = await TestSite.Json(versionOnly);
        Assert.Equal(version, versionBody.RootElement.GetProperty("version").GetInt64());

        using var same = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var sameBody = await TestSite.Json(same);
        Assert.Equal(version, sameBody.RootElement.GetProperty("version").GetInt64()); // no change, same number

        using var request = new HttpRequestMessage(HttpMethod.Get, _site.InternalBase + "/internal/content");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using var notModified = await _site.Internal.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(etag, notModified.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Publishing_changes_the_version_and_the_served_json()
    {
        using var before = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var beforeBody = await TestSite.Json(before);
        var beforeVersion = beforeBody.RootElement.GetProperty("version").GetInt64();

        await SaveDraftPublish("work:versionbump", "changed");

        using var after = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var afterBody = await TestSite.Json(after);
        var afterVersion = afterBody.RootElement.GetProperty("version").GetInt64();
        Assert.NotEqual(beforeVersion, afterVersion);
        Assert.Equal("changed", afterBody.RootElement.GetProperty("documents").GetProperty("work:versionbump")
            .GetProperty("summary").GetString());

        // The old ETag is stale now.
        using var request = new HttpRequestMessage(HttpMethod.Get, _site.InternalBase + "/internal/content");
        request.Headers.IfNoneMatch.ParseAdd($"\"{beforeVersion}\"");
        using var fresh = await _site.Internal.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task Internal_surface_is_refused_off_the_internal_listener()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _site.Public.GetAsync("/internal/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _site.Admin.GetAsync("/internal/content")).StatusCode);
    }

    [Fact]
    public async Task Preview_links_show_the_draft_and_expire_plainly()
    {
        await SaveDraftPublish("work:previewer", "published form");
        using var draft = await _site.PutJsonAsync("/api/admin/content/work:previewer/draft",
            new { content = JsonSerializer.Deserialize<JsonDocument>(WorkDocument("draft form")) });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);

        using var preview = await _site.PostJsonAsync("/api/admin/content/work:previewer/preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var body = await TestSite.Json(preview);
        var token = body.RootElement.GetProperty("token").GetString()!;
        var url = body.RootElement.GetProperty("url").GetString()!;
        // The public address, not the admin one, is what a preview link uses.
        Assert.StartsWith($"http://localhost:{_site.PublicPort}/work/previewer?preview=", url);
        Assert.EndsWith(Uri.EscapeDataString(token), url);

        using var snapshot = await _site.Internal.GetAsync(
            _site.InternalBase + "/internal/content?preview=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        using var previewBody = await TestSite.Json(snapshot);
        Assert.Equal("draft form", previewBody.RootElement.GetProperty("documents").GetProperty("work:previewer")
            .GetProperty("summary").GetString());
        Assert.Equal("no-store", snapshot.Headers.CacheControl!.ToString());

        // The live snapshot is untouched by previews.
        using var live = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var liveBody = await TestSite.Json(live);
        Assert.Equal("published form", liveBody.RootElement.GetProperty("documents").GetProperty("work:previewer")
            .GetProperty("summary").GetString());

        using var expired = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content?preview=not.a.token");
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        using var expiredBody = await TestSite.Json(expired);
        Assert.Equal("preview_expired", expiredBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Redirect_validation_and_the_snapshot()
    {
        using var reserved = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/work/enclave", to = "/" });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        using var staff = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/admin", to = "/" });
        Assert.Equal(HttpStatusCode.BadRequest, staff.StatusCode);
        using var relative = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/ok1", to = "//evil.example" });
        Assert.Equal(HttpStatusCode.BadRequest, relative.StatusCode);
        using var nonCanonical = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/OK2", to = "/" });
        Assert.Equal(HttpStatusCode.BadRequest, nonCanonical.StatusCode);
        using var self = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/ok3", to = "/ok3" });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        using var good = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/old-path", to = "/work/enclave" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        // Chains are impossible: the target is already a from.
        using var chain = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/ok4", to = "/old-path" });
        Assert.Equal(HttpStatusCode.BadRequest, chain.StatusCode);

        using var duplicate = await _site.PostJsonAsync("/api/admin/redirects", new { from = "/old-path", to = "/" });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        using var list = await _site.Admin.GetAsync("/api/admin/redirects");
        using var listBody = await TestSite.Json(list);
        var entries = listBody.RootElement.GetProperty("redirects");
        Assert.Single(entries.EnumerateArray());
        var entry = entries[0];
        var id = entry.GetProperty("id").GetInt64();
        Assert.Equal("/old-path", entry.GetProperty("from").GetString());
        Assert.Equal("/work/enclave", entry.GetProperty("to").GetString());

        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var snapshotBody = await TestSite.Json(snapshot);
        var redirects = snapshotBody.RootElement.GetProperty("redirects");
        Assert.Single(redirects.EnumerateArray());
        Assert.Equal("/old-path", redirects[0].GetProperty("from").GetString());

        using var deleted = await _site.Admin.DeleteAsync($"/api/admin/redirects/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var snapshotAfter = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var after = await TestSite.Json(snapshotAfter);
        Assert.Empty(after.RootElement.GetProperty("redirects").EnumerateArray());
    }

    [Fact]
    public async Task Seed_loads_on_an_empty_database_and_only_once()
    {
        using var snapshot = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var body = await TestSite.Json(snapshot);
        var documents = body.RootElement.GetProperty("documents");
        foreach (var key in new[]
                 {
                     "site", "page:home", "work:enclave", "work:rustos", "work:multiforge", "work:edex",
                     "work:red-wagon-rentals", "work:connected-riding",
                 })
            Assert.True(documents.TryGetProperty(key, out _), $"missing seeded document {key}");
        Assert.Equal("Ari Cummings", documents.GetProperty("site").GetProperty("name").GetString());
        Assert.Equal(8, documents.EnumerateObject().Count());
        // The work index order comes from the home document.
        Assert.Equal("enclave", documents.GetProperty("page:home").GetProperty("workOrder")[0].GetString());

        using var health = await _site.Public.GetAsync("/api/health");
        using var healthBody = await TestSite.Json(health);
        Assert.Equal("ok", healthBody.RootElement.GetProperty("status").GetString());
        Assert.True(healthBody.RootElement.GetProperty("contentVersion").GetInt64() > 0);
        // GitHub has never been fetched in tests.
        Assert.Equal(JsonValueKind.Null, healthBody.RootElement.GetProperty("githubSyncedAt").ValueKind);

        // A second boot over the same data dir seeds nothing new.
        var dataDir = _site.DataDir;
        await _site.DisposeAsync();
        await using var second = new TestSite("flow-restart", dataDir);
        Assert.Equal(8, second.Db.App().Query<long>("SELECT COUNT(*) FROM document_revisions;").Single());
        Assert.Equal(8, second.Db.App().Query<long>("SELECT COUNT(*) FROM documents;").Single());
    }

    [Fact]
    public async Task GitHub_cache_is_null_safe_and_joins_when_present()
    {
        using var empty = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var emptyBody = await TestSite.Json(empty);
        Assert.Equal(JsonValueKind.Null, emptyBody.RootElement.GetProperty("github").ValueKind);

        await using var connection = _site.Db.App();
        var fetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await connection.ExecuteAsync("""
            INSERT INTO github_cache(id, fetched_at, data) VALUES('profile', @at, '{"login":"0xnullsect0r"}')
            ON CONFLICT(id) DO UPDATE SET fetched_at=@at, data='{"login":"0xnullsect0r"}';
            INSERT INTO github_cache(id, fetched_at, data) VALUES('repos', @at, '[{"name":"enclave-messenger"}]')
            ON CONFLICT(id) DO UPDATE SET fetched_at=@at, data='[{"name":"enclave-messenger"}]';
            """, new { at = fetchedAt });

        // A publish rebuilds the snapshot inside its transaction; the join must follow.
        await SaveDraftPublish("work:gha", "with github");
        using var joined = await _site.Internal.GetAsync(_site.InternalBase + "/internal/content");
        using var joinedBody = await TestSite.Json(joined);
        var github = joinedBody.RootElement.GetProperty("github");
        Assert.Equal("0xnullsect0r", github.GetProperty("profile").GetProperty("login").GetString());
        Assert.Equal("enclave-messenger", github.GetProperty("repos")[0].GetProperty("name").GetString());
        Assert.Equal(fetchedAt, github.GetProperty("fetchedAt").GetInt64());

        using var health = await _site.Public.GetAsync("/api/health");
        using var healthBody = await TestSite.Json(health);
        Assert.Equal(fetchedAt, healthBody.RootElement.GetProperty("githubSyncedAt").GetInt64());
    }

    [Fact]
    public async Task Draft_list_shows_every_document_with_its_state()
    {
        using var list = await _site.Admin.GetAsync("/api/admin/content");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var body = await TestSite.Json(list);
        var documents = body.RootElement.GetProperty("documents");
        Assert.Equal(8, documents.GetArrayLength());
        var enclave = documents.EnumerateArray().Single(d => d.GetProperty("key").GetString() == "work:enclave");
        Assert.True(enclave.GetProperty("hasPublished").GetBoolean());
        Assert.False(enclave.GetProperty("draftDiffers").GetBoolean());
    }
}
