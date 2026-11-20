using System.Text.Json;
using Ari.Platform.Api;

namespace Ari.Features.Content;

/// <summary>
/// One field of a document kind: a key in stored order plus the rule its value must follow.
/// </summary>
public sealed record DocField(string Key, FieldRule Rule);

/// <summary>
/// Small rule objects that validate and normalize a document in one walk: the writer emits
/// only known fields, in schema order, so stored JSON is canonical and an older image can
/// still read a newer document. Unknown keys in the input are ignored, never rejected —
/// adding a field never rewrites stored documents and rollback of the image stays safe.
/// </summary>
public abstract record FieldRule
{
    /// <summary>Validate value and write its normalized form. Errors are collected, not thrown.</summary>
    internal abstract void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors);
}

public enum StringKind { Plain, RichText, UrlHttps, UrlHttp, UrlHttpsOrMailto, Email, MediaId }

public sealed record StringRule(int Min = 0, int Max = 5000, StringKind Kind = StringKind.Plain) : FieldRule
{
    internal override void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: must be text");
            return;
        }
        var text = value.GetString() ?? "";
        if (text.Length < Min) errors.Add($"{path}: must be at least {Min} characters");
        if (text.Length > Max) errors.Add($"{path}: must be at most {Max} characters");

        switch (Kind)
        {
            case StringKind.RichText:
                text = Sanitize(path, text, errors);
                break;
            case StringKind.UrlHttps:
                if (!HttpsUrl(text)) errors.Add($"{path}: must be an https address");
                break;
            case StringKind.UrlHttp:
                if (!HttpUrl(text)) errors.Add($"{path}: must be an http or https address");
                break;
            case StringKind.UrlHttpsOrMailto:
                if (!HttpsUrl(text) && !text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{path}: must be an https or mailto address");
                break;
            case StringKind.Email:
                if (!text.Contains('@') || text.Length is < 5 or > 254 || text.Contains(' '))
                    errors.Add($"{path}: must be an email address");
                break;
            case StringKind.MediaId:
                if (!MediaIds.Pattern().IsMatch(text)) errors.Add($"{path}: must be a media id (16 hex characters)");
                break;
        }

        writer.WriteStringValue(text);
    }

    private static string Sanitize(string path, string text, List<string> errors)
    {
        try
        {
            return RichText.Clean(text);
        }
        catch (ApiException)
        {
            errors.Add($"{path}: contains a link target that is not allowed");
            return text;
        }
    }

    private static bool HttpUrl(string text) =>
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool HttpsUrl(string text) => text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public sealed record ChoiceRule(params string[] Options) : FieldRule
{
    internal override void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: must be one of " + string.Join(", ", Options));
            return;
        }
        var text = value.GetString() ?? "";
        if (!Options.Contains(text)) errors.Add($"{path}: must be one of " + string.Join(", ", Options));
        writer.WriteStringValue(text);
    }
}

public sealed record ArrayRule(DocField Item, int MinItems = 0, int MaxItems = 100) : FieldRule
{
    internal override void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        // Contract: every rule writes exactly one complete value, whatever happened, so
        // the enclosing writer never sees a dangling property name. A fallback value is
        // fine because the whole result is discarded when any error was found.
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}: must be a list");
            writer.WriteStartArray();
            writer.WriteEndArray();
            return;
        }
        var before = errors.Count;
        var elements = value.EnumerateArray().ToList();
        if (elements.Count < MinItems) errors.Add($"{path}: needs at least {MinItems} items");
        if (elements.Count > MaxItems) errors.Add($"{path}: allows at most {MaxItems} items");

        using var buffer = new MemoryStream();
        using var inner = new Utf8JsonWriter(buffer);
        inner.WriteStartArray();
        foreach (var (element, index) in elements.Select((element, index) => (element, index)))
            Item.Rule.Write(inner, element, $"{path}[{index}]", errors);
        inner.WriteEndArray();
        inner.Flush();
        if (errors.Count == before) writer.WriteRawValue(buffer.ToArray(), skipInputValidation: true);
        else
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
        }
    }
}

public sealed record ObjectRule(params DocField[] Fields) : FieldRule
{
    internal override void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: must be an object");
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }
        var before = errors.Count;
        using var buffer = new MemoryStream();
        using var inner = new Utf8JsonWriter(buffer);
        inner.WriteStartObject();
        foreach (var field in Fields)
        {
            if (!value.TryGetProperty(field.Key, out var child))
            {
                if (field.Rule is not OptionalRule) errors.Add($"{path}.{field.Key}: is required");
                continue;
            }
            if (child.ValueKind == JsonValueKind.Null)
            {
                if (field.Rule is not OptionalRule) errors.Add($"{path}.{field.Key}: is required");
                continue;
            }
            var optional = field.Rule is OptionalRule;
            if (optional
                && field.Rule is OptionalRule { Inner: ArrayRule array }
                && child.ValueKind == JsonValueKind.Array
                && !child.EnumerateArray().Any())
                continue; // default empty means the key stays missing
            inner.WritePropertyName(field.Key);
            field.Rule.Write(inner, child, $"{path}.{field.Key}", errors);
        }
        inner.WriteEndObject();
        inner.Flush();
        if (errors.Count == before) writer.WriteRawValue(buffer.ToArray(), skipInputValidation: true);
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }
}

/// <summary>The key may be missing; when present the inner rule applies. "Default empty means missing".</summary>
public sealed record OptionalRule(FieldRule Inner) : FieldRule
{
    internal override void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors) =>
        Inner.Write(writer, value, path, errors);
}

/// <summary>
/// One flexible content block, discriminated by "type": text (heading?, html), photos
/// (items[] of image/alt?/caption?) or link (label, url). Blocks may be saved blank so a
/// draft can be half written; an address with no label is refused.
/// </summary>
public sealed record SectionBlockRule : FieldRule
{
    internal override void Write(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: must be a block with a type");
            writer.WriteStartObject();
            writer.WriteEndObject();
            return;
        }
        var type = typeElement.GetString();
        var before = errors.Count;
        using var buffer = new MemoryStream();
        using var inner = new Utf8JsonWriter(buffer);
        inner.WriteStartObject();
        inner.WriteString("type", type);
        switch (type)
        {
            case "text":
                WriteText(inner, value, path, errors);
                break;
            case "photos":
                WritePhotos(inner, value, path, errors);
                break;
            case "link":
                WriteLink(inner, value, path, errors);
                break;
            default:
                errors.Add($"{path}.type: must be text, photos or link");
                break;
        }
        inner.WriteEndObject();
        inner.Flush();
        // The block object is always complete; it is only kept when nothing went wrong.
        if (errors.Count == before) writer.WriteRawValue(buffer.ToArray(), skipInputValidation: true);
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
    }

    private static void WriteText(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        var heading = "";
        if (value.TryGetProperty("heading", out var headingElement) && headingElement.ValueKind == JsonValueKind.String)
            heading = headingElement.GetString() ?? "";
        var html = "";
        if (value.TryGetProperty("html", out var htmlElement) && htmlElement.ValueKind == JsonValueKind.String)
            html = htmlElement.GetString() ?? "";
        if (heading.Length > 300) errors.Add($"{path}.heading: must be at most 300 characters");
        if (html.Length > 50_000) errors.Add($"{path}.html: must be at most 50000 characters");

        string cleaned = "";
        if (html.Length > 0)
        {
            try { cleaned = RichText.Clean(html); }
            catch (ApiException) { errors.Add($"{path}.html: contains a link target that is not allowed"); }
        }
        // A completely blank block is allowed (skipped by pages); a heading alone is not kept.
        if (errors.Count > 0) return;
        if (heading.Length > 0) writer.WriteString("heading", heading);
        if (cleaned.Length > 0) writer.WriteString("html", cleaned);
    }

    private static void WritePhotos(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        if (!value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.items: must be a list");
            return;
        }
        var elements = items.EnumerateArray().ToList();
        if (elements.Count > 50) errors.Add($"{path}.items: allows at most 50 photos");

        using var buffer = new MemoryStream();
        using var inner = new Utf8JsonWriter(buffer);
        inner.WriteStartArray();
        foreach (var (item, index) in elements.Select((item, index) => (item, index)))
        {
            var itemPath = $"{path}.items[{index}]";
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("image", out var image)
                || image.ValueKind != JsonValueKind.String
                || !MediaIds.Pattern().IsMatch(image.GetString() ?? ""))
            {
                errors.Add($"{itemPath}.image: must be a media id (16 hex characters)");
                continue;
            }
            inner.WriteStartObject();
            inner.WriteString("image", image.GetString());
            WriteOptionalShort(inner, item, "alt", 500, itemPath, errors);
            WriteOptionalShort(inner, item, "caption", 500, itemPath, errors);
            inner.WriteEndObject();
        }
        inner.WriteEndArray();
        inner.Flush();
        if (errors.Count > 0) return;
        writer.WritePropertyName("items");
        writer.WriteRawValue(buffer.ToArray(), skipInputValidation: true);
    }

    private static void WriteLink(Utf8JsonWriter writer, JsonElement value, string path, List<string> errors)
    {
        var label = value.TryGetProperty("label", out var labelElement) && labelElement.ValueKind == JsonValueKind.String
            ? labelElement.GetString() ?? ""
            : "";
        var url = value.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString() ?? ""
            : "";
        if (label.Length is < 1 or > 300) errors.Add($"{path}.label: must be 1 to 300 characters");
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{path}.url: must be an https or mailto address");
        if (url.Length > 1000) errors.Add($"{path}.url: must be at most 1000 characters");
        if (errors.Count > 0) return;
        writer.WriteString("label", label);
        writer.WriteString("url", url);
    }

    private static void WriteOptionalShort(Utf8JsonWriter writer, JsonElement item, string key, int max, string path, List<string> errors)
    {
        if (!item.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String) return;
        var text = element.GetString() ?? "";
        if (text.Length > max) errors.Add($"{path}.{key}: must be at most {max} characters");
        if (text.Length > 0) writer.WriteString(key, text);
    }
}

/// <summary>Document keys and their shapes. A slug can never change: it is the key.</summary>
public static class DocumentSchema
{
    public static readonly string[] ReservedSlugs = { "admin", "api", "media", "account", "checkout", "cart" };

    private static readonly System.Text.RegularExpressions.Regex SlugPattern =
        new("^[a-z0-9]+(?:-[a-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsValidKey(string key, out string? problem)
    {
        problem = null;
        if (key == "site" || key == "page:home") return true;
        if (key.StartsWith("work:", StringComparison.Ordinal))
        {
            var slug = key["work:".Length..];
            if (slug.Length is < 2 or > 60 || !SlugPattern.IsMatch(slug))
            {
                problem = "work slugs are lowercase letters, digits and single hyphens";
                return false;
            }
            if (ReservedSlugs.Contains(slug))
            {
                problem = $"'{slug}' is reserved";
                return false;
            }
            return true;
        }
        problem = "document keys are 'site', 'page:home' or 'work:<slug>'";
        return false;
    }

    public static DocField[] For(string key) => key switch
    {
        "site" => Site,
        "page:home" => Home,
        _ => key.StartsWith("work:", StringComparison.Ordinal) ? Work : throw new ApiException(400, "Unknown document kind.", "bad_key"),
    };

    private static readonly DocField[] Site =
    {
        new("name", new StringRule(1, 200)),
        new("tagline", new StringRule(0, 300)),
        new("contactEmail", new StringRule(5, 254, StringKind.Email)),
        new("githubHandle", new StringRule(1, 100)),
        new("location", new StringRule(1, 200)),
        new("footerLicense", new StringRule(1, 100)),
        new("seo", new OptionalRule(new ObjectRule(
            new DocField("title", new OptionalRule(new StringRule(0, 200))),
            new DocField("description", new OptionalRule(new StringRule(0, 320)))))),
    };

    private static readonly DocField[] Home =
    {
        new("hero", new ObjectRule(
            new DocField("kicker", new StringRule(0, 200)),
            new DocField("lines", new ArrayRule(new DocField("line", new StringRule(1, 300)), MinItems: 1, MaxItems: 6)),
            new DocField("roles", new OptionalRule(new ArrayRule(new DocField("role", new StringRule(1, 100)), MaxItems: 4))),
            new DocField("intro", new OptionalRule(new StringRule(0, 2000))))),
        new("about", new ObjectRule(
            new DocField("paras", new ArrayRule(new DocField("para", new StringRule(1, 2000)), MinItems: 1, MaxItems: 20)),
            new DocField("facts", new OptionalRule(new ArrayRule(new DocField("fact", new StringRule(1, 200)), MaxItems: 20))))),
        new("clientIntro", new OptionalRule(new StringRule(0, 2000))),
        new("alsoBuilding", new OptionalRule(new ArrayRule(
            new DocField("item", new ObjectRule(
                new DocField("name", new StringRule(1, 100)),
                new DocField("url", new OptionalRule(new StringRule(0, 500, StringKind.UrlHttps))),
                new DocField("note", new OptionalRule(new StringRule(0, 300))))),
            MaxItems: 30))),
        new("stack", new ArrayRule(
            new DocField("group", new ObjectRule(
                new DocField("label", new StringRule(1, 100)),
                new DocField("items", new ArrayRule(new DocField("item", new StringRule(1, 60)), MinItems: 1, MaxItems: 30)))),
            MinItems: 1, MaxItems: 12)),
        new("workOrder", new ArrayRule(new DocField("slug", new StringRule(1, 60)), MaxItems: 100)),
    };

    private static readonly DocField[] Work =
    {
        new("title", new StringRule(1, 200)),
        new("status", new StringRule(1, 100)),
        new("category", new ChoiceRule("oss", "client")),
        new("year", new StringRule(1, 40)),
        new("summary", new StringRule(1, 1000)),
        new("links", new OptionalRule(new ArrayRule(
            new DocField("link", new ObjectRule(
                new DocField("label", new StringRule(1, 200)),
                new DocField("url", new StringRule(1, 1000, StringKind.UrlHttp)))),
            MaxItems: 20))),
        new("tech", new OptionalRule(new ArrayRule(new DocField("item", new StringRule(1, 60)), MaxItems: 60))),
        new("repo", new OptionalRule(new ObjectRule(
            new DocField("url", new StringRule(1, 1000, StringKind.UrlHttps)),
            new DocField("license", new StringRule(1, 100))))),
        new("sections", new OptionalRule(new ArrayRule(new DocField("section", new SectionBlockRule()), MaxItems: 60))),
    };

    /// <summary>Validate a document against its kind and return canonical JSON, or the field problems.</summary>
    public static string Normalize(string key, JsonElement document, out IReadOnlyList<string> problems)
    {
        var errors = new List<string>();
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var field in For(key))
        {
            if (!document.TryGetProperty(field.Key, out var value))
            {
                if (field.Rule is not OptionalRule) errors.Add($"{field.Key}: is required");
                continue;
            }
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (field.Rule is not OptionalRule) errors.Add($"{field.Key}: is required");
                continue;
            }
            var optional = field.Rule is OptionalRule;
            if (optional
                && field.Rule is OptionalRule { Inner: ArrayRule array }
                && value.ValueKind == JsonValueKind.Array
                && !value.EnumerateArray().Any())
                continue;
            writer.WritePropertyName(field.Key);
            field.Rule.Write(writer, value, field.Key, errors);
        }
        writer.WriteEndObject();
        writer.Flush();
        problems = errors;
        return errors.Count > 0 ? "{}" : System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Media ids: first 16 hex characters of the SHA-256 of the original file.</summary>
public static partial class MediaIds
{
    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{16}$")]
    public static partial System.Text.RegularExpressions.Regex Pattern();
}
