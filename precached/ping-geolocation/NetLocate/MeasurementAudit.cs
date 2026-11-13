using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;

namespace NetLocate;

public sealed record AuditRound(int Number, int Packets, int RespondingSites, double AreaKm2,
    double? RadiusKm, double? BestRttMs, int CapsNotContainedInAnother);
public sealed record MeasurementAuditReport(string Recording, string Planner, int ResponsiveSites,
    int SitesWithin250KmOfFastest, int KnownAsns, int CapsNotContainedInAnother, int ContainedOrGlobalCaps,
    string? TightestLandmark, double? BestRttMs, double? TightestRadiusKm, int? BestRttFirstRound,
    double AreaKm2, double? RadiusKm, double? FinerMeshAreaKm2, double? FinerMeshDifferencePercent,
    AuditRound[] Rounds, string[] Findings)
{
    public int PrimaryResponses { get; init; }
    public int DeclaredResponses { get; init; }
    public double? TightestPrimaryRttMs { get; init; }
    public RegionalAudit? Regional { get; init; }
}
public sealed record RegionalAudit(string Status, double AreaKm2, double? RadiusKm,
    int Sites, int Asns, double MinimumRttMs, int ElapsedUpperSamples);

internal static class MeasurementAudit
{
    public static MeasurementAuditReport Analyze(string path)
    {
        var (header, result) = RecordingReader.Read(path);
        var final = result.Rounds.LastOrDefault() ?? throw new InvalidDataException("No hay rondas para analizar.");
        var bounds = final.Update.Region?.Constraints ?? [];
        var effective = NonContained(bounds);
        var byId = header.Catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var successes = final.Session.Observations.Where(o => o.MinimumRttMs.HasValue).ToArray();
        var fastest = successes.OrderBy(o => o.MinimumRttMs).ThenBy(o => o.LandmarkId, StringComparer.Ordinal).FirstOrDefault();
        var tightest = bounds.OrderBy(b => b.RadiusKm).ThenBy(b => b.LandmarkId, StringComparer.Ordinal).FirstOrDefault();
        int near = fastest is null ? 0 : successes.Count(o => Geography.DistanceKm(byId[fastest.LandmarkId].Position, byId[o.LandmarkId].Position) < 250);
        int asns = successes.Select(o => byId[o.LandmarkId].Asn ??
            EmbeddedCatalog.Default.LookupAsn(IPAddress.Parse(o.Address))).Where(a => a.HasValue).Distinct().Count();
        double? finerArea = null, difference = null;
        var region = final.Update.Region;
        if (region is { Cells.Count: > 0 } && region.Status != "unbounded")
        {
            var fine = new LatencyEstimator(header.Options.Model with
            {
                ResolutionKm = Math.Max(1, header.Options.Model.ResolutionKm / 4),
                MaxCells = 200_000, MaxAge = TimeSpan.FromDays(36500)
            }).Estimate(header.Catalog, final.Session, final.Session.Observations.Max(o => o.MeasuredUtc));
            if (fine.Status is "candidate-region" or "unresolved")
            {
                finerArea = fine.CandidateAreaKm2;
                difference = region.AreaKm2 <= 0 ? null : (region.AreaKm2 - finerArea) / region.AreaKm2 * 100;
            }
        }
        int? firstBest = fastest is null ? null : result.Rounds.First(r => r.Session.Observations.Any(o =>
            o.LandmarkId == fastest.LandmarkId && o.MinimumRttMs <= fastest.MinimumRttMs)).Number;
        var findings = new List<string>
        {
            "Las cotas conservan el modelo congelado y el método de cada muestra; no restan acceso ni estrechan la velocidad. Los RTT elapsed-upper-v1 incluyen sobrecoste local y margen nominal de reloj de al menos 0,05 ms.",
            "El conteo de cotas contenidas describe el estado final; una referencia redundante al final pudo ser útil antes.",
            "La concentración por ASN es una pista de planificación. ASN distintos no prueban independencia de rutas.",
            "Una reducción de área compatible no demuestra error geográfico menor sin una ubicación física real de validación.",
            "La auditoría puede consultar únicamente el índice ASN local incorporado para describir registros antiguos; no cambia su catálogo congelado ni sus cotas."
        };
        if (tightest is not null)
            findings.Add(FormattableString.Invariant($"La cota más corta aún permite {tightest.RadiusKm:F0} km desde su referencia; incluye la holgura temporal y su incertidumbre declarada."));
        if (successes.Length > 0 && near > successes.Length * .4)
            findings.Add("Muchas referencias se concentran cerca de la más rápida: buscar más IP en ese grupo puede repetir casi la misma cota.");
        if (difference is { } d && Math.Abs(d) < 10)
            findings.Add("Reducir a una cuarta parte el objetivo de resolución y ampliar el presupuesto de celdas cambia poco el área: la incertidumbre dominante procede de latencias/geometría, no del tamaño del píxel.");
        return new(Path.GetFullPath(path), header.Options.Planner ?? "legacy", successes.Length, near, asns,
            effective.Count, bounds.Count - effective.Count, tightest?.LandmarkId, fastest?.MinimumRttMs,
            tightest?.RadiusKm, firstBest, region?.AreaKm2 ?? 0, region?.CoveringRadiusKm, finerArea, difference,
            result.Rounds.Select(r => new AuditRound(r.Number, r.SentPackets,
                r.Session.Observations.Count(o => o.MinimumRttMs.HasValue), r.Update.Region?.AreaKm2 ?? 0,
                r.Update.Region?.CoveringRadiusKm, r.Session.Observations.Where(o => o.MinimumRttMs.HasValue)
                    .Select(o => o.MinimumRttMs).Min(), NonContained(r.Update.Region?.Constraints ?? []).Count)).ToArray(),
            findings.ToArray())
        {
            PrimaryResponses = successes.Count(o => byId[o.LandmarkId].Usable),
            DeclaredResponses = successes.Count(o => byId[o.LandmarkId].CoordinateEvidence == "operator-declared"),
            TightestPrimaryRttMs = tightest?.RttMs,
            Regional = final.Update.RegionalRefinement is { } refinement ? new(refinement.Region.Status,
                refinement.Region.AreaKm2, refinement.Region.CoveringRadiusKm, refinement.RespondingSites,
                refinement.NetworkGroups, refinement.MinimumRttMs, final.Session.Observations
                    .SelectMany(o => o.Samples).Count(s => s.TimingMethod == "elapsed-upper-v1")) : null
        };
    }

    public static List<DistanceConstraint> NonContained(IReadOnlyList<DistanceConstraint> bounds)
    {
        var active = new List<DistanceConstraint>();
        foreach (var bound in bounds.OrderBy(b => b.RadiusKm).ThenBy(b => b.LandmarkId, StringComparer.Ordinal))
        {
            if (bound.RadiusKm >= Math.PI * Geography.EarthRadiusKm - .000001) continue;
            if (active.Take(64).Any(a => Geography.DistanceKm(a.Center, bound.Center) + a.RadiusKm <= bound.RadiusKm)) continue;
            active.Add(bound);
        }
        return active;
    }

    public static int Run(string[] args)
    {
        if (args.Length is not (2 or 4) || args.Length == 4 && args[2] != "--out")
            throw new ArgumentException("Uso: NetLocate analyze measurements.jsonl [--out audit.json]");
        var report = Analyze(args[1]);
        Console.WriteLine(FormattableString.Invariant($"Planificador: {report.Planner}; referencias con respuesta: {report.ResponsiveSites}; a <250 km de la más rápida: {report.SitesWithin250KmOfFastest}; ASN: {report.KnownAsns}."));
        Console.WriteLine(FormattableString.Invariant($"Cotas no contenidas en otra: {report.CapsNotContainedInAnother}; contenidas/globales: {report.ContainedOrGlobalCaps}."));
        Console.WriteLine(FormattableString.Invariant($"RTT mínimo de todos los destinos: {report.BestRttMs:F4} ms, ronda {report.BestRttFirstRound}. Cota principal más corta: {report.TightestRadiusKm:F0} km (RTT {report.TightestPrimaryRttMs:F4} ms)."));
        Console.WriteLine(FormattableString.Invariant($"Área: {report.AreaKm2:F0} km²; radio: {report.RadiusKm:F0} km; cambio con malla más fina: {report.FinerMeshDifferencePercent:F2} %."));
        if (report.Regional is { } regional)
            Console.WriteLine(FormattableString.Invariant($"Hipótesis declarada separada: {regional.Status}; {regional.AreaKm2:F0} km²; radio {regional.RadiusKm:F0} km; RTT {regional.MinimumRttMs:F4} ms; {regional.Sites} sitios / {regional.Asns} ASN. No confirma ciudad."));
        if (args.Length == 4)
            AtomicFile.Write(args[3], JsonSerializer.Serialize(report, GeoJsonContext.Default.MeasurementAuditReport));
        foreach (string finding in report.Findings) Console.WriteLine(finding);
        return 0;
    }
}
