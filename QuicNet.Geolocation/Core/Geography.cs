namespace QuicNet.Geolocation;

public static class Geography
{
    public const double EarthRadiusKm = 6371.0088;
    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        double lat1 = a.Latitude * Math.PI / 180, lat2 = b.Latitude * Math.PI / 180;
        double dlat = lat2 - lat1, dlon = (b.Longitude - a.Longitude) * Math.PI / 180;
        double h = Math.Pow(Math.Sin(dlat / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dlon / 2), 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }

    public static IEnumerable<GeoCell> WorldCells()
    {
        for (int lat = -90; lat < 90; lat += 30)
            for (int lon = -180; lon < 180; lon += 30)
                yield return new(lat, lon, lat + 30, lon + 30);
    }
}
