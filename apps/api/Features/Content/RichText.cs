using System.Text.RegularExpressions;
using Ari.Platform.Api;
using Ganss.Xss;

namespace Ari.Features.Content;

/// <summary>
/// Server-side rich-text cleaning, run at save time. Allowlist exactly: p br strong b em i u
/// ul ol li h2 h3 blockquote a; href/target/rel on links; schemes http https mailto tel.
/// Everything else loses the tag and keeps the text; script and style go with their text.
/// Hrefs are checked after control characters and HTML comments are gone, and
/// protocol-relative addresses are refused.
/// </summary>
public static partial class RichText
{
    [GeneratedRegex(@"(?:href|src)\s*=\s*[""']?[^""'>\s]*")]
    private static partial Regex LinkTargets();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]")]
    private static partial Regex ControlChars();

    // Script and style go with their text; an unclosed one ends at the end of the input.
    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>|<script\b[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlocks();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>|<style\b[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlocks();

    private static readonly HtmlSanitizer Sanitizer = Create();

    private static HtmlSanitizer Create()
    {
        var sanitizer = new HtmlSanitizer
        {
            KeepChildNodes = true, // a dropped tag keeps its text
            AllowDataAttributes = false,
        };
        sanitizer.AllowedTags.Clear(); // never the defaults: the allowlist is exact
        foreach (var tag in new[] { "p", "br", "strong", "b", "em", "i", "u", "ul", "ol", "li", "h2", "h3", "blockquote", "a" })
            sanitizer.AllowedTags.Add(tag);
        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "target", "rel" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "tel" })
            sanitizer.AllowedSchemes.Add(scheme);
        return sanitizer;
    }

    /// <summary>Throws <see cref="ApiException"/> when a link target cannot be allowed to survive.</summary>
    public static string Clean(string html)
    {
        // Control characters first: they can hide a scheme from every later reader.
        var withoutControls = ControlChars().Replace(html, "");
        var withoutScript = ScriptBlocks().Replace(withoutControls, "");
        var withoutStyle = StyleBlocks().Replace(withoutScript, "");
        var cleaned = Sanitizer.Sanitize(withoutStyle).Trim();

        // The sanitizer keeps relative hrefs; protocol-relative ones must not survive either.
        foreach (Match match in LinkTargets().Matches(cleaned))
        {
            var value = match.Value;
            var separator = value.IndexOf('=');
            if (separator < 0) continue;
            var target = value[(separator + 1)..].TrimStart(' ', '\t').TrimStart('\'', '"');
            if (target.StartsWith("//", StringComparison.Ordinal))
                throw new ApiException(400, "A link target is protocol-relative; give it a scheme.", "bad_link");
        }
        return cleaned;
    }
}
