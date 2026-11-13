namespace QuicNet.Geolocation.Workbench;

/// <summary>Shared presentation for the standalone application and the embedded web laboratory.</summary>
public sealed record WorkbenchAnalysis(string Status, string Stage, string Tunnel, string Fingerprints,
    int Probes, int References, bool Conditional, EnhancedEvidence? Evidence)
{
    public static WorkbenchAnalysis Create(LocationUpdate? update, WorkbenchSettings settings, bool running)
    {
        var evidence = update?.Enhanced;
        string status = update?.Phase switch
        {
            "cancelled-partial" => "Detenido · resultado parcial",
            "time-budget-exhausted" => "Tiempo agotado · resultado parcial",
            "probe-budget-exhausted" => "Límite de sondeos alcanzado",
            "network-context-changed" => "La conexión cambió · inicia otro análisis",
            "no-landmarks" => "Sin referencias para esta familia de IP",
            _ when running => "Midiendo…",
            _ when update is null => "Listo para empezar",
            "offline" => "Consulta local · sin sondeos",
            _ => "Análisis terminado"
        };
        string stage = evidence?.Hierarchy.Stage switch
        {
            "world-exploration" => "Exploración global",
            "country-candidates" => "Alternativas de país",
            "regional-candidates" => "Alternativas regionales",
            "local-area-candidates" => "Alternativas de área local",
            _ => "Esperando mediciones"
        };
        string tunnel = !settings.EnableTunnelAssessment ? "Diagnóstico desactivado" : evidence?.Tunnel.State switch
        {
            TunnelState.LocalTunnelIdentified => "Túnel local identificado",
            TunnelState.PossibleEncapsulation => "Posible encapsulación",
            TunnelState.NoIndicators => "Sin indicios de túnel local",
            _ => "Sin determinar"
        };
        string fingerprints = evidence?.Fingerprints.Status switch
        {
            "no-trained-data" => "Sin perfiles de entrenamiento locales",
            "mixed-network-context" => "Comparación suspendida: rutas mezcladas o cambiantes",
            _ when evidence?.Fingerprints.Matches.Count > 0 => "Comparación con perfiles locales disponible",
            _ => "Sin comparación de perfiles disponible"
        };
        return new(status, stage, tunnel, fingerprints, update?.SentPackets ?? 0, update?.MeasuredLandmarks ?? 0,
            update?.RegionalRefinement?.Region.Status == "candidate-region", evidence);
    }

    public static string Level(string level) => level switch
    { "country" => "País", "regional-area" => "Región", "local-area" => "Área local", _ => "Zona" };

    public static string HypothesisLabel(GeographicHypothesis value) => value.Level != "country" ? value.Label : value.CountryCode switch
    {
        "ES" => "España", "DE" => "Alemania", "CZ" => "Chequia", "EE" => "Estonia", "FI" => "Finlandia",
        "AE" => "Emiratos Árabes Unidos", "AU" => "Australia", "BR" => "Brasil", "CA" => "Canadá", "ZA" => "Sudáfrica",
        "FR" => "Francia", "GB" => "Reino Unido", "PT" => "Portugal", "IT" => "Italia", "NL" => "Países Bajos",
        _ => value.Label
    };

    public static string PathMtuStatus(string status) => status switch
    {
        "bounded" => "Intervalo medido",
        "at-least-probed-size" => "Al menos el tamaño probado",
        "control-unreachable" => "Sin respuesta de control",
        "indeterminate-filter-or-loss" => "Indeterminado: filtrado o pérdida",
        "unsupported-portable-ipv6-df" => "Medición IPv6 no disponible",
        "interrupted" => "Medición interrumpida",
        "probe-budget" => "Límite de sondeos alcanzado",
        _ => "Sin determinar"
    };
}

public sealed record WorkbenchCoverage(int Destinations, int Sites, int Places)
{
    public static WorkbenchCoverage Current { get; } = new(EmbeddedCatalog.Default.Anchors.Count,
        EmbeddedCatalog.Default.Anchors.DistinctBy(a => a.SiteId ?? a.Id).Count(), LocalityCatalog.Default.Count);
}
