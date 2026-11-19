namespace Ari.Features.Analytics;

/// <summary>The validated body of one beacon: the ids and client facts, plus normalized events.</summary>
public sealed record ValidBeacon(
    string Visitor,
    string Journey,
    string? Screen,
    string? Language,
    string? Timezone,
    bool Webdriver,
    IReadOnlyList<BeaconEvent> Events);

/// <summary>One normalized event from a beacon. Client times never arrive: the server clocks everything.</summary>
public sealed record BeaconEvent(
    string Type,          // view | active | scroll | click | outbound | conversion
    string? Path,         // view
    string? Title,        // view
    string? Referrer,     // view
    string? Campaign,     // view
    long? ActiveMs,       // active
    int? ScrollPct,       // scroll
    string? Label,        // click | outbound | conversion
    string? Target,       // click | outbound
    int? X,               // click
    int? Y,               // click
    string? Section);     // click

/// <summary>A whole validated beacon, plus the request facts the endpoint attaches later.</summary>
public sealed record IngestBeacon(
    long At,
    string Visitor,
    string Journey,
    string NetworkKey,
    string Ip,
    string? Ipv6Net,
    string? UserAgent,
    string Browser,
    string Os,
    string Device,
    string? Screen,
    string? Language,
    string? Timezone,
    GeoPlace? Geo,
    bool Webdriver,
    IReadOnlyList<BeaconEvent> Events)
{
    /// <summary>Events count against the bounded buffer, so this is the beacon's weight.</summary>
    public int Weight => 1 + Events.Count;
}
