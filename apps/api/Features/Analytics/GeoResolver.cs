using System.Net;
using MaxMind.Db;

namespace Ari.Features.Analytics;

/// <summary>Where a visit came from. Null fields mean unknown, never an error.</summary>
public sealed record GeoPlace(string? Country, string? Region, string? City, string? Network);

/// <summary>
/// Local-only geo lookup against an mmdb file (GeoLite2 City, or any City+ASN file).
/// When no file is configured or present, every lookup returns null and nothing is ever
/// sent anywhere. Any failure leaves the location blank rather than failing a visit.
/// </summary>
public sealed class GeoResolver : IDisposable
{
    public const string Attribution = "IP geolocation by MaxMind (GeoLite2)";

    private readonly Reader? _reader;
    private readonly object _gate = new();

    private GeoResolver(Reader? reader) => _reader = reader;

    public static GeoResolver Create(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new GeoResolver(null);
        try
        {
            if (!File.Exists(path)) return new GeoResolver(null);
            return new GeoResolver(new Reader(path, FileAccessMode.MemoryMapped));
        }
        catch
        {
            // A broken database file must not take the site down; geo fields stay null.
            return new GeoResolver(null);
        }
    }

    public bool Enabled => _reader is not null;

    public GeoPlace Resolve(string? ip)
    {
        if (_reader is null || string.IsNullOrEmpty(ip)) return Null;
        try
        {
            if (!IPAddress.TryParse(ip, out var address)) return Null;
            lock (_gate)
            {
                var record = _reader.Find<GeoRecord>(address);
                if (record is null) return Null;
                var country = record.Country?.IsoCode;
                var subdivision = record.Subdivisions is { Count: > 0 } ? record.Subdivisions[0]?.IsoCode : null;
                var city = record.City?.Names is { Count: > 0 } names
                    ? names.GetValueOrDefault("en") ?? names.Values.FirstOrDefault()
                    : null;
                var network = record.Traits?.AutonomousSystemOrganization;
                return new GeoPlace(country, subdivision, city, network);
            }
        }
        catch
        {
            return Null;
        }
    }

    private static GeoPlace Null => new(null, null, null, null);

    public void Dispose() => _reader?.Dispose();

    // mmdb field names are lowercase and matched to constructor parameters by name.
    private sealed class GeoRecord
    {
        [Constructor]
        public GeoRecord(GeoCountry? country, IReadOnlyList<GeoSubdivision?>? subdivisions, GeoCity? city, GeoTraits? traits)
        {
            Country = country;
            Subdivisions = subdivisions;
            City = city;
            Traits = traits;
        }

        public GeoCountry? Country { get; }
        public IReadOnlyList<GeoSubdivision?>? Subdivisions { get; }
        public GeoCity? City { get; }
        public GeoTraits? Traits { get; }
    }

    private sealed class GeoCountry
    {
        [Constructor]
        public GeoCountry(string? iso_code) => IsoCode = iso_code;

        public string? IsoCode { get; }
    }

    private sealed class GeoSubdivision
    {
        [Constructor]
        public GeoSubdivision(string? iso_code) => IsoCode = iso_code;

        public string? IsoCode { get; }
    }

    private sealed class GeoCity
    {
        [Constructor]
        public GeoCity(IReadOnlyDictionary<string, string>? names) => Names = names;

        public IReadOnlyDictionary<string, string>? Names { get; }
    }

    private sealed class GeoTraits
    {
        [Constructor]
        public GeoTraits(string? autonomous_system_organization) => AutonomousSystemOrganization = autonomous_system_organization;

        public string? AutonomousSystemOrganization { get; }
    }
}
