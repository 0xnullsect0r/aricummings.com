namespace Ari.Features.Analytics;

/// <summary>
/// Server-side user-agent parsing into browser, OS and device, from a compact rule
/// table in the spirit of bowser's (checked newest engine token first, because older
/// tokens linger in every UA string). Beacons are untrusted, so this never throws and
/// never runs a user-controlled regex.
/// </summary>
public sealed class UserAgentParser
{
    /// <summary>Browser, OS, device ("desktop" | "mobile" | "tablet" | "other").</summary>
    public (string Browser, string Os, string Device) Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return ("unknown", "unknown", "other");
        var ua = userAgent.Trim();
        if (ua.Length > 400) ua = ua[..400];
        return (Browser(ua), Os(ua), Device(ua));
    }

    private static string Browser(string ua)
    {
        // Match newest first: Edge and friends carry "Chrome" and "Safari" tokens too.
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "Edg/");
            return version is null ? "Edge" : $"Edge {version}";
        }
        if (ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) || ua.Contains("Opera", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "OPR/") ?? Token(ua, "Version/");
            return version is null ? "Opera" : $"Opera {version}";
        }
        if (ua.Contains("SamsungBrowser/", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "SamsungBrowser/");
            return version is null ? "Samsung Internet" : $"Samsung Internet {version}";
        }
        if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) || ua.Contains("FxiOS/", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "Firefox/") ?? Token(ua, "FxiOS/");
            return version is null ? "Firefox" : $"Firefox {version}";
        }
        if (ua.Contains("CriOS/", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "CriOS/");
            return version is null ? "Chrome" : $"Chrome {version}";
        }
        if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "Chrome/");
            return version is null ? "Chrome" : $"Chrome {version}";
        }
        if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase))
        {
            var version = Token(ua, "Version/");
            return version is null ? "Safari" : $"Safari {version}";
        }
        return "other";
    }

    private static string Os(string ua)
    {
        if (ua.Contains("Windows Phone", StringComparison.OrdinalIgnoreCase)) return "Windows Phone";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPod", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPadOS";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase)) return "ChromeOS";
        if (ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "macOS";
        if (ua.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase)) return "FreeBSD";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) || ua.Contains("X11", StringComparison.OrdinalIgnoreCase)) return "Linux";
        return "other";
    }

    private static string Device(string ua)
    {
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase))
            return "tablet";
        var mobile = ua.Contains("Mobi", StringComparison.OrdinalIgnoreCase)
                     || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                     || ua.Contains("Android", StringComparison.OrdinalIgnoreCase)
                     || ua.Contains("Windows Phone", StringComparison.OrdinalIgnoreCase);
        if (mobile) return "mobile";
        var desktop = ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                      || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
                      || ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase)
                      || ua.Contains("CrOS", StringComparison.OrdinalIgnoreCase)
                      || ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                      || ua.Contains("X11", StringComparison.OrdinalIgnoreCase)
                      || ua.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase);
        return desktop ? "desktop" : "other";
    }

    /// <summary>The major version after the token, e.g. "Edg/131.0.2903.86" → "131".</summary>
    private static string? Token(string ua, string marker)
    {
        var index = ua.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var rest = ua[(index + marker.Length)..];
        var end = 0;
        while (end < rest.Length && (char.IsAsciiDigit(rest[end]) || rest[end] == '.')) end++;
        var version = rest[..end].TrimEnd('.');
        var major = version.Split('.')[0];
        return major.Length > 0 && major.All(char.IsAsciiDigit) ? major : null;
    }
}
