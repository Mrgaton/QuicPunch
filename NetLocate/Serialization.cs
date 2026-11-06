using System.Text.Json;
using System.Text.Json.Serialization;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation
{
    // The standalone target links the engine sources, keeping its .NET 10/AOT build
    // independent of the repository's .NET 11 web host and its Razor runtime.
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Landmark[]))]
    [JsonSerializable(typeof(Catalog))]
    [JsonSerializable(typeof(MeasurementSession))]
    [JsonSerializable(typeof(LocationEstimate))]
    [JsonSerializable(typeof(LocationUpdate))]
    [JsonSerializable(typeof(LocationUpdate[]))]
    [JsonSerializable(typeof(GeoCell))]
    [JsonSerializable(typeof(SpatialSupport))]
    [JsonSerializable(typeof(NetLocate.JournalRecord))]
    [JsonSerializable(typeof(NetLocate.RecordingHeader))]
    [JsonSerializable(typeof(ProgressiveSearchResult))]
    [JsonSerializable(typeof(NetLocate.MeasurementAuditReport))]
    public partial class GeoJsonContext : JsonSerializerContext { }
}

namespace NetLocate
{
    public sealed record RecordingHeader(int SchemaVersion, string EngineVersion, string CatalogSha256,
        Catalog Catalog, ProgressiveSearchOptions Options)
    {
        public string? PlannerVersion { get; init; }
        public string? RefinedFromSha256 { get; init; }
    }
    public sealed record JournalRecord(string Kind, RecordingHeader? Header = null, SearchRound? Round = null,
        string? State = null);
}
