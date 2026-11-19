using System.Text.Json;
using Ari.Features.Content;
using Ari.Platform.Api;
using Xunit;

namespace AriApi.Tests;

/// <summary>Schema validation accept/reject per document kind, and rich-text cleaning.</summary>
public sealed class SchemaTests
{
    private static (string Json, IReadOnlyList<string> Problems) Normalize(string key, string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var result = DocumentSchema.Normalize(key, parsed.RootElement, out var problems);
        return (result, problems);
    }

    private static readonly string ValidSite = """
        {
          "name": "Ari Cummings",
          "tagline": "t",
          "contactEmail": "contact@aricummings.com",
          "githubHandle": "0xnullsect0r",
          "location": "McMinnville, Oregon",
          "footerLicense": "AGPL-3.0",
          "seo": { "title": "x", "description": "y" }
        }
        """;

    private static readonly string ValidHome = """
        {
          "hero": { "kicker": "k", "lines": ["one", "two"], "intro": "i" },
          "about": { "paras": ["p"], "facts": ["f"] },
          "clientIntro": "c",
          "alsoBuilding": [{ "name": "Quark" }],
          "stack": [{ "label": "Systems", "items": ["Rust"] }],
          "workOrder": ["enclave"]
        }
        """;

    private static readonly string ValidWork = """
        {
          "title": "Thing",
          "status": "pre-release",
          "category": "oss",
          "year": "2026",
          "summary": "s",
          "links": [{ "label": "Source", "url": "https://example.com" }],
          "tech": ["Rust"],
          "repo": { "url": "https://example.com", "license": "GPL-3.0" },
          "sections": [
            { "type": "text", "heading": "h", "html": "<p>hi</p>" },
            { "type": "photos", "items": [{ "image": "0123456789abcdef", "alt": "a" }] },
            { "type": "link", "label": "l", "url": "https://example.com" }
          ]
        }
        """;

    [Fact]
    public void Accepts_valid_documents_of_every_kind()
    {
        Assert.Empty(Normalize("site", ValidSite).Problems);
        Assert.Empty(Normalize("page:home", ValidHome).Problems);
        Assert.Empty(Normalize("work:thing", ValidWork).Problems);
    }

    [Fact]
    public void Site_rejects_missing_and_bad_fields()
    {
        Assert.Contains("name: is required", Normalize("site", "{}").Problems);
        Assert.Contains("contactEmail: must be an email address", Normalize("site", """
            { "name": "a", "githubHandle": "g", "location": "l", "footerLicense": "AGPL-3.0",
              "contactEmail": "not an email" }
            """).Problems);
    }

    [Fact]
    public void Home_rejects_empty_hero_lines_and_bad_stack()
    {
        Assert.Contains("hero.lines: needs at least 1 items", Normalize("page:home", """
            { "hero": { "lines": [] }, "about": { "paras": ["p"] }, "stack": [{ "label": "s", "items": ["x"] }] }
            """).Problems);
        Assert.Contains("stack: needs at least 1 items", Normalize("page:home", """
            { "hero": { "lines": ["l"] }, "about": { "paras": ["p"] }, "stack": [] }
            """).Problems);
        Assert.Contains("alsoBuilding[0].url: must be an https address", Normalize("page:home", """
            { "hero": { "lines": ["l"] }, "about": { "paras": ["p"] }, "stack": [{ "label": "s", "items": ["x"] }],
              "alsoBuilding": [{ "name": "n", "url": "http://insecure.example" }] }
            """).Problems);
    }

    [Fact]
    public void Work_rejects_bad_category_media_id_and_link_url()
    {
        Assert.Contains("category: must be one of oss, client", Normalize("work:x", """
            { "title": "t", "status": "s", "category": "other", "year": "2026", "summary": "s" }
            """).Problems);
        Assert.Contains("sections[0].items[0].image: must be a media id (16 hex characters)", Normalize("work:x", """
            { "title": "t", "status": "s", "category": "oss", "year": "2026", "summary": "s",
              "sections": [{ "type": "photos", "items": [{ "image": "nothex" }] }] }
            """).Problems);
        Assert.Contains("sections[0].url: must be an https or mailto address", Normalize("work:x", """
            { "title": "t", "status": "s", "category": "oss", "year": "2026", "summary": "s",
              "sections": [{ "type": "link", "label": "l", "url": "ftp://example.com" }] }
            """).Problems);
        Assert.Contains("sections[0].label: must be 1 to 300 characters", Normalize("work:x", """
            { "title": "t", "status": "s", "category": "oss", "year": "2026", "summary": "s",
              "sections": [{ "type": "link", "label": "", "url": "https://example.com" }] }
            """).Problems);
        Assert.Contains("sections[0].type: must be text, photos or link", Normalize("work:x", """
            { "title": "t", "status": "s", "category": "oss", "year": "2026", "summary": "s",
              "sections": [{ "type": "video" }] }
            """).Problems);
    }

    [Fact]
    public void Keys_and_reserved_slugs_are_refused()
    {
        Assert.False(DocumentSchema.IsValidKey("page:about", out _));
        Assert.False(DocumentSchema.IsValidKey("random", out _));
        Assert.False(DocumentSchema.IsValidKey("work:Admin", out _));
        Assert.False(DocumentSchema.IsValidKey("work:admin", out var problem));
        Assert.Contains("reserved", problem);
        Assert.False(DocumentSchema.IsValidKey("work:--x", out _));
        Assert.True(DocumentSchema.IsValidKey("work:red-wagon-rentals", out _));
        Assert.True(DocumentSchema.IsValidKey("site", out _));
        Assert.True(DocumentSchema.IsValidKey("page:home", out _));
    }

    [Fact]
    public void Empty_optional_arrays_are_omitted_and_unknown_keys_dropped()
    {
        var (json, problems) = Normalize("work:x", """
            { "title": "t", "status": "s", "category": "oss", "year": "2026", "summary": "s",
              "sections": [], "futureField": { "x": 1 } }
            """);
        Assert.Empty(problems);
        using var parsed = JsonDocument.Parse(json);
        Assert.False(parsed.RootElement.TryGetProperty("sections", out _));
        Assert.False(parsed.RootElement.TryGetProperty("futureField", out _));
        Assert.Equal("t", parsed.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Blank_blocks_are_kept_so_a_draft_can_be_half_written()
    {
        var (json, problems) = Normalize("work:x", """
            { "title": "t", "status": "s", "category": "oss", "year": "2026", "summary": "s",
              "sections": [{ "type": "text" }] }
            """);
        Assert.Empty(problems);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.GetProperty("sections")[0].ValueKind);
    }

    [Fact]
    public void Rich_text_drops_disallowed_tags_but_keeps_text_and_kills_script_content()
    {
        var cleaned = RichText.Clean(
            "<p>keep <span>this</span></p><script>alert(1)</script><style>body{}</style>" +
            "<h2>head</h2><a href=\"https://x.example\" target=\"_blank\">link</a>");
        Assert.Contains("keep this", cleaned);
        Assert.DoesNotContain("span", cleaned);
        Assert.DoesNotContain("alert(1)", cleaned);
        Assert.DoesNotContain("body{}", cleaned);
        Assert.DoesNotContain("<script", cleaned);
        Assert.Contains("<h2>head</h2>", cleaned);
        Assert.Contains("href=\"https://x.example\"", cleaned);
        Assert.Contains("target=\"_blank\"", cleaned);
    }

    [Fact]
    public void Rich_text_allows_only_the_four_schemes()
    {
        Assert.Contains("mailto:", RichText.Clean("<a href=\"mailto:a@b.example\">m</a>"));
        Assert.Contains("tel:", RichText.Clean("<a href=\"tel:+1555\">t</a>"));
        Assert.DoesNotContain("javascript:", RichText.Clean("<a href=\"javascript:alert(1)\">x</a>"));
        Assert.DoesNotContain("data:", RichText.Clean("<a href=\"data:text/html,boo\">x</a>"));
    }

    [Fact]
    public void Rich_text_refuses_protocol_relative_and_defangs_hidden_schemes()
    {
        Assert.Throws<ApiException>(() => RichText.Clean("<a href=\"//evil.example\">x</a>"));
        // A control character cannot hide a scheme: it is stripped before anything reads hrefs.
        Assert.DoesNotContain("jav", RichText.Clean("<a href=\"jav\u0000ascript:x\">x</a>"));
        // Neither can an HTML comment.
        Assert.DoesNotContain("jav", RichText.Clean("<a href=\"jav<!--x-->ascript:alert(1)\">x</a>"));
    }

    [Fact]
    public void Rich_text_ignores_html_comments()
    {
        var cleaned = RichText.Clean("<p>a</p><!-- hidden -->");
        Assert.DoesNotContain("hidden", cleaned);
    }
}
