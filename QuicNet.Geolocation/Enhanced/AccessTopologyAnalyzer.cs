using System.Net;

namespace QuicNet.Geolocation;

public static class AccessTopologyAnalyzer
{
    public static AccessTopology Analyze(MeasurementSession session, EmbeddedCatalog? catalog = null)
    {
        var data = catalog ?? EmbeddedCatalog.Default;
        var routes = session.Observations.Where(o => o.Route.Count > 0).Take(128).ToArray();
        var stable = routes.ToDictionary(o => o.LandmarkId, Stable);
        var publicPaths = routes.ToDictionary(o => o.LandmarkId, o => stable[o.LandmarkId]
            // Private addresses are opaque within-session topology identities, never geographic locations.
            // Exclude the local first-hop gateway and duplicate IPs at multiple TTLs.
            .Where(h => (h.Key > 1 || IsPublic(h.Value)) && h.Value != o.Address && IPAddress.TryParse(h.Value, out var ip) && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
            .GroupBy(h => h.Value, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).Select(g => g.Single()).OrderBy(h => h.Key).ToArray());
        var nodes = publicPaths.Values.SelectMany(p => p.Select(h => (Address: h.Value, Ttl: h.Key)))
            .GroupBy(h => h.Address, StringComparer.Ordinal)
            .Select(g => new TopologyNode(g.Key, publicPaths.Values.Count(p => p.Any(h => h.Value == g.Key)), g.Min(h => h.Ttl),
                routes.Length >= 3 && publicPaths.Values.Count(p => p.Any(h => h.Value == g.Key)) >= Math.Ceiling(routes.Length * .75)) { Public = IsPublic(g.Key) })
            .OrderBy(n => n.MinimumTtl).ThenBy(n => n.Address, StringComparer.Ordinal).ToArray();
        var nodeByAddress = nodes.ToDictionary(n => n.Address, StringComparer.Ordinal);
        var edgePaths = publicPaths.ToDictionary(p => p.Key, p => p.Value.Zip(p.Value.Skip(1))
            // A gap is unknown connectivity. Never invent an edge across silent or unstable TTLs.
            .Where(h => h.Second.Key == h.First.Key + 1 && h.First.Value != h.Second.Value)
            .Select(h => (From: h.First.Value, To: h.Second.Value)).Distinct().ToArray());
        var edges = edgePaths.Values.SelectMany(p => p).GroupBy(e => e)
            .Select(g => new TopologyEdge(g.Key.From, g.Key.To, g.Count())).OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ToArray();
        var shared = nodes.Where(n => n.Paths >= 2 && n.Public).Select(n => new SharedRouter(n.Address, n.MinimumTtl, n.Paths,
            routes.SelectMany(o => o.Route).Where(h => h.Address == n.Address && h.RttMs is >= 0 && double.IsFinite(h.RttMs.Value))
                .Select(h => h.RttMs).DefaultIfEmpty(null).Min(), data.LookupAsn(IPAddress.Parse(n.Address)), data.LookupRouter(IPAddress.Parse(n.Address))))
            .ToArray();
        var pairs = new List<RoutePair>();
        for (int a = 0; a < routes.Length; a++) for (int b = a + 1; b < routes.Length; b++)
        {
            var left = stable[routes[a].LandmarkId]; var right = stable[routes[b].LandmarkId];
            int common = 0; int? divergence = null; bool missing = false, prefixOpen = true;
            int max = Math.Min(routes[a].Route.Max(h => h.Ttl), routes[b].Route.Max(h => h.Ttl));
            for (int ttl = 1; ttl <= max; ttl++)
            {
                if (!left.TryGetValue(ttl, out var l) || !right.TryGetValue(ttl, out var r)) { missing = true; prefixOpen = false; continue; }
                if (l != r) { divergence ??= ttl; prefixOpen = false; }
                else if (prefixOpen) common++;
            }
            var sharedNodes = publicPaths[routes[a].LandmarkId].Select(h => h.Value)
                .Intersect(publicPaths[routes[b].LandmarkId].Select(h => h.Value), StringComparer.Ordinal).ToArray();
            pairs.Add(new(routes[a].LandmarkId, routes[b].LandmarkId, common, divergence, missing)
            { SharedPublicRouters = sharedNodes.Where(IsPublic).ToArray(), SharedPathNodes = sharedNodes,
                SharedEdges = edgePaths[routes[a].LandmarkId].Intersect(edgePaths[routes[b].LandmarkId]).Count() });
        }
        var traces = routes.Select(o =>
        {
            var path = publicPaths[o.LandmarkId];
            var groups = o.Route.GroupBy(h => h.Ttl).ToArray();
            int unstable = groups.Count(g => g.Where(IsReply).Select(h => h.Address).Distinct(StringComparer.Ordinal).Count() > 1);
            int missing = groups.Count(g => !g.Any(IsReply));
            var reached = o.Route.Where(h => h.Status == "Success" && h.Address == o.Address).Select(h => (int?)h.Ttl).Min();
            // A ubiquitous access router cannot make the whole world one independent branch.
            string branch = path.LastOrDefault(h => nodeByAddress[h.Value].Paths >= 2 && !nodeByAddress[h.Value].CommonAccess).Value
                ?? "unresolved:" + o.LandmarkId;
            double stability = path.Length == 0 ? 0 : (double)path.Length / Math.Max(1, groups.Count(g => g.Any(IsReply)));
            return new TraceSummary(o.LandmarkId, path.Count(h => IsPublic(h.Value)), unstable, missing, reached.HasValue, reached, branch, stability)
            { StablePathHops = path.Length };
        }).ToArray();
        return new(shared, pairs.ToArray(), routes.Length,
            "Grafo de routers repetidos y estables; reconoce coincidencias después de saltos sin respuesta. " +
            "Las IP privadas solo identifican tramos de esta sesión y nunca reciben coordenadas. Los tramos de acceso comunes no localizan el móvil. " +
            "Solo se unen TTL consecutivos; no se restan RTT de routers ni se convierten saltos en kilómetros.")
        { Nodes = nodes, Edges = edges, Traces = traces };
    }
    private static bool IsReply(RouteHop h) => h.Address is not null && h.Status is "TtlExpired" or "Success";
    private static bool IsPublic(string address) => IPAddress.TryParse(address, out var ip) && EmbeddedCatalog.IsPublic(ip);
    private static Dictionary<int, string> Stable(Observation observation) => observation.Route
        .GroupBy(h => h.Ttl).Where(g => g.Count() >= 2 && (!g.Any(h => h.Sweep > 0) || g.Select(h => h.Sweep).Distinct().Count() >= 2) && g.All(IsReply) &&
            g.Select(h => h.Address).Distinct(StringComparer.Ordinal).Count() == 1)
        .ToDictionary(g => g.Key, g => g.First().Address!);
}
