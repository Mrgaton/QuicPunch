namespace QuicNet.Geolocation.App.Diagnostics;

internal static class SyntheticNetwork
{
    public static (Catalog Catalog, MeasurementSession Session, GeoPoint Truth) Create()
    {
        var sites = new (string Id, double Lat, double Lon)[]
        {
            ("barcelona", 41.387, 2.170), ("madrid", 40.416, -3.704), ("paris", 48.857, 2.352),
            ("london", 51.507, -0.128), ("new-york", 40.713, -74.006), ("bogota", 4.711, -74.072),
            ("tokyo", 35.676, 139.650), ("sydney", -33.869, 151.209), ("cape-town", -33.925, 18.424),
            ("singapore", 1.352, 103.820), ("valencia", 39.470, -0.376), ("alicante", 38.346, -0.491),
            ("castellon", 39.987, -0.050), ("zaragoza", 41.649, -0.889)
        };
        var catalog = new Catalog(sites.Select((s, i) => new Landmark(s.Id, "192.0.2." + (i + 1), new(s.Lat, s.Lon), "synthetic-fixture")
            { LocationVerified = true, UncertaintyKm = 2, SiteId = s.Id }).ToList());
        var truth = new GeoPoint(39.47, -0.376);
        var session = new MeasurementSession("synthetic-valencia", "synthetic-no-network", "ipv4", []) { Synthetic = true };
        return (catalog, session, truth);
    }

    public static Observation Observe(Landmark anchor, GeoPoint truth)
    {
        // Simulates fibre propagation with a small route detour, processing delay and queueing.
        double floor = Geography.DistanceKm(anchor.Position, truth) * 2 / 200 * 1.12 + 0.3;
        return new(anchor.Id, anchor.Address, DateTimeOffset.UtcNow,
            [new(floor + 4, "Success"), new(floor, "Success"), new(null, "TimedOut"), new(floor + 1, "Success")]);
    }
}
