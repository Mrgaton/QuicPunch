using System.Text.Json;

namespace QuicNet.Geolocation;

/// <summary>Empirical CBG bestline from public inter-anchor measurements; never a physical guarantee.</summary>
public sealed record DelayCalibration(string LandmarkId, string Address, double SlopeMsPerKm, double InterceptMs,
    int Sites, long MeasurementId, DateTimeOffset CapturedUtc)
{
    public bool Applicable(string address, DateTimeOffset now) => Address == address && Sites >= 8 &&
        double.IsFinite(SlopeMsPerKm) && SlopeMsPerKm >= 2 / 299.792458 && SlopeMsPerKm <= 1 &&
        double.IsFinite(InterceptMs) && InterceptMs >= 0 &&
        now - CapturedUtc <= TimeSpan.FromDays(30) && CapturedUtc - now <= TimeSpan.FromDays(1);
}
public sealed record DelayCalibrationCatalog(int SchemaVersion, string Method, string Source, DelayCalibration[] Lines);

internal static class EmbeddedDelayCalibration
{
    private static readonly Lazy<IReadOnlyDictionary<string, DelayCalibration>> Data = new(() =>
    {
        if (!EmbeddedCatalog.HasResource("latency-calibration.json")) return new Dictionary<string, DelayCalibration>();
        using var stream = EmbeddedCatalog.Resource("latency-calibration.json");
        var catalog = JsonSerializer.Deserialize(stream, GeoJsonContext.Default.DelayCalibrationCatalog);
        if (catalog?.SchemaVersion != 1 || catalog.Lines.Length > 20000) throw new InvalidDataException("Invalid delay calibration snapshot");
        return catalog.Lines.GroupBy(l => l.LandmarkId).ToDictionary(g => g.Key, g => g.First());
    });
    public static Landmark Attach(Landmark a) => a.DelayCalibration is null && Data.Value.TryGetValue(a.Id, out var line) &&
        line.Applicable(a.Address, DateTimeOffset.UtcNow) ? a with { DelayCalibration = line } : a;
}
