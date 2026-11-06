using System.Net;
using QuicNet.Geolocation;

namespace QuicNet.Networking;

internal sealed record PeerLocation(string status, string? city = null, string? region = null, string? country = null, string? countryCode = null, bool advertised = false)
{
    public string? municipality { get; init; }
    public string? source { get; init; }
    public string? precision { get; init; }
}
internal sealed record GeolocationStatus(bool ready, bool downloading, long downloadedBytes, string? databaseDate, string? error)
{
    public bool embeddedReady { get; init; } = true;
    public AutomaticGeolocationStatus? connection { get; init; }
}

// Uses the same embedded IPTrust snapshot as the latency workbench. Existing
// DB-IP files in the settings directory cannot override this catalogue.
internal sealed class LocalIpGeolocation : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, PeerLocation> _cache = new();
    private readonly EmbeddedCatalog _data = EmbeddedCatalog.Default;
    private bool _disposed;
    private string? _error;

    internal GeolocationStatus Status
    {
        get
        {
            lock (_sync)
                return new(_data.HasIpLocationDatabase, false, 0, _data.IpLocationDatabaseDate, _error)
                { embeddedReady = _data.HasIpLocationDatabase };
        }
    }
    internal static bool IsPublic(IPAddress address) => EmbeddedCatalog.IsPublic(address);

    internal PeerLocation Locate(bool tor, string? endpoint, IEnumerable<string>? announced = null)
    {
        if (tor) return new("tor");
        IPAddress? address = null;
        bool advertised = false;
        if (endpoint != null && IPEndPoint.TryParse(endpoint, out var ep)) address = ep.Address;
        else if (IPAddress.TryParse(endpoint, out var ip)) address = ip;
        if (address == null)
        {
            advertised = true;
            address = announced?.Select(s => IPAddress.TryParse(s, out var a) ? a : null).FirstOrDefault(a => a != null && IsPublic(a));
        }
        if (address == null) return new("unknown");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (!IsPublic(address)) return new("local");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_cache.TryGetValue(address, out var result))
            {
                try
                {
                    var published = _data.Lookup(address);
                    var locality = published != null && !string.IsNullOrWhiteSpace(published.City)
                        ? LocalityCatalog.Default.MatchPublished(published) : null;
                    result = published == null ? new("unknown") : new("located",
                        EmptyToNull(published.City), EmptyToNull(published.Region), CountryName(published.Country), published.Country)
                    {
                        municipality = EmptyToNull(locality?.Municipality),
                        source = published.Source,
                        precision = string.IsNullOrWhiteSpace(published.City) ? "ip-country" : "ip-published"
                    };
                }
                catch (Exception ex) { _error = "Error al consultar la base local: " + ex.Message; result = new("unknown"); }
                if (_cache.Count >= 2048) _cache.Clear();
                _cache[address] = result;
            }
            return result with { advertised = advertised };
        }
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string CountryName(string code)
    {
        try { return new System.Globalization.RegionInfo(code).DisplayName; }
        catch (ArgumentException) { return code; }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _cache.Clear(); }
    }
}
