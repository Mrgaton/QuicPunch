using System.Text.Json;
using System.Text.Json.Serialization;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Landmark[]))]
[JsonSerializable(typeof(DelayCalibrationCatalog))]
[JsonSerializable(typeof(GeoCell))]
[JsonSerializable(typeof(Catalog))]
[JsonSerializable(typeof(MeasurementSession))]
[JsonSerializable(typeof(LocationEstimate))]
[JsonSerializable(typeof(LocationUpdate))]
[JsonSerializable(typeof(LocationUpdate[]))]
[JsonSerializable(typeof(WorkbenchSettings))]
[JsonSerializable(typeof(WorkbenchAnalysis))]
[JsonSerializable(typeof(WorkbenchExplanation))]
[JsonSerializable(typeof(PacketRecord))]
[JsonSerializable(typeof(WorkbenchEvent))]
[JsonSerializable(typeof(WorkbenchStarted))]
[JsonSerializable(typeof(WorkbenchFinished))]
[JsonSerializable(typeof(WorkbenchError))]
[JsonSerializable(typeof(WorkbenchSnapshot))]
[JsonSerializable(typeof(WorkbenchPreview))]
[JsonSerializable(typeof(SpatialSupport))]
[JsonSerializable(typeof(Locality[]))]
[JsonSerializable(typeof(LocalityAssessment))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ProgressiveSearchResult))]
[JsonSerializable(typeof(AnalysisResult))]
[JsonSerializable(typeof(EnhancedArchive))]
[JsonSerializable(typeof(EnhancedEvidence))]
[JsonSerializable(typeof(FingerprintDatabase))]
[JsonSerializable(typeof(NetworkFingerprint))]
[JsonSerializable(typeof(OfflineMetadata))]
[JsonSerializable(typeof(TunnelAssessment))]
public partial class GeoJsonContext : JsonSerializerContext { }
