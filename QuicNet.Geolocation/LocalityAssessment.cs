namespace QuicNet.Geolocation;

public sealed record LocalityCandidate(Locality Place, double DistanceKm, string Basis);
public sealed record LocalityAssessment(string Status, string Summary, string Precision, string Evidence,
    GeoPoint? Position, double? ModelSpreadKm, LatencyPosition? Fit, IReadOnlyList<LocalityCandidate> Candidates,
    Locality? PublishedPlace, IReadOnlyList<string> Limitations)
{
    public static LocalityAssessment FromCalibration(RegionalRefinement refinement)
    {
        var region = refinement.CalibratedRegion!;
        if (region.Status != "candidate-region" || region.Representative is null || region.CoveringRadiusKm is null)
            throw new ArgumentException("CBG region has no representative");
        var nearby = LocalityCatalog.Default.Near(region.Representative, 6, Math.Min(500, region.CoveringRadiusKm.Value))
            .Select(p => new LocalityCandidate(p.Place, p.DistanceKm, "conditional-cbg-region-centroid")).ToArray();
        // CBG estimates the region first. Its centroid is only a display reference, not an RTT fit.
        var fit = new LatencyPosition(region.Representative, region.CoveringRadiusKm.Value, null, null,
            refinement.RespondingSites, false, [region.Representative]) { Method = "cbg-region-centroid" };
        return new("regional-estimate", "Región orientativa por latencia calibrada", "regional",
            "CONDITIONAL CBG · malla pública RIPE · coordenadas declaradas", region.Representative, region.CoveringRadiusKm, fit,
            nearby, null, [refinement.Explanation,
                "El centro representa la intersección de cotas CBG; no es una ciudad detectada. La extensión es geométrica, no un intervalo estadístico.",
                "El residuo y el retardo de acceso de un ajuste no se aplican al centro CBG."]);
    }
    public static LocalityAssessment Create(LocationUpdate update, Catalog? catalog = null, MeasurementSession? session = null)
    {
        var gazetteer = LocalityCatalog.Default;
        var prior = update.DatabasePrior;
        var published = prior == null ? null : gazetteer.MatchPublished(prior);
        if (update.Level == EvidenceLevel.Conflicting || update.Region?.Status == "inconsistent")
            return new("conflicting", "Las mediciones se contradicen", "unknown", "Sin una ubicación de red válida", null, null, null, [], published,
                ["Se conserva la referencia publicada por separado; no se utiliza para ocultar una contradicción."]);
        var fit = catalog != null && session != null && update.Region != null
            ? LatencyPositionEstimator.Estimate(catalog, session, update.Region, prior?.Position) : null;
        var limits = new List<string>
        {
            "Las localidades son candidatas; el ping no identifica el municipio ni el domicilio de una persona.",
            "La dispersión del ajuste mide sensibilidad a modelos de red; no es un radio de confianza ni un porcentaje de acierto."
        };
        if (fit != null)
        {
            var candidates = gazetteer.Near(fit.Position, 6, Math.Min(500, Math.Max(25, fit.ModelSpreadKm)))
                .Select(p => new LocalityCandidate(p.Place, p.DistanceKm, "latency-fit")).ToArray();
            bool disagrees = prior != null && (Geography.DistanceKm(prior.Position, fit.Position) > Math.Max(100, fit.ModelSpreadKm) ||
                update.Region!.Constraints.Any(c => Geography.DistanceKm(prior.Position, c.Center) > c.RadiusKm));
            string region = candidates.FirstOrDefault()?.Place.Region ?? "";
            string name = candidates.FirstOrDefault()?.Place.DisplayName ?? "";
            string summary = disagrees ? "La red y la ubicación publicada no coinciden" :
                fit.DiverseGeometry && fit.ModelSpreadKm <= 75 && name.Length > 0 ? "Entorno orientativo de " + name :
                region.Length > 0 ? "Zona orientativa: " + region : "Zona estimada a partir de varias referencias";
            if (disagrees) limits.Add("La posición publicada de la IP discrepa de los pings. No se elige una ciudad como confirmada.");
            if (!fit.DiverseGeometry) limits.Add("Las referencias rodean poco al objetivo; hacen falta mediciones desde otras direcciones.");
            return new(disagrees ? "disagreement" : fit.DiverseGeometry && fit.ModelSpreadKm <= 75 ? "locality-candidates" : "regional-estimate",
                summary, "estimate", $"{fit.IndependentSites} emplazamientos independientes · ajuste robusto de RTT", fit.Position,
                fit.ModelSpreadKm, fit, Array.AsReadOnly(candidates), published, limits.AsReadOnly());
        }
        if (prior != null)
        {
            string label = published?.DisplayName ?? prior.City;
            if (string.IsNullOrWhiteSpace(label)) label = string.Join(", ", new[] { prior.Region, prior.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var nearby = gazetteer.Near(prior.Position, 6, 30, prior.Country)
                .Select(p => new LocalityCandidate(p.Place, p.DistanceKm, "published-area")).ToArray();
            limits.Add("La referencia publicada puede situar la salida del operador o de una VPN. Las localidades próximas no han sido medidas individualmente.");
            return new("published-only", "Referencia publicada de la IP: " + label, "published", prior.Source,
                prior.Position, null, null, Array.AsReadOnly(nearby), published, limits.AsReadOnly());
        }
        if (update.Support?.Peaks is { Count: > 0 } peaks)
        {
            var nearby = peaks.Take(3).SelectMany(p => gazetteer.Near(p.Position, 3, 75)
                .Select(n => new LocalityCandidate(n.Place, n.DistanceKm, "network-infrastructure")))
                .DistinctBy(p => p.Place.Id).Take(6).ToArray();
            return new("network-area", "Zona de red con localidades orientativas", "regional", "Referencias y rutas de baja latencia",
                null, null, null, Array.AsReadOnly(nearby), null,
                [.. limits, "Las ciudades de routers describen infraestructura, no la ubicación medida del usuario."]);
        }
        return new("insufficient-data", "No hay señales suficientes para elegir una localidad", "unknown", "Sin referencias utilizables",
            null, null, null, [], null, ["El cortafuegos, ICMP bloqueado o un catálogo sin coincidencias pueden impedir la estimación. No se inventa un municipio."]);
    }
}
