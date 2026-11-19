using System.Text.RegularExpressions;

namespace Ari.Features.Analytics;

/// <summary>
/// Classifies beacons as bot or human. Order matters and is pinned by the analytics
/// reference: empty user agent, headless, the vendored isbot list, then the client's
/// webdriver flag. A regex timeout must never turn a crafted string into a visitor:
/// one retry, and a second timeout is itself treated as a bot.
/// </summary>
public sealed class BotFilter
{
    // ECMAScript semantics so the vendored JavaScript pattern means what it meant upstream.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex _isBot;

    public BotFilter()
    {
        _isBot = new Regex(BotPatterns.IsBot, RegexOptions.ECMAScript | RegexOptions.IgnoreCase, MatchTimeout);
    }

    /// <summary>Null means not a bot; otherwise the reason recorded on the bot hit.</summary>
    public string? Classify(string? userAgent, bool webdriverFlag)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "empty";
        if (userAgent.Contains("headless", StringComparison.OrdinalIgnoreCase)) return "headless";

        if (IsBotListMatch(userAgent)) return "isbot";

        return webdriverFlag ? "webdriver" : null;
    }

    private bool IsBotListMatch(string userAgent)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _isBot.IsMatch(userAgent);
            }
            catch (RegexMatchTimeoutException)
            {
                // The timeout is wall-clock, so one miss proves nothing: retry once.
                // A second timeout means a crafted string: a bot, never a visitor.
                if (attempt >= 1) return true;
            }
        }
    }
}
