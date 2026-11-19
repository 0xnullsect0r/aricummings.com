using System.Text;

namespace Ari.Platform.Edge;

/// <summary>
/// Answers "what does this path really mean, however it was spelled", so callers can
/// refuse. Stricter than the router is safe here because it is only used to reject.
/// </summary>
public static class Paths
{
    /// <summary>Null means refuse: malformed percent-encoding or stray percent.</summary>
    public static string? Canonical(string target)
    {
        var path = target;
        var cut = path.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) path = path[..cut];

        string decoded = path;
        for (var round = 0; round < 3; round++)
        {
            if (!decoded.Contains('%')) break;
            string next;
            try { next = Uri.UnescapeDataString(decoded); }
            catch (UriFormatException) { return null; }
            if (next == decoded) break;
            decoded = next;
        }
        if (decoded.Contains('%')) return null; // still encoded after three rounds: refuse

        if (decoded.Contains('\t') || decoded.Contains('\n') || decoded.Contains('\r')) return null;
        decoded = decoded.Trim().TrimStart('\0', '\b', '\f', '\v');
        cut = decoded.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) decoded = decoded[..cut];
        decoded = decoded.Replace('\\', '/');

        // Absolute-form scheme://host/path — drop the scheme and host.
        var schemeIndex = decoded.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0)
        {
            var authorityStart = schemeIndex + 3;
            var authorityEnd = decoded.IndexOf('/', authorityStart);
            decoded = authorityEnd < 0 ? "/" : decoded[authorityEnd..];
        }

        // Resolve . and .. segments.
        var segments = decoded.Split('/');
        var stack = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is ".") continue;
            if (segment is "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }
        var sb = new StringBuilder();
        sb.Append('/');
        sb.Append(string.Join("/", stack));

        var result = sb.ToString();
        result = result.ToLowerInvariant();
        if (result.Length > 1 && result.EndsWith('/')) result = result[..^1];
        return result;
    }

    /// <summary>The admin-prefixed path for clean admin URLs, or the path unchanged when it has no admin prefix.</summary>
    public static string? AdminPathFor(string cleanPath) =>
        cleanPath.Length == 0 || cleanPath == "/"
            ? "/admin"
            : "/admin" + cleanPath;
}
