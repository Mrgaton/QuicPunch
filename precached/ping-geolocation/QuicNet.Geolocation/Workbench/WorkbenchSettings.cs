using System.Net;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation.Workbench;

public sealed record WorkbenchSettings
{
    public string Ip { get; set; } = "";
    public bool Offline { get; set; }
    public string Family { get; set; } = "ipv4";
    public int Rounds { get; set; } = 5;
    public int BatchSize { get; set; } = 32;
    public int Samples { get; set; } = 4;
    public int TimeoutMs { get; set; } = 700;
    public int MaxHops { get; set; } = 28;
    public int TraceCount { get; set; } = 4;
    public int PacketsPerSecond { get; set; } = 20;
    public int Seconds { get; set; } = 300;
    public double ResolutionKm { get; set; } = 50;
    public int Parallelism { get; set; } = 6;
    public double TimingSlackMs { get; set; } = 1;
    public int MaxCells { get; set; } = 20_000;
    public double PropagationKmPerMs { get; set; } = 299.792458;
    public bool EnableTunnelAssessment { get; set; } = true;
    public bool EnableTopology { get; set; } = true;
    public bool EnablePathMtu { get; set; }
    public bool EnableTcp { get; set; }
    public int MaximumProbes { get; set; } = 4000;
    public int TraceRepetitions { get; set; } = 3;
    public int MaximumTraceRoutes { get; set; } = 16;
    public static WorkbenchSettings Preset(string name) => name switch
    {
        "Rápido" => new() { Rounds = 3, BatchSize = 24, Samples = 3, Seconds = 120, TraceCount = 3, MaxHops = 24, MaximumTraceRoutes = 8, MaximumProbes = 2000, TraceRepetitions = 2 },
        "Detallado" => new() { Rounds = 8, BatchSize = 48, Samples = 6, Seconds = 600, TraceCount = 6, MaxHops = 32, MaximumTraceRoutes = 24, MaximumProbes = 8000, TraceRepetitions = 3 },
        "Equilibrado" => new() { MaximumProbes = 4000, TraceRepetitions = 3 },
        "Sin red" => new() { Offline = true },
        _ => new()
    };
    public EstimatorOptions ToModel() => new()
    {
        ResolutionKm = ResolutionKm,
        TimingSlackMs = TimingSlackMs,
        MaxCells = MaxCells,
        PropagationKmPerMs = PropagationKmPerMs
    };
    public LocateRequest ToRequest()
    {
        Validate();
        return new()
        {
            PublicAddress = string.IsNullOrWhiteSpace(Ip) ? null : IPAddress.Parse(Ip.Trim()),
            EnableNetwork = !Offline,
            UseHierarchicalEngine = true,
            EnableTunnelAssessment = EnableTunnelAssessment,
            EnableTopology = EnableTopology,
            EnablePathMtu = EnablePathMtu,
            EnableTcp = EnableTcp,
            MaximumProbes = MaximumProbes,
            TraceRepetitions = TraceRepetitions,
            MaximumTraceRoutes = MaximumTraceRoutes,
            IPv6 = Family == "ipv6",
            Rounds = Rounds,
            BatchSize = BatchSize,
            Samples = Samples,
            TimeoutMs = TimeoutMs,
            MaxHops = MaxHops,
            TraceCount = TraceCount,
            PacketsPerSecond = PacketsPerSecond,
            Budget = TimeSpan.FromSeconds(Seconds),
            ResolutionKm = ResolutionKm,
            Parallelism = Parallelism,
            TimingSlackMs = TimingSlackMs,
            MaxCells = MaxCells,
            PropagationKmPerMs = PropagationKmPerMs
        };
    }
    public void Validate()
    {
        if (Rounds is < 1 or > 10 || BatchSize is < 6 or > 64 || Samples is < 1 or > 20 || TimeoutMs is < 100 or > 10000 ||
            MaxHops is < 1 or > 64 || TraceCount is < 1 or > 8 || PacketsPerSecond is < 1 or > 20 || Seconds is < 1 or > 1800 ||
            Parallelism is < 1 or > 32 || MaximumProbes is < 1 or > 100000 || TraceRepetitions is < 1 or > 3 || MaximumTraceRoutes is < 1 or > 64 ||
            !double.IsFinite(TimingSlackMs) || TimingSlackMs is < 0 or > 100 || Family is not ("ipv4" or "ipv6"))
            throw new ArgumentException("Parámetros fuera de los límites permitidos.");
        ToModel().Validate();
        if (!string.IsNullOrWhiteSpace(Ip) && (!IPAddress.TryParse(Ip.Trim(), out var ip) || !EmbeddedCatalog.IsPublic(ip) ||
            (EmbeddedCatalog.Normalize(ip).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) != (Family == "ipv6")))
            throw new ArgumentException("La IP debe ser pública y coincidir con la familia seleccionada. Puedes dejarla vacía.");
    }
}

public sealed record LocationDescription(string Title, string Coordinates, string Explanation, string Evidence)
{
    private static System.Globalization.CultureInfo DisplayCulture { get { try { return System.Globalization.CultureInfo.GetCultureInfo("es-ES"); } catch (System.Globalization.CultureNotFoundException) { return System.Globalization.CultureInfo.InvariantCulture; } } }
    public static LocationDescription Describe(LocationUpdate? update, bool active = false)
    {
        var region = update?.Region;
        if (update?.Level == EvidenceLevel.Conflicting || region?.Status == "inconsistent")
            return new("No hay una estimación válida", "Se ha retirado el punto provisional.", "Las mediciones se contradicen; revisa las referencias y las rutas.", "");
        string title = "Todavía no hay una localización estimada", coordinates = "Esperando señales de esta conexión.",
            explanation = "Inicia un análisis o abre una sesión para ver la aproximación y su margen.", evidence = "";
        if (region?.Representative is not null)
        {
            title = "Zona compatible · ciudad sin confirmar";
            coordinates = region.CoveringRadiusKm is { } radius ? string.Format(DisplayCulture, "Radio de la zona: {0:N0} km · superficie {1:N0} km²", radius, region.AreaKm2) : "Extensión sin determinar";
            explanation = "El punto es el centro geométrico del área, no la ubicación del dispositivo. Su cercanía a una ciudad no mide el error.";
        }
        else if (region?.Status == "unbounded")
        { title = "Todavía no hay una zona delimitada"; coordinates = "Los RTT actuales permiten una zona demasiado amplia."; explanation = "Se necesitan referencias más cercanas; no se dibuja una ubicación inventada."; }
        else if (region?.Cells.Count > 0)
        { title = "Hay una zona candidata, todavía sin punto provisional"; coordinates = "La región ámbar es el resultado disponible."; explanation = "No se fuerza una coordenada cuando no existe un punto representativo compatible."; }
        else if (update?.DatabasePrior is { } prior)
        {
            title = "Ubicación publicada de tu IP: " + string.Join(", ", new[] { prior.City, prior.Region, prior.Country }.Where(s => !string.IsNullOrEmpty(s)));
            coordinates = FormatCoordinates(prior.Position); explanation = $"Declaración de {prior.Source} sin verificar con mediciones. El rombo amarillo no confirma dónde está el dispositivo.";
        }
        else if (active && (update is null || !update.IsFinal))
        { title = "Midiendo: aún no hay una zona delimitada"; coordinates = "Los paquetes aparecen mientras se completa la primera tanda."; explanation = "La aproximación se mostrará cuando haya suficientes mediciones."; }
        else if (update is not null) explanation = "No se obtuvieron señales suficientes. Consulta los estados de paquetes y rutas.";
        if (update?.Routers.Count > 0)
        {
            var fastest = update.Routers.MinBy(r => r.RttMs)!;
            evidence = string.Format(DisplayCulture, "Router de menor latencia: {0} ({1}) · {2:F2} ms. Es una pista de infraestructura, no tu ciudad confirmada.", fastest.Published.City, fastest.Published.Country, fastest.RttMs);
        }
        else if (region?.Constraints.Count > 0) evidence = $"{region.Constraints.Count} cotas de distancia por latencia; sin referencias con nombre de ciudad corroboradas.";
        if (update?.Localities is { } places && places.Status is not ("insufficient-data" or "conflicting"))
        {
            title = places.Summary;
            if (places.Fit is { } fit)
            {
                coordinates = fit.Method == "cbg-region-centroid"
                    ? string.Format(DisplayCulture, "Centro de la región CBG: {0} · extensión desde el centro: {1:N0} km", FormatCoordinates(fit.Position), fit.ModelSpreadKm)
                    : string.Format(DisplayCulture, "Ajuste orientativo: {0} · dispersión entre modelos: {1:N0} km", FormatCoordinates(fit.Position), fit.ModelSpreadKm);
                explanation = fit.Method == "cbg-region-centroid"
                    ? "La región combina cotas calibradas por ancla con mediciones públicas RIPE. Su centro es una referencia geométrica; las localidades cercanas no son municipios confirmados."
                    : "El ajuste usa varias latencias y estima por separado el retardo de acceso. La zona física compatible se conserva; las localidades cercanas son alternativas, no un municipio confirmado.";
            }
            else if (places.Status == "published-only") explanation = "Se muestra la mejor referencia local disponible aunque los pings no basten. Sitúa la IP o su operador; no confirma dónde está la persona.";
        }
        if (update?.Enhanced?.Hierarchy is { Hypotheses.Count: > 0 } hierarchy &&
            region?.Representative is null && update.Localities?.Fit is null)
        {
            title = "Alternativas de red · ciudad sin confirmar";
            coordinates = $"{hierarchy.Hypotheses.Count} hipótesis geográficas abiertas.";
            explanation = "Compara las alternativas en el mapa. La afinidad de red puede describir la salida de un túnel o la infraestructura del operador.";
        }
        if (update?.RegionalRefinement is { Region.Status: "candidate-region" } conditional &&
            region?.Constraints.Count is not > 0)
        {
            title = "Ubicación sin resolver · afinidad de red disponible";
            coordinates = $"{conditional.RespondingSites} emplazamientos · coordenadas de sondas declaradas, sin verificación física.";
            explanation = "Explora el mapa de calor y las referencias medidas. El punto del ajuste no se presenta como la posición del dispositivo.";
        }
        return new(title, coordinates, explanation, evidence);
    }
    public static string FormatCoordinates(GeoPoint p) => FormattableString.Invariant($"{Math.Abs(p.Latitude):F4}° {(p.Latitude >= 0 ? "N" : "S")}, {Math.Abs(p.Longitude):F4}° {(p.Longitude >= 0 ? "E" : "O")}");
}
