using System.Text;
using Ari.Features.Analytics;
using Xunit;

namespace AriApi.Tests.Analytics;

/// <summary>Pure logic: bot classification, path normalization, CSV escaping, UA parsing, geo null-safety.</summary>
public sealed class AnalyticsUnitTests
{
    private static readonly string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "isbot")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", "isbot")]
    [InlineData("curl/8.9.1", "isbot")]
    [InlineData("Wget/1.21.4 (linux-gnu)", "isbot")]
    [InlineData("python-requests/2.32.3", "isbot")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) HeadlessChrome/131.0.0.0", "headless")]
    [InlineData("", "empty")]
    [InlineData(null, "empty")]
    public void Bots_classify_with_a_reason(string? userAgent, string reason)
    {
        Assert.Equal(reason, new BotFilter().Classify(userAgent, webdriverFlag: false));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0")]
    public void Ordinary_visitors_are_not_bots(string userAgent)
    {
        Assert.Null(new BotFilter().Classify(userAgent, webdriverFlag: false));
    }

    [Fact]
    public void Webdriver_flag_alone_marks_a_bot()
    {
        Assert.Equal("webdriver", new BotFilter().Classify(Chrome, webdriverFlag: true));
    }

    [Fact]
    public void Bot_pattern_compiles_with_ecmascript_semantics()
    {
        // The vendored pattern must construct under ECMAScript|IgnoreCase with a timeout.
        var filter = new BotFilter();
        Assert.Null(filter.Classify(Chrome, webdriverFlag: false));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/work/enclave", "/work/enclave")]
    [InlineData("/WORK/Enclave", "/work/enclave")]
    [InlineData("/x/../work/rustos", "/work/rustos")]
    [InlineData("/%77ork/edex", "/work/edex")]
    [InlineData("/work/enclave?utm=x", "/work/enclave")]
    [InlineData("/work", "/work")]
    [InlineData("/work/", "/work")]
    [InlineData("/random/page", "(other)")]
    [InlineData("/admin", "(other)")]
    [InlineData("/work/a b", "(other)")]
    [InlineData("http://evil.example/work/enclave", "/work/enclave")]
    public void Paths_normalize_to_the_known_page_set(string raw, string expected)
    {
        Assert.Equal(expected, BeaconDecoder.NormalizePath(raw));
    }

    [Fact]
    public void Csv_escapes_formulas_and_quotes()
    {
        Assert.Equal("\"'=SUM(A1:A2)\"", CsvWriterExtensions.Escape("=SUM(A1:A2)"));
        Assert.Equal("\"'+1\"", CsvWriterExtensions.Escape("+1"));
        Assert.Equal("\"'@user\"", CsvWriterExtensions.Escape("@user"));
        Assert.Equal("\"plain\"", CsvWriterExtensions.Escape("plain"));
        Assert.Equal("\"he said \"\"hi\"\"\"", CsvWriterExtensions.Escape("he said \"hi\""));
        Assert.Equal("\"\"\"\"", CsvWriterExtensions.Escape("\"")); // a quote alone is not a formula
        Assert.Equal("\"'\t\"", CsvWriterExtensions.Escape("\t"));
        Assert.Equal("\"\"", CsvWriterExtensions.Escape(null));
    }

    [Fact]
    public void Csv_writer_prefixes_dangerous_cells()
    {
        var writer = new StringWriter();
        writer.WriteCsvRow(new[] { "=1+1", "plain", "-5" });
        Assert.Equal("\"'=1+1\",\"plain\",\"'-5\"\r\n", writer.ToString());
    }

    private static string Escaped(string value) => CsvWriterExtensions.Escape(value);

    [Fact]
    public void Csv_dash_prefix_is_caught()
    {
        Assert.Equal("\"'-5\"", Escaped("-5"));
        Assert.Equal("\"5-5\"", Escaped("5-5")); // only a LEADING dash is a formula
        Assert.Equal("\"'-@x\"", Escaped("-@x"));
    }

    [Fact]
    public void User_agent_parses_browser_os_device()
    {
        var parser = new UserAgentParser();
        Assert.Equal(("Chrome 131", "Windows", "desktop"), parser.Parse(Chrome));
        Assert.Equal(("Safari 17", "iOS", "mobile"),
            parser.Parse("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1"));
        Assert.Equal(("Safari 17", "iPadOS", "tablet"),
            parser.Parse("Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1"));
        Assert.Equal(("Firefox 132", "Linux", "desktop"),
            parser.Parse("Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0"));
        Assert.Equal(("Edge 131", "Windows", "desktop"),
            parser.Parse("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.2903.86"));
        Assert.Equal(("unknown", "unknown", "other"), parser.Parse(null));
    }

    [Fact]
    public void Geo_without_a_database_is_null_safe()
    {
        using (var resolver = GeoResolver.Create(null))
        {
            Assert.False(resolver.Enabled);
            var place = resolver.Resolve("81.2.69.160");
            Assert.Null(place.Country);
            Assert.Null(place.City);
        }
        using (var missing = GeoResolver.Create("/tmp/analytics-wt/definitely-not-here.mmdb"))
        {
            Assert.False(missing.Enabled);
            Assert.Null(missing.Resolve("81.2.69.160").Country);
        }
        var garbage = Path.Combine("/tmp", "analytics-wt", "garbage-" + Guid.NewGuid().ToString("N") + ".mmdb");
        Directory.CreateDirectory("/tmp/analytics-wt");
        File.WriteAllText(garbage, "not an mmdb");
        using (var broken = GeoResolver.Create(garbage))
        {
            Assert.False(broken.Enabled);
            Assert.Null(broken.Resolve("81.2.69.160").Country);
        }
        File.Delete(garbage);
    }

    [Fact]
    public void Decoder_rejects_bad_ids_and_events()
    {
        var decoder = new BeaconDecoder("www.aricummings.com");
        byte[] BeaconBytes(string json) => Encoding.UTF8.GetBytes(json);

        Assert.Null(decoder.Decode(BeaconBytes("""{"v":1,"visitor":"short","journey":"journey0001","events":[{"t":"view","path":"/"}]}""")));
        Assert.Null(decoder.Decode(BeaconBytes("""{"v":1,"visitor":"visitor-with-illegal-char!","journey":"journey0001","events":[{"t":"view","path":"/"}]}""")));
        Assert.Null(decoder.Decode(BeaconBytes("""{"v":1,"visitor":"visitor0001","journey":"journey0001","events":[]}""")));
        Assert.Null(decoder.Decode(BeaconBytes("""{"v":1,"visitor":"visitor0001","journey":"journey0001"}""")));
        Assert.Null(decoder.Decode(BeaconBytes("[1,2,3]")));

        var valid = decoder.Decode(BeaconBytes("""{"v":1,"visitor":"visitor0001","journey":"journey0001","events":[{"t":"active","ms":1000}]}"""));
        Assert.NotNull(valid);
        Assert.Equal("visitor0001", valid!.Visitor);
        Assert.Single(valid.Events);
    }

    [Fact]
    public void Referrer_rules_hold()
    {
        var decoder = new BeaconDecoder("www.aricummings.com");
        Assert.Equal("example.com", decoder.NormalizeReferrer("https://example.com/path"));
        Assert.Equal("example.com", decoder.NormalizeReferrer("example.com"));
        Assert.Null(decoder.NormalizeReferrer("ftp://example.com"));
        Assert.Null(decoder.NormalizeReferrer("javascript:alert(1)"));
        Assert.Null(decoder.NormalizeReferrer("https://www.aricummings.com/page"));
        Assert.Null(decoder.NormalizeReferrer(null));
        Assert.Null(decoder.NormalizeReferrer(""));
    }
}
