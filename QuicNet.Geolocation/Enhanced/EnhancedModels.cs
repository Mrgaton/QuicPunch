using System.Text.Json.Serialization;

namespace QuicNet.Geolocation;

/// <summary>Optional empirical data; never downloaded during a measurement.</summary>
public sealed record FingerprintDatabase
{
    public int SchemaVersion { get; init; } = 1;
    public string FeatureVersion { get; init; } = NetworkFingerprintEstimator.FeatureVersion;
    public string Source { get; init; } = "local-consented-measurements";
    public List<NetworkFingerprint> Profiles { get; init; } = [];
}
public sealed record FingerprintFeature(string SiteId, double MinimumMs, double MedianMs, double MadMs, int Samples);
public sealed record NetworkFingerprint(string Id, GeoPoint Position, string CountryCode, string Region, string Locality,
    uint? Asn, string AddressFamily, string Protocol, DateTimeOffset CapturedUtc, List<FingerprintFeature> Features)
{
    public string AccessTechnology { get; init; } = "unknown";
    public string TimingMethod { get; init; } = "elapsed-upper-v1";
    public string[] AccessRouters { get; init; } = [];
    public bool GroundTruthConsented { get; init; }
    public double GroundTruthUncertaintyKm { get; init; } = 10;
    public string? DatasetGroup { get; init; }
}
public sealed record FingerprintMatch(string ProfileId, GeoPoint Position, string CountryCode, string Region,
    string Locality, double RelativeScore, double Distance, int MatchedSites, bool SameAsn, double UncertaintyKm);
public sealed record FingerprintAssessment(string Status, IReadOnlyList<FingerprintMatch> Matches, string Explanation)
{
    // Relative matching weights are NOT calibrated probabilities.
    public bool Calibrated => false;
}
public sealed record GeographicHypothesis(string Id, string Level, string Label, string CountryCode,
    GeoPoint Position, double RelativeScore, double SearchRadiusKm, int IndependentSites, string Basis);
public sealed record HierarchyAssessment(string Stage, IReadOnlyList<GeographicHypothesis> Hypotheses,
    IReadOnlyList<Locality> NearbyPlaces, string Explanation)
{
    public bool MunicipalityConfirmed => false;
}
public sealed record LatencyStatistics(int Attempts, int Successes, double? MinimumMs, double? MedianMs,
    double? MadMs, double LossFraction);
public sealed record SharedRouter(string Address, int MinimumTtl, int Paths, double? MinimumRttMs,
    uint? Asn, PublishedLocation? Published);
public sealed record RoutePair(string FirstLandmarkId, string SecondLandmarkId, int SharedPrefixHops,
    int? FirstDivergenceTtl, bool HasMissingHops)
{
    public string[] SharedPublicRouters { get; init; } = [];
    public string[] SharedPathNodes { get; init; } = [];
    public int SharedEdges { get; init; }
}
public sealed record TopologyNode(string Address, int Paths, int MinimumTtl, bool CommonAccess)
{ public bool Public { get; init; } }
public sealed record TopologyEdge(string From, string To, int Paths);
public sealed record TraceSummary(string LandmarkId, int StablePublicHops, int UnstableHops, int MissingHops,
    bool Reached, int? DestinationTtl, string Branch, double Stability)
{ public int StablePathHops { get; init; } }
public sealed record AccessTopology(IReadOnlyList<SharedRouter> SharedRouters,
    IReadOnlyList<RoutePair> RoutePairs, int DistinctRoutes, string Explanation)
{
    public IReadOnlyList<TopologyNode> Nodes { get; init; } = [];
    public IReadOnlyList<TopologyEdge> Edges { get; init; } = [];
    public IReadOnlyList<TraceSummary> Traces { get; init; } = [];
}
public sealed record RouteContrast(string FirstLandmarkId, string SecondLandmarkId, double DeltaRttMs,
    double NoiseMs, int SharedRouters, int SharedEdges, double Weight);
public sealed record TopologyZone(string CountryCode, string Region, string Label, GeoPoint Position,
    double RelativeScore, double LatencyLoss, double? RouteLoss, int Sites, int TracedSites,
    int Branches, string[] Reasons);
public sealed record TopologyLocationAssessment(string Status, IReadOnlyList<TopologyZone> Zones,
    IReadOnlyList<RouteContrast> Contrasts, int UsableRoutes, int IdentifiedBranches, string[] Limitations)
{
    public string Method { get; init; } = "route-conditioned-affinity-v1";
    public bool Calibrated => false;
    public bool ExcludesGeographicZones => false;
}
public sealed record RouteAssessment(string Destination, string? SourceAddress, string? InterfaceId,
    string? InterfaceName, string? InterfaceDescription, int? InterfaceIndex, int? Mtu, string InterfaceType,
    bool? UsesLocalTunnel, string Method, string? Error);
public sealed record MtuAttempt(int PayloadBytes, string Status);
public sealed record PathMtuResult(string Destination, string AddressFamily, string Status, int? LowerBoundBytes,
    int? UpperBoundBytes, IReadOnlyList<MtuAttempt> Attempts, string Explanation);
[JsonConverter(typeof(JsonStringEnumConverter<TunnelState>))]
public enum TunnelState { Indeterminate, NoIndicators, PossibleEncapsulation, LocalTunnelIdentified }
public sealed record TunnelAssessment(TunnelState State, IReadOnlyList<RouteAssessment> Routes,
    IReadOnlyList<PathMtuResult> PathMtu, IReadOnlyList<string> Reasons)
{
    public bool AbsenceGuaranteed => false;
    public bool? MixedRoutes { get; init; }
}
public sealed record SupplementalObservation(string LandmarkId, string Protocol, LatencyStatistics Statistics,
    DateTimeOffset MeasuredUtc);
public sealed record LandmarkMeasurement(string Id, string Address, string SiteId, string Name, string CountryCode,
    GeoPoint Position, double? MinimumMs, double? MedianMs, int Successes, int Attempts, bool CoordinatesVerified, string Source);
public sealed record EnhancedEvidence(HierarchyAssessment Hierarchy, FingerprintAssessment Fingerprints,
    AccessTopology Topology, TunnelAssessment Tunnel, IReadOnlyList<SupplementalObservation> Supplemental)
{
    public string EngineVersion { get; init; } = "hierarchical-3-topology";
    public IReadOnlyList<LandmarkMeasurement> Measurements { get; init; } = [];
    public TopologyLocationAssessment? RouteLocation { get; init; }
}
public sealed record PrefixMetadata(string Prefix, uint? Asn, PublishedLocation? Location,
    string Source, bool Hosting = false);
public sealed record OfflineMetadata(PrefixMetadata[] Prefixes);
public sealed record EnhancedArchive(int SchemaVersion, AnalysisResult Result, FingerprintDatabase? Profiles);
