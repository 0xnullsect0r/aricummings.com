using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ari.Features.Analytics;

/// <summary>
/// Turns a raw beacon body into validated events. Beacons are anonymous, so everything
/// here is untrusted: every field is checked by type and length, control characters and
/// lone surrogates are stripped, paths are normalized against the known page set so
/// random URLs cannot fill the path table, and only http/https referrers survive.
/// Valid JSON that is not a valid beacon decodes to null (the endpoint answers 204);
/// bytes that are not JSON at all throw <see cref="JsonException"/> (the endpoint answers 400).
/// </summary>
public sealed partial class BeaconDecoder
{
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxEvents = 100;
    private const long MaxActiveMs = 24L * 60 * 60 * 1000;

    private static readonly string[] EventTypes = { "view", "active", "scroll", "click", "outbound", "conversion" };

    // Visitor and journey ids are opaque random strings set client-side, same alphabet either way.
    [GeneratedRegex("^[A-Za-z0-9_-]{8,40}$")]
    private static partial Regex IdPattern();

    private static readonly Regex SlugPattern = GenerateSlugPattern();

    private static Regex GenerateSlugPattern() =>
        new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly string _selfHost;

    public BeaconDecoder(string publicOriginHost) => _selfHost = publicOriginHost;

    public static string SelfHostFrom(string publicOrigin)
    {
        try
        {
            var (_, host, _) = Ari.Platform.Env.ParseOrigin(publicOrigin);
            return host;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Reads at most 64 KB as a stream with a counter (Content-Length can be absent).
    /// Null when the body is larger: counted and dropped by the caller.
    /// </summary>
    public static async Task<byte[]?> TryReadBodyAsync(Stream body)
    {
        var buffer = new byte[8192];
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (read == 0) break;
            if (writer.WrittenCount + read > MaxBodyBytes) return null;
            writer.Write(buffer.AsSpan(0, read));
        }
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>Validated beacon body, or null when the beacon is dropped.</summary>
    public ValidBeacon? Decode(byte[] body)
    {
        using var document = JsonDocument.Parse(body); // not JSON at all: JsonException -> 400
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

        var root = document.RootElement;
        if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt64(out var v) || v != 1)
            return null;
        if (!Id(root, "visitor", out var visitor)) return null;
        if (!Id(root, "journey", out var journey)) return null;
        if (!root.TryGetProperty("events", out var eventsElement) || eventsElement.ValueKind != JsonValueKind.Array)
            return null;

        string? screen = null;
        if (TryInt(root, "sw", 1, 50_000, out var sw) && TryInt(root, "sh", 1, 50_000, out var sh))
            screen = $"{sw}x{sh}";

        var language = OptionalText(root, "lang", 35);
        var timezone = OptionalText(root, "tz", 64);
        var webdriver = TryBool(root, "webdriver", out var flag) && flag;

        List<BeaconEvent> events = new();
        foreach (var item in eventsElement.EnumerateArray())
        {
            if (events.Count >= MaxEvents) break; // counted as received, the rest dropped
            if (item.ValueKind != JsonValueKind.Object) return null;
            if (!item.TryGetProperty("t", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || typeElement.GetString() is not { } type
                || !EventTypes.Contains(type, StringComparer.Ordinal))
                return null;

            BeaconEvent? decoded = type switch
            {
                "view" => DecodeView(item),
                "active" => DecodeActive(item),
                "scroll" => DecodeScroll(item),
                "click" => DecodeClick(item),
                "outbound" => DecodeLink(item, "outbound"),
                "conversion" => DecodeLink(item, "conversion"),
                _ => null,
            };
            if (decoded is null) return null;
            events.Add(decoded);
        }

        if (events.Count == 0) return null;
        return new ValidBeacon(visitor, journey, screen, language, timezone, webdriver, events);
    }

    private BeaconEvent? DecodeView(JsonElement item)
    {
        var path = RequiredText(item, "path", 500);
        if (path is null) return null;
        var title = OptionalText(item, "title", 200);
        var referrer = NormalizeReferrer(OptionalText(item, "referrer", 200));
        var campaign = OptionalText(item, "campaign", 100);
        return new BeaconEvent("view", NormalizePath(path), title, referrer, campaign, null, null, null, null, null, null, null);
    }

    private static BeaconEvent? DecodeActive(JsonElement item)
    {
        if (!item.TryGetProperty("ms", out var msElement) || msElement.ValueKind != JsonValueKind.Number
            || !msElement.TryGetInt64(out var ms) || ms < 0 || ms > MaxActiveMs)
            return null;
        return new BeaconEvent("active", null, null, null, null, ms, null, null, null, null, null, null);
    }

    private static BeaconEvent? DecodeScroll(JsonElement item)
    {
        if (!item.TryGetProperty("pct", out var pctElement) || pctElement.ValueKind != JsonValueKind.Number
            || !pctElement.TryGetDouble(out var pct) || pct < 0 || pct > 100)
            return null;
        return new BeaconEvent("scroll", null, null, null, null, null, (int)Math.Round(pct), null, null, null, null, null);
    }

    private static BeaconEvent? DecodeClick(JsonElement item)
    {
        var label = OptionalText(item, "label", 80);
        var target = OptionalText(item, "target", 120);
        if (TryNumber(item, "x", out var x) && (x < 0 || x > 100_000)) return null;
        if (TryNumber(item, "y", out var y) && (y < 0 || y > 100_000)) return null;
        var section = OptionalText(item, "section", 60);
        if (section == "__proto__") section = null; // never a section name anyone may rely on
        if (label is null && target is null) return null;
        return new BeaconEvent("click", null, null, null, null, null, null, label, target, (int?)x, (int?)y, section);
    }

    private static BeaconEvent? DecodeLink(JsonElement item, string type)
    {
        var label = OptionalText(item, "label", 80);
        var target = OptionalText(item, "target", 120);
        // The contact-success page emits a bare conversion; outbound always carries a
        // label or a target. Anything else identifies nothing and is dropped.
        if (type != "conversion" && label is null && target is null) return null;
        return new BeaconEvent(type, null, null, null, null, null, null, label, target, null, null, null);
    }

    /// <summary>
    /// Normalises anything not a known page to "(other)", so random URLs cannot fill the
    /// path table. Known pages: the home page and work pages, however they were spelled.
    /// </summary>
    public static string NormalizePath(string raw)
    {
        var path = raw.Split('?', '#')[0].Trim();
        try
        {
            if (path.Contains('%')) path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return "(other)";
        }
        path = path.Replace('\\', '/');
        var cut = path.IndexOf("://", StringComparison.Ordinal);
        if (cut > 0)
        {
            var authorityStart = cut + 3;
            var authorityEnd = path.IndexOf('/', authorityStart);
            path = authorityEnd < 0 ? "/" : path[authorityEnd..];
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Stack<string> stack = new();
        foreach (var segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }
            stack.Push(segment);
        }
        var canonical = "/" + string.Join("/", stack.Reverse()).ToLowerInvariant();
        if (canonical.Length > 200) return "(other)";
        if (canonical == "/" || canonical == "/work") return canonical; // home and the work index
        if (canonical.StartsWith("/work/", StringComparison.Ordinal)
            && canonical.LastIndexOf('/') == 5
            && SlugPattern.IsMatch(canonical[6..]))
            return canonical;
        return "(other)";
    }

    /// <summary>Referrers arrive as a host. Only http(s) referrers are kept; self-referrals drop.</summary>
    public string? NormalizeReferrer(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var value = raw.Trim();
        if (value.Length == 0 || value.Length > 200) return null;

        string host;
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            host = uri.Host;
        }
        else
        {
            // Bare host, optionally with a numeric port. Anything else
            // ("javascript:alert(1)") is not a referring host.
            host = value.Split('/')[0];
            var colon = host.IndexOf(':');
            var bare = colon < 0 ? host : host[..colon];
            var port = colon < 0 ? "" : host[(colon + 1)..];
            if (bare.Length == 0 || port.Length == 0 && colon >= 0) return null;
            if (!bare.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
            if (!port.All(char.IsAsciiDigit)) return null;
        }
        if (host.Length == 0 || host.Length > 200) return null;
        if (host.Contains('@') || host.Any(char.IsControl)) return null;
        var finalHost = host.Split(':')[0].ToLowerInvariant();
        if (finalHost.Length == 0) return null;
        if (finalHost.Equals(_selfHost, StringComparison.Ordinal)) return null; // self-referral
        return finalHost;
    }

    private static bool Id(JsonElement root, string field, out string value)
    {
        value = "";
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var candidate = element.GetString();
        if (candidate is null) return false;
        return IdPattern().IsMatch(candidate) && (value = candidate) == candidate;
    }

    private static bool TryInt(JsonElement root, string field, int min, int max, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Number)
            return false;
        if (!element.TryGetInt64(out var number)) return false;
        if (number < min || number > max) return false;
        value = (int)number;
        return true;
    }

    private static bool TryBool(JsonElement root, string field, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
            return false;
        value = element.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool TryNumber(JsonElement item, string field, out double value)
    {
        value = 0;
        if (!item.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Number)
            return false;
        return element.TryGetDouble(out value);
    }

    private static string? RequiredText(JsonElement item, string field, int max)
    {
        var value = OptionalText(item, field, max);
        return value is { Length: > 0 } ? value : null;
    }

    /// <summary>Text with control characters and lone surrogates stripped, or null when absent.</summary>
    private static string? OptionalText(JsonElement parent, string field, int max)
    {
        if (!parent.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var raw = element.GetString();
        if (raw is null) return null;
        if (raw.Length == 0) return "";
        StringBuilder builder = new(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsControl(c)) continue;
            if (char.IsSurrogate(c)) continue; // lone surrogates cannot be stored: treat as invalid
            builder.Append(c);
            if (builder.Length > max) return null;
        }
        var trimmed = builder.ToString().Trim();
        return trimmed.Length > max ? null : trimmed;
    }
}
