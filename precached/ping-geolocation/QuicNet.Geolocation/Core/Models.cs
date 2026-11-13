using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QuicNet.Geolocation;

public sealed record GeoPoint(double Latitude, double Longitude)
{
    public void Validate()
    {
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude) || Math.Abs(Latitude) > 90 || Math.Abs(Longitude) > 180)
            throw new InvalidDataException("Coordenadas fuera de rango.");
    }
}

public sealed record Landmark(string Id, string Address, GeoPoint Position, string Source)
{
    public string? SiteId { get; init; }
    /// <summary>Optional local prefix-to-AS metadata; scheduling hint only, never a geographic constraint.</summary>
    public uint? Asn { get; init; }
    /// <summary>Optional provenance. A declared location is probeable but never a primary distance bound.</summary>
    public string? CoordinateEvidence { get; init; }
    public string? Name { get; init; }
    public string? CountryCode { get; init; }
    public string? RegionName { get; init; }
    /// <summary>Frozen with the catalogue/session for reproducible conditional CBG replay.</summary>
    public DelayCalibration? DelayCalibration { get; init; }
    private string _kind = "anchor";
    public string Kind { get => _kind; init => _kind = value ?? "anchor"; }
    public bool LocationVerified { get; init; }
    public bool Anycast { get; init; }
    /// <summary>Opt-in service intended for external TCP measurement; never inferred from an open port.</summary>
    public bool TcpProbeAllowed { get; init; }
    public int TcpPort { get; init; } = 443;
    public DateTimeOffset? VerifiedUtc { get; init; }
    public string? MunicipalityCode { get; init; }
    public string? AccessTechnology { get; init; }
    public double UncertaintyKm { get; init; } = 25;
    public bool Usable => LocationVerified && !Anycast;
    [JsonIgnore]
    public bool ProbeEligible => !Anycast && (LocationVerified || CoordinateEvidence == "operator-declared");

    public void Validate()
    {
        if (Position == null) throw new InvalidDataException("Faltan coordenadas: " + Id);
        Position.Validate();
        if (string.IsNullOrWhiteSpace(Id) || !IPAddress.TryParse(Address, out _) || string.IsNullOrWhiteSpace(Source) ||
            Kind is not ("anchor" or "router") || TcpPort is < 1 or > 65535 || !double.IsFinite(UncertaintyKm) || UncertaintyKm < 0)
            throw new InvalidDataException("Referencia inválida: " + Id);
    }
}

public sealed record Catalog(List<Landmark> Landmarks)
{
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public void Validate()
    {
        if (Landmarks == null || Landmarks.Count > 20_000 || Landmarks.Any(a => a == null)) throw new InvalidDataException("Catálogo inválido o demasiado grande.");
        foreach (var item in Landmarks) item.Validate();
        if (Landmarks.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != Landmarks.Count)
            throw new InvalidDataException("IDs de referencias duplicados.");
    }
}

public sealed record ProbeSample(double? RttMs, string Status)
{
    /// <summary>elapsed-upper-v1 brackets the full asynchronous send/receive with Stopwatch, including local overhead.</summary>
    public string? TimingMethod { get; init; }
}
public sealed record RouteHop(int Ttl, string? Address, double? RttMs, string Status)
{ public int Sweep { get; init; } }
public sealed record Observation(string LandmarkId, string Address, DateTimeOffset MeasuredUtc, List<ProbeSample> Samples)
{
    public List<RouteHop> Route { get; init; } = [];
    public double? MinimumRttMs => Samples.Where(s => s.Status == "Success" && s.RttMs is >= 0 && double.IsFinite(s.RttMs.Value))
        .Select(s => s.RttMs).Min();
}

public sealed record MeasurementSession(string TargetId, string NetworkContext, string AddressFamily, List<Observation> Observations)
{
    public string Mode { get; init; } = "target-to-anchors";
    public bool Synthetic { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    // In observer-to-target sessions each LandmarkId identifies the measuring observer.
    public string? TargetAddress { get; init; }
}

public sealed record EstimatorOptions
{
    // Vacuum propagation is conservative even for radio links; fibre-only models can be tighter but conditional.
    public double PropagationKmPerMs { get; init; } = 299.792458;
    public double TimingSlackMs { get; init; } = 1;
    public double ResolutionKm { get; init; } = 100;
    public int MaxCells { get; init; } = 20_000;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(1);

    public void Validate()
    {
        if (!double.IsFinite(PropagationKmPerMs) || PropagationKmPerMs is < 1 or > 299.792458 ||
            !double.IsFinite(TimingSlackMs) || TimingSlackMs < 0 || !double.IsFinite(ResolutionKm) ||
            ResolutionKm is < 1 or > 5_000 || MaxCells is < 72 or > 200_000 || MaxAge <= TimeSpan.Zero)
            throw new InvalidDataException("Parámetros del estimador fuera de rango.");
    }
}

public sealed record DistanceConstraint(string LandmarkId, string SiteId, GeoPoint Center, double RadiusKm, double RttMs, string Evidence);

public sealed record GeoCell(double South, double West, double North, double East)
{
    [JsonIgnore]
    public GeoPoint Center => new((South + North) / 2, (West + East) / 2);
    // Travel along a meridian then a parallel: a conservative covering radius, including polar cells.
    [JsonIgnore]
    public double CoverRadiusKm => Math.Min(Math.PI * Geography.EarthRadiusKm,
        Geography.EarthRadiusKm * Math.PI / 180 * ((North - South) / 2 + (East - West) / 2));
    [JsonIgnore]
    public double AreaKm2 => Geography.EarthRadiusKm * Geography.EarthRadiusKm * Math.PI / 180 * (East - West) *
        (Math.Sin(North * Math.PI / 180) - Math.Sin(South * Math.PI / 180));
    public IEnumerable<GeoCell> Split()
    {
        double lat = Center.Latitude, lon = Center.Longitude;
        yield return new(South, West, lat, lon);
        yield return new(South, lon, lat, East);
        yield return new(lat, West, North, lon);
        yield return new(lat, lon, North, East);
    }
}

public sealed record LocationEstimate(string Status, GeoPoint? Representative, double? CoveringRadiusKm,
    double CandidateAreaKm2, List<GeoCell> Cells, List<DistanceConstraint> Constraints, List<string> Warnings)
{
    public bool ResolutionLimited { get; init; }
    public EstimatorOptions Model { get; init; } = new();
    public int IndependentSites => Constraints.Select(c => c.SiteId).Distinct(StringComparer.Ordinal).Count();
}

public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new QuicNet.Geolocation.GeoJsonContext()
    };
    public static JsonTypeInfo<T> GetTypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(path);
        if (input.Length > 128 * 1024 * 1024) throw new InvalidDataException("Input JSON exceeds 128 MiB.");
        byte[] prefix = new byte[3];
        int read = await input.ReadAtLeastAsync(prefix, 3, throwOnEndOfStream: false, ct);
        input.Position = read == 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf ? 3 : 0;
        return await JsonSerializer.DeserializeAsync(input, GetTypeInfo<T>(), ct) ?? throw new InvalidDataException("JSON vacío: " + path);
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(output, value, GetTypeInfo<T>(), ct);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
