using System.Collections.Concurrent;
using System.Net;
using QuicNet.Geolocation;

namespace NetLocate;

internal sealed class DemoNetwork : IEchoTransport
{
    public GeoPoint Truth { get; } = new(40.4168, -3.7038);
    public Catalog Catalog { get; }
    private readonly Dictionary<string, Landmark> _byAddress;
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public DemoNetwork()
    {
        var sites = new List<(string Id, double Lat, double Lon)>
        {
            ("paris",48.857,2.352), ("london",51.507,-.128), ("new-york",40.713,-74.006),
            ("tokyo",35.676,139.650), ("sydney",-33.869,151.209), ("cape-town",-33.925,18.424),
            ("singapore",1.352,103.820), ("sao-paulo",-23.551,-46.633), ("los-angeles",34.05,-118.24),
            ("nairobi",-1.29,36.82), ("berlin",52.52,13.405), ("stockholm",59.33,18.07),
            ("barcelona",41.387,2.170), ("lisbon",38.722,-9.139), ("madrid",40.4168,-3.7038)
        };
        for (int y = -4; y <= 4; y++)
            for (int x = -4; x <= 4; x++)
                if (x != 0 || y != 0) sites.Add(($"local-{x + 4}-{y + 4}", Truth.Latitude + y * .2, Truth.Longitude + x * .2));
        Catalog = new(sites.Select((s, i) => new Landmark(s.Id, "192.0.2." + (i + 1),
            new(s.Lat, s.Lon), "synthetic-demo")
        { Name = s.Id, SiteId = s.Id, LocationVerified = true, UncertaintyKm = 2 }).ToList())
        { CreatedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        _byAddress = Catalog.Landmarks.ToDictionary(a => a.Address, StringComparer.Ordinal);
    }

    public Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string key = address.ToString();
        var anchor = _byAddress[key];
        int attempt = _attempts.AddOrUpdate(key, 1, (_, old) => old + 1);
        double floor = Geography.DistanceKm(anchor.Position, Truth) / 100 * 1.12 + .35;
        double jitter = attempt % 3 == 1 ? 4 : attempt % 3 == 2 ? 0 : 1;
        return Task.FromResult(new EchoResult("Success", key, floor + jitter));
    }
}
