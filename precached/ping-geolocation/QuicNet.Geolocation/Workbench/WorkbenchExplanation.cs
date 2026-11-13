namespace QuicNet.Geolocation.Workbench;

/// <summary>Explains observations separately from the assumptions used to draw geographic hypotheses.</summary>
public sealed record WorkbenchExplanation(string Summary, string[] Reasons, LandmarkMeasurement[] Measurements,
    string? ModelDetail)
{
    public static WorkbenchExplanation Create(LocationUpdate? update)
    {
        var measured = update?.Enhanced?.Measurements.ToArray() ?? [];
        var responded = measured.Where(m => m.MinimumMs.HasValue).DistinctBy(m => m.SiteId).ToArray();
        bool primary = update?.Region is { Status: "candidate-region", Constraints.Count: > 0 };
        bool conditional = update?.RegionalRefinement is { Region.Status: "candidate-region" };
        var reasons = new List<string>();
        if (measured.Length > 0)
        {
            reasons.Add($"{responded.Length} emplazamientos respondieron de {measured.Length} destinos medidos. Se usa el RTT mínimo; una pérdida no se trata como latencia cero.");
            if (responded.MinBy(m => m.MinimumMs) is {} fastest)
                reasons.Add($"La referencia más rápida es {fastest.Name}: {fastest.MinimumMs:F2} ms. Indica cercanía de red a esa infraestructura, no la ciudad del dispositivo.");
        }
        else if (update?.Candidates.Count > 0)
            reasons.Add("Esta revisión conserva referencias rápidas, pero no el desglose de muestras. Un análisis nuevo permite inspeccionar mínimos, medianas y pérdidas por destino.");
        if (!primary && conditional)
            reasons.Add("No hay cotas físicas verificadas que delimiten tu posición. El color usa las coordenadas declaradas por los operadores de las sondas; por eso se muestra como afinidad condicional.");
        else if (primary)
            reasons.Add($"{update!.Region!.Constraints.Count} cotas de RTT delimitan una región físicamente compatible, no un punto exacto.");
        if (update?.RegionalRefinement?.CalibratedRegion is {} calibrated)
            reasons.Add($"Calibración CBG: {calibrated.Status}. Cada ancla usa su propia relación entre distancia y RTT, aprendida de mediciones públicas RIPE. Es una hipótesis condicionada a rutas y coordenadas declaradas, no una garantía física.");
        if (update?.Localities?.Fit is { Method: not "cbg-region-centroid" })
            reasons.Add("El ajuste intenta explicar las latencias con varios modelos de propagación y retardo de acceso. Los desvíos de las rutas pueden desplazar su óptimo incluso al mar. La dispersión entre modelos no mide el error respecto a tu ubicación real.");
        if (update?.Enhanced?.Fingerprints.Status == "no-trained-data")
            reasons.Add("No hay perfiles locales de entrenamiento para contrastar la forma de las latencias. La comparación por comunidades del mapa es visual, no una identificación del domicilio.");
        if (update?.Enhanced?.RouteLocation is {} topology)
        {
            reasons.Add($"Topología: {topology.UsableRoutes} rutas con al menos dos routers estables; {topology.Contrasts.Count} contrastes de RTT entre destinos con tramos compartidos. Los huecos no se interpretan como saltos cercanos ni como distancia cero.");
            if (topology.Zones.FirstOrDefault() is {} leading)
            {
                reasons.Add($"Mayor afinidad de red del modelo: {leading.Label} ({leading.CountryCode}). Puede describir la salida del operador, especialmente en datos móviles.");
                reasons.AddRange(leading.Reasons);
            }
            reasons.AddRange(topology.Limitations);
        }
        string summary = update is null ? "Inicia el análisis para ver qué señales sostienen el resultado." :
            primary ? "Hay una región compatible con las mediciones; la ciudad sigue sin confirmar." :
            conditional ? "La ubicación sigue sin resolver. Hay afinidad de red que puedes explorar, pero no una posición verificada." :
            update.DatabasePrior is not null ? "Solo hay una referencia publicada de la IP; no una ubicación medida." :
            "Las señales actuales no permiten localizar esta conexión.";
        string? detail = update?.Localities?.Fit is { Method: "cbg-region-centroid" } cbg
            ? $"Centro geométrico de la región CBG, no posición del dispositivo: {LocationDescription.FormatCoordinates(cbg.Position)}. Extensión de la región desde ese centro: {cbg.ModelSpreadKm:N0} km."
            : update?.Localities?.Fit is {} fit
            ? $"Punto matemático del ajuste, no posición del dispositivo: {LocationDescription.FormatCoordinates(fit.Position)}. " +
              $"Dispersión entre modelos: {fit.ModelSpreadKm:N0} km · residuo de RTT: {fit.ResidualMs:F2} ms · retardo de acceso ajustado: {fit.AccessDelayMs:F2} ms."
            : null;
        return new(summary, reasons.ToArray(), measured, detail);
    }
}
