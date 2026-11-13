using System.Text.Json.Serialization;

namespace QuicNet.Geolocation.App.Serialization;

public sealed record ReadyMessage(string Kind, string Url, string Version);

public sealed record MapMarker(double X, double Y, string Label);

public sealed record MapUpdate(bool Reset, SpatialSupport? Heat, GeoPoint? Center, double? Radius, MapMarker[] Markers)
{
    public GeoPoint? Estimate { get; init; }
    public double? EstimateRadius { get; init; }
    public PublishedLocation? Prior { get; init; }
    public SpatialSupport? ConditionalHeat { get; init; }
    public GeoPoint? ModelPoint { get; init; }
    public LandmarkMeasurement[] References { get; init; } = [];
    public LocalityCandidate[] Places { get; init; } = [];
    public static MapUpdate Create(LocationUpdate? update, bool reset) => new(reset, update?.Support,
        update?.Region?.Representative, update?.Region?.CoveringRadiusKm,
        (update?.Routers ?? []).GroupBy(r => (Math.Round(r.Published.Position.Latitude, 2), Math.Round(r.Published.Position.Longitude, 2)))
            .Select(g => g.MinBy(r => r.RttMs)!).Select(r => new MapMarker(r.Published.Position.Longitude, r.Published.Position.Latitude,
                $"{r.Published.City} · {r.RttMs:F2} ms · ubicación publicada sin verificar")).ToArray())
        { Prior = update?.DatabasePrior, ConditionalHeat = update?.RegionalRefinement is { Region.Status: "candidate-region" } conditional ? conditional.Support : null,
          ModelPoint = update?.Localities?.Fit?.Position, References = update?.Enhanced?.Measurements.ToArray() ?? [],
          Places = update?.Localities?.Candidates.ToArray() ?? [] };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadyMessage))]
[JsonSerializable(typeof(MapUpdate))]
public partial class AppJsonContext : JsonSerializerContext { }
