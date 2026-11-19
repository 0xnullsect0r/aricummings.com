using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Ari.Platform;
using Ari.Platform.Api;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ari.Features.Analytics;

/// <summary>
/// POST /api/analytics/beacon — the pinned contract from the brief. Beacons are
/// anonymous and untrusted: everything validates by shape and length, the answer is 204
/// both when accepted and when malformed-but-limited (no validation leaks), and only
/// bytes that are not JSON at all get a 400. Rate limit 120 with 60 a minute per
/// network (IPv6 keys on the /64) via the Platform limiter. Bots are classified here and
/// logged, never recorded as visits. Accepted beacons are offered to the writer channel,
/// so the request path never touches the disk.
/// </summary>
public sealed class BeaconEndpoint
{
    private readonly RateLimits _limits;
    private readonly AnalyticsStore _store;
    private readonly BotFilter _bots;
    private readonly UserAgentParser _userAgents;
    private readonly GeoResolver _geo;
    private readonly BeaconDecoder _decoder;
    private readonly ILogger<BeaconEndpoint> _logger;

    public BeaconEndpoint(Env env, RateLimits limits, AnalyticsStore store, BotFilter bots,
        UserAgentParser userAgents, GeoResolver geo, ILogger<BeaconEndpoint> logger)
    {
        _limits = limits;
        _store = store;
        _bots = bots;
        _userAgents = userAgents;
        _geo = geo;
        _logger = logger;
        _decoder = new BeaconDecoder(BeaconDecoder.SelfHostFrom(env.PublicOrigin));
    }

    /// <summary>Takes HttpRequest (not HttpContext) so the route handler is never mistaken
    /// for a RequestDelegate, which would discard the IResult.</summary>
    public static Task<IResult> Post(HttpRequest request) =>
        request.HttpContext.RequestServices.GetRequiredService<BeaconEndpoint>().Handle(request.HttpContext);

    private async Task<IResult> Handle(HttpContext http)
    {
        try
        {
            // Rate limit first, on the network, before any parsing. 204 either way.
            var (ip, networkKey, ipv6Net) = ClientNetwork(http);
            if (!_limits.TryConsume(_limits.Beacon, networkKey))
            {
                _store.CountDropped();
                return NoContent();
            }

            if (await BeaconDecoder.TryReadBodyAsync(http.Request.Body) is not { } body)
            {
                _store.CountDropped();
                return NoContent(); // over 64 KB: counted and dropped, never a leak
            }

            ValidBeacon? valid;
            try
            {
                valid = _decoder.Decode(body);
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "The beacon is not valid JSON.", code = "bad_json" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (valid is null) return NoContent(); // valid JSON, not a valid beacon

            var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var userAgent = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

            // Bots: UA heuristics and the client's own webdriver flag, logged, never a visit.
            var botReason = _bots.Classify(userAgent, valid.Webdriver);
            if (botReason is not null)
            {
                var botPath = valid.Events.FirstOrDefault(e => e.Type == "view")?.Path ?? "(none)";
                _store.OfferBot(at, ip, userAgent, botPath);
                return NoContent();
            }

            var (browser, os, device) = _userAgents.Parse(userAgent);
            var place = _geo.Enabled ? _geo.Resolve(ip) : null;

            var beacon = new IngestBeacon(
                at,
                valid.Visitor,
                valid.Journey,
                networkKey,
                ip,
                ipv6Net,
                userAgent,
                browser,
                os,
                device,
                valid.Screen,
                valid.Language,
                valid.Timezone,
                place,
                valid.Webdriver,
                valid.Events);
            _store.Offer(beacon);
            return NoContent();
        }
        catch (Exception error)
        {
            // The beacon endpoint never surfaces an error: count and move on.
            _logger.LogWarning(error, "Beacon handling failed");
            _store.CountDropped();
            return NoContent();
        }

        static NoContent NoContent() => TypedResults.NoContent();
    }

    /// <summary>
    /// The client IP (the edge already rewrote X-Forwarded-For for trusted proxies only)
    /// and the rate-limit network key: the full IPv4 address, or the /64 for IPv6.
    /// </summary>
    private static (string Ip, string NetworkKey, string? Ipv6Net) ClientNetwork(HttpContext http)
    {
        IPAddress? address = null;
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (forwarded.Length > 0 && IPAddress.TryParse(forwarded.Split(',')[0].Trim(), out var parsed))
            address = parsed;
        address ??= http.Connection.RemoteIpAddress;

        if (address is null) return ("0.0.0.0", "unknown", null);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            var network = new byte[8];
            Array.Copy(bytes, network, 8);
            var netAddress = new IPAddress(network);
            return (address.ToString(), netAddress.ToString(), netAddress.ToString());
        }

        return (address.ToString(), address.ToString(), null);
    }
}
