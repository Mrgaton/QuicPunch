using System.Net;
using System.Net.Sockets;

namespace QuicNet.Geolocation;

public sealed class LatencyEstimator(EstimatorOptions? options = null, bool useEmpiricalCalibration = false)
{
    private readonly EstimatorOptions _options = options ?? new();

    public LocationEstimate Estimate(Catalog catalog, MeasurementSession session, DateTimeOffset? now = null)
    {
        catalog.Validate();
        _options.Validate();
        ValidateSession(session);
        var byId = catalog.Landmarks.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var byIp = catalog.Landmarks.Where(a => a.Usable).GroupBy(a => IPAddress.Parse(a.Address))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var constraints = new List<DistanceConstraint>();
        var warnings = new List<string>();
        var clock = now ?? DateTimeOffset.UtcNow;
        foreach (var observation in session.Observations)
        {
            if (!byId.TryGetValue(observation.LandmarkId, out var landmark))
                throw new InvalidDataException("Referencia desconocida: " + observation.LandmarkId);
            if (clock - observation.MeasuredUtc > _options.MaxAge || observation.MeasuredUtc - clock > TimeSpan.FromMinutes(1))
            { warnings.Add("Medición caducada/futura omitida: " + landmark.Id); continue; }
            if (!landmark.Usable) { warnings.Add("Referencia anycast o sin verificar omitida: " + landmark.Id); continue; }
            // Bind evidence to the literal address in this catalogue, never to a mutable DNS name.
            string expected = session.Mode == "observer-to-target" ? session.TargetAddress! : landmark.Address;
            if (!IPAddress.Parse(observation.Address).Equals(IPAddress.Parse(expected)))
                throw new InvalidDataException("La IP medida no coincide con la referencia/objetivo: " + landmark.Id);
            var best = observation.Samples.Where(s => s.Status == "Success" && s.RttMs is >= 0 && double.IsFinite(s.RttMs.Value))
                .MinBy(s => s.RttMs!.Value + TimingSlack(s, _options));
            if (best?.RttMs is { } rtt)
                constraints.Add(Bound(landmark, rtt, best.TimingMethod == "elapsed-upper-v1" ? "icmp-elapsed-upper" : "icmp-min-rtt", TimingSlack(best, _options), clock));
            // A trace made at the unknown target gives extra target→known-router distance constraints.
            // In observer→target mode the hops are distances FROM THE OBSERVER, so they cannot constrain the target.
            if (session.Mode != "target-to-anchors") continue;
            foreach (var hop in observation.Route)
            {
                if (hop.Status != "TtlExpired" || hop.RttMs is not { } hopRtt || hopRtt < 0 || !double.IsFinite(hopRtt) ||
                    !IPAddress.TryParse(hop.Address, out var ip) || !byIp.TryGetValue(ip, out var matches)) continue;
                var routers = matches.Where(a => a.Kind == "router").ToArray();
                // Conflicting coordinates for one router IP must never create a false constraint.
                if (routers.Length != 1) continue;
                constraints.Add(Bound(routers[0], hopRtt, "known-router-ttl-rtt"));
            }
        }
        constraints = constraints.GroupBy(c => c.LandmarkId).Select(g => g.MinBy(c => c.RadiusKm)!).ToList();
        if (session.Synthetic) warnings.Add("Datos sintéticos: no representan mediciones de Internet.");
        warnings.Add("Zona compatible con el modelo; no es un intervalo de confianza estadístico ni identifica dónde vive una persona.");
        if (_options.PropagationKmPerMs < 299.792458)
            warnings.Add("Modelo de propagación condicionado al medio configurado; puede excluir enlaces de radio más rápidos.");
        if (constraints.Count == 0) return new("insufficient-data", null, null, 0, [], constraints, warnings) { Model = _options };
        if (constraints.Select(c => c.SiteId).Distinct().Count() < 3)
            warnings.Add("Menos de tres emplazamientos independientes: localización muy ambigua.");

        // A cap covering the globe conveys no location information. Redundant caps must not
        // pull the representative towards distant, numerous infrastructure sites.
        var active = new List<DistanceConstraint>();
        foreach (var bound in constraints.OrderBy(c => c.RadiusKm).ThenBy(c => c.LandmarkId, StringComparer.Ordinal))
        {
            if (bound.RadiusKm >= Math.PI * Geography.EarthRadiusKm - .000001) continue;
            if (active.Take(64).Any(tighter => Geography.DistanceKm(tighter.Center, bound.Center) + tighter.RadiusKm <= bound.RadiusKm)) continue;
            active.Add(bound);
        }
        if (active.Count == 0)
            return new("unbounded", null, null, 4 * Math.PI * Geography.EarthRadiusKm * Geography.EarthRadiusKm,
                Geography.WorldCells().ToList(), constraints, [.. warnings, "Los RTT disponibles no delimitan una zona menor que el mundo."])
            { Model = _options };
        bool Possible(GeoCell cell) => active.All(c => Geography.DistanceKm(cell.Center, c.Center) <= c.RadiusKm + cell.CoverRadiusKm);
        var cells = Geography.WorldCells().Where(Possible).ToList();
        bool limited = false;
        // Retain all feasible branches, including distant/disconnected regions; never keep just the best local minimum.
        while (cells.Any(c => c.CoverRadiusKm > _options.ResolutionKm))
        {
            var next = new List<GeoCell>();
            foreach (var cell in cells)
            {
                if (cell.CoverRadiusKm <= _options.ResolutionKm) next.Add(cell);
                else next.AddRange(cell.Split().Where(Possible));
            }
            if (next.Count > _options.MaxCells) { limited = true; break; }
            cells = next;
        }
        if (cells.Count == 0) return new("inconsistent", null, null, 0, [], constraints,
            [.. warnings, "Restricciones incompatibles: revisar IP, ubicación de referencias, reloj, rutas y muestras."])
        { Model = _options };
        if (limited) warnings.Add("Se alcanzó el presupuesto de celdas; se conserva una zona más amplia.");
        var candidateCenters = cells.Where(c => active.All(b => Geography.DistanceKm(c.Center, b.Center) <= b.RadiusKm)).ToArray();
        // Area-weighted spherical centre of the feasible geometry, projected onto a feasible cell.
        // The number and locations of non-binding references cannot change this point.
        double x = 0, y = 0, z = 0, total = 0;
        foreach (var cell in cells)
        {
            double lat = cell.Center.Latitude * Math.PI / 180, lon = cell.Center.Longitude * Math.PI / 180, weight = cell.AreaKm2;
            x += weight * Math.Cos(lat) * Math.Cos(lon); y += weight * Math.Cos(lat) * Math.Sin(lon); z += weight * Math.Sin(lat); total += weight;
        }
        GeoPoint? representative = null;
        if (candidateCenters.Length > 0 && Math.Sqrt(x * x + y * y + z * z) > total * 1e-8)
        {
            var centroid = new GeoPoint(Math.Atan2(z, Math.Sqrt(x * x + y * y)) * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);
            representative = candidateCenters.MinBy(c => Geography.DistanceKm(c.Center, centroid))!.Center;
        }
        double? covering = representative == null ? null : cells.Max(c => Math.Min(Math.PI * Geography.EarthRadiusKm,
            Geography.DistanceKm(representative, c.Center) + c.CoverRadiusKm));
        return new(representative is null ? "unresolved" : "candidate-region", representative, covering,
            cells.Sum(c => c.AreaKm2), cells, constraints, warnings)
        { ResolutionLimited = limited, Model = _options };
    }

    public static double TimingSlack(ProbeSample sample, EstimatorOptions model) =>
        sample.TimingMethod == "elapsed-upper-v1" ? Math.Max(.05, model.TimingSlackMs - 1) : model.TimingSlackMs;

    private DistanceConstraint Bound(Landmark landmark, double rtt, string evidence, double? slack = null, DateTimeOffset? calibrationAt = null)
    {
        double radius = (rtt + (slack ?? _options.TimingSlackMs)) * _options.PropagationKmPerMs / 2 + landmark.UncertaintyKm;
        if (useEmpiricalCalibration && landmark.DelayCalibration is {} line && line.Applicable(landmark.Address, calibrationAt ?? DateTimeOffset.UtcNow) &&
            rtt + (slack ?? _options.TimingSlackMs) >= line.InterceptMs)
        {
            radius = Math.Min(radius, Math.Max(0, (rtt + (slack ?? _options.TimingSlackMs) - line.InterceptMs) / line.SlopeMsPerKm) + landmark.UncertaintyKm);
            evidence = "cbg-empirical-conditional:" + line.MeasurementId;
        }
        return new(landmark.Id, landmark.SiteId ?? landmark.Id, landmark.Position,
            Math.Min(Math.PI * Geography.EarthRadiusKm, radius), rtt, evidence);
    }

    internal static void ValidateSession(MeasurementSession session)
    {
        if (string.IsNullOrWhiteSpace(session.TargetId) || string.IsNullOrWhiteSpace(session.NetworkContext) ||
            session.AddressFamily is not ("ipv4" or "ipv6") || session.Mode is not ("target-to-anchors" or "observer-to-target") ||
            session.Observations == null || session.Observations.Count > 20_000) throw new InvalidDataException("Sesión inválida.");
        var family = session.AddressFamily == "ipv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        if (session.Mode == "observer-to-target" && (!IPAddress.TryParse(session.TargetAddress, out var target) || target.AddressFamily != family))
            throw new InvalidDataException("Falta una IP objetivo válida para observer-to-target.");
        foreach (var obs in session.Observations)
        {
            if (obs == null || string.IsNullOrWhiteSpace(obs.LandmarkId) || !IPAddress.TryParse(obs.Address, out var address) || address.AddressFamily != family ||
                // Up to three complete 64-TTL sweeps; this limit counts replies, not path depth.
                obs.Samples == null || obs.Route == null || obs.Samples.Count > 100 || obs.Route.Count > 192 ||
                obs.Samples.Any(s => s == null || s.RttMs is { } rtt && (!double.IsFinite(rtt) || rtt < 0)) || obs.Route.Any(h => h == null))
                throw new InvalidDataException("Muestras inválidas o mezcla de familias IP.");
        }
    }
}
