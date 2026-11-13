using System.Net;
using System.Text.Json;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class TopologyTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var areas = new[] { ("Cataluña", new GeoPoint(41.39, 2.17), 3d), ("Madrid", new GeoPoint(40.42, -3.70), 11d),
            ("Galicia", new GeoPoint(42.88, -8.54), 18d), ("Andalucía", new GeoPoint(37.39, -5.98), 20d) };
        var anchors = areas.Select((a,i) => new Landmark("t"+i, "8.8.8."+(i+1), a.Item2, "synthetic topology fixture")
            { Name = a.Item1, CountryCode = "ES", RegionName = a.Item1, SiteId = "site"+i, Asn = (uint)(100+i), CoordinateEvidence = "operator-declared" }).ToList();
        var catalog = new Catalog(anchors);
        List<RouteHop> Path(string destination) => Enumerable.Range(1,2).SelectMany(s => new RouteHop[]
        {
            new(1,"192.168.1.1",1,"TtlExpired") { Sweep=s }, new(2,null,null,"TimedOut") { Sweep=s },
            new(3,"80.1.1.1",2,"TtlExpired") { Sweep=s }, new(4,"80.1.1.2",3,"TtlExpired") { Sweep=s },
            new(5,destination,4,"Success") { Sweep=s }
        }).ToList();
        var session = new MeasurementSession("fixture","one-network","ipv4", anchors.Select((a,i) =>
            new Observation(a.Id,a.Address,now,[new(areas[i].Item3,"Success"),new(areas[i].Item3+.2,"Success")]) { Route=Path(a.Address) }).ToList()) { Synthetic = true };
        var topology = AccessTopologyAnalyzer.Analyze(session);
        check(topology.RoutePairs.All(p => p.SharedPrefixHops==1 && p.HasMissingHops && p.SharedPublicRouters.Length==2 && p.SharedEdges==1),
            "repeated public shared paths survive missing early TTLs without inventing a continuous prefix");
        check(topology.Nodes.Count==2 && topology.Nodes.All(n=>n.Paths==4 && n.CommonAccess) && topology.Edges.Count==1,
            "graph counts destinations once, excludes private/target addresses and marks ubiquitous access");
        check(topology.Traces.All(t=>t.Reached && t.DestinationTtl==5 && t.MissingHops==1 && t.Branch.StartsWith("unresolved:")),
            "a common operator access is not counted as independent regional branches");
        var estimate = TopologyLocationEstimator.Evaluate(catalog,session,topology);
        check(estimate.Status=="route-informed-affinity" && estimate.Contrasts.Count==6 && estimate.Zones.Count==4 &&
            !estimate.ExcludesGeographicZones && !estimate.Calibrated,
            "shared path differences affect explicit regional affinity without removing alternatives or claiming probabilities");
        check(estimate.Zones[0].Region=="Cataluña" && estimate.Zones.All(z=>z.Reasons.Length>=3),
            "known synthetic latency pattern ranks Catalonia and explains every area");
        var privatePaths = session with { Observations=session.Observations.Select(o=>o with
            { Route=o.Route.Select(h=>h.Ttl==3 ? h with { Address="10.50.1.1" } : h.Ttl==4 ? h with { Address="100.64.1.1" } : h).ToList() }).ToList() };
        var privateGraph = AccessTopologyAnalyzer.Analyze(privatePaths);
        var privateEstimate = TopologyLocationEstimator.Evaluate(catalog,privatePaths,privateGraph);
        check(privateGraph.SharedRouters.Count==0 && privateGraph.Nodes.All(n=>!n.Public) && privateEstimate.Contrasts.Count==6 &&
            privateEstimate.Zones.Select(z=>z.RelativeScore).SequenceEqual(estimate.Zones.Select(z=>z.RelativeScore)),
            "private and CGNAT nodes carry within-session topology without receiving coordinates or changing its meaning");
        var remote = Enumerable.Range(0,24).Select(i=>new Landmark("remote"+i,"9.9.9."+(i+1),new(35+i%10,-15+i),"synthetic remote fixture")
            { CountryCode="XX",RegionName="Remote"+i,Name="remote",SiteId="remote"+i,CoordinateEvidence="operator-declared" }).ToArray();
        var many = session with { Observations=[..session.Observations,..remote.Select(a=>new Observation(a.Id,a.Address,now,
            [new(8+.015*Geography.DistanceKm(areas[1].Item2,a.Position),"Success"),new(9+.015*Geography.DistanceKm(areas[1].Item2,a.Position),"Success")]))] };
        var strongLocal = TopologyLocationEstimator.Evaluate(new([..anchors,..remote]),many,topology);
        check(strongLocal.Zones[0].Region=="Cataluña", "many slow remote references cannot override the strongest local RTT through a global geometric fit");
        var noRoutes = session with { Observations = session.Observations.Select(o=>o with { Route=[] }).ToList() };
        var plain = TopologyLocationEstimator.Evaluate(catalog,noRoutes,AccessTopologyAnalyzer.Analyze(noRoutes));
        check(plain.Status=="latency-only-fallback" && plain.Contrasts.Count==0 && plain.Zones.Any(z=>Math.Abs(z.RelativeScore-estimate.Zones.Single(e=>e.Region==z.Region).RelativeScore)>1e-8),
            "fixed-measurement ablation proves routes affect scores and missing topology is an explicit fallback");
        var variable = session with { Observations = session.Observations.Select(o=>o with
            { Route=o.Route.Select(h=>h.Ttl==3 && h.Sweep==2 ? h with { Address="80.1.1.99" } : h).ToList() }).ToList() };
        var unstable = AccessTopologyAnalyzer.Analyze(variable);
        check(unstable.Nodes.All(n=>n.Address!="80.1.1.1" && n.Address!="80.1.1.99") && unstable.Traces.All(t=>t.UnstableHops==1) && unstable.Edges.Count==0,
            "load-balanced TTLs are excluded from shared edges rather than merged into a false path");
        var singles = session with { Observations=session.Observations.Select(o=>o with { Route=o.Route.Where(h=>h.Sweep==1).ToList() }).ToList() };
        check(AccessTopologyAnalyzer.Analyze(singles).SharedRouters.Count==0,
            "one response per TTL cannot masquerade as repeated stable topology");
        var shifted = session with { Observations=session.Observations.Select(o=>o with { Route=o.Route.Select(h=>h.Ttl==1 ? h : h with { Ttl=h.Ttl+10 }).ToList() }).ToList() };
        var shiftedEstimate = TopologyLocationEstimator.Evaluate(catalog,shifted,AccessTopologyAnalyzer.Analyze(shifted));
        check(estimate.Zones.All(z=>Math.Abs(z.RelativeScore-shiftedEstimate.Zones.Single(s=>s.Region==z.Region).RelativeScore)<1e-12),
            "adding ten visible hops cannot change geographic ranking: hop count is not distance");
        var duplicate = anchors[0] with { Id="duplicate",Address="8.8.4.4" };
        var repeatedSession = session with { Observations=[..session.Observations,new(duplicate.Id,duplicate.Address,now,[new(3,"Success"),new(3.2,"Success")])] };
        var dedup = TopologyLocationEstimator.Evaluate(new([..anchors,duplicate]),repeatedSession,topology);
        check(dedup.Zones.Select(z=>z.RelativeScore).SequenceEqual(estimate.Zones.Select(z=>z.RelativeScore)),
            "a colocated duplicate site adds no geographic vote");
        var locator = new ProgressiveLocator();
        var longTrace = session with { Observations=session.Observations.Select((o,i)=>i==0 ? o with
            { Route=Enumerable.Range(0,96).Select(n=>new RouteHop(n%32+1,null,null,"TimedOut") { Sweep=n/32+1 }).ToList() } : o).ToList() };
        check(locator.Replay(catalog,longTrace).IsFinal,"three full 32-hop sweeps remain valid when all intermediate replies are filtered");
        try
        {
            _=locator.Replay(catalog,session with { Observations=[session.Observations[0] with
                { Route=Enumerable.Range(0,193).Select(n=>new RouteHop(1,null,null,"TimedOut")).ToList() }] });
            check(false,"oversize route rejected");
        }
        catch (InvalidDataException) { check(true,"route reply count stays bounded at three 64-TTL sweeps"); }
        var withTopology = locator.Replay(catalog,session); var withoutTopology = locator.Replay(catalog,noRoutes);
        check(withTopology.Region!.Cells.SequenceEqual(withoutTopology.Region!.Cells) &&
            withTopology.RegionalRefinement!.Region.Cells.SequenceEqual(withoutTopology.RegionalRefinement!.Region.Cells),
            "topological affinity cannot cut the primary or declared-coordinate RTT-compatible region");
        var roundtrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(withTopology,GeoJsonContext.Default.LocationUpdate),GeoJsonContext.Default.LocationUpdate)!;
        check(roundtrip.Enhanced?.RouteLocation?.Zones.Count==4 && roundtrip.Enhanced.Topology.Edges.Count==1 &&
            WorkbenchExplanation.Create(roundtrip).Reasons.Any(r=>r.Contains("Cataluña")),
            "generated JSON and shared web explanation preserve regional reasoning and graph evidence");
        var selected = TopologyTracePlanner.Next(catalog,session,new([],[],0,"fixture"),new HashSet<string>(),3);
        check(selected.Length==3 && selected.Select(o=>anchors.Single(a=>a.Id==o.LandmarkId).RegionName).Distinct().Count()==3,
            "trace planner explores competing regions rather than only the lowest RTTs");
        using (var budget = new ProbeBudget(20,100))
        {
            var echo = new HiddenPrefix();
            var path = await new RepeatedIcmpTraceProvider(echo,2).TraceAsync("8.8.8.8",12,100,budget,CancellationToken.None);
            check(path.Count==18 && path.Count(h=>h.Status=="Success")==2 && path.Any(h=>h.Ttl==8 && h.Address=="80.1.1.1"),
                "traceroute reaches public hops after seven silent TTLs and repeats complete sweeps");
        }
        using (var budget = new ProbeBudget(20,3))
        {
            var path = await new RepeatedIcmpTraceProvider(new HiddenPrefix(),2).TraceAsync("8.8.8.8",12,100,budget,CancellationToken.None);
            check(path.Count==3 && budget.Sent==3, "exhausted trace budget retains completed probes without exceeding the launch cap");
        }
        var profiles = new[]{"Rápido","Equilibrado","Detallado"}.Select(WorkbenchSettings.Preset).ToArray();
        check(profiles[0].Seconds<profiles[1].Seconds && profiles[1].Seconds<profiles[2].Seconds && profiles[2].MaximumTraceRoutes==24 && profiles[2].MaxHops==32,
            "quality presets increase route depth, repetition and total independent destinations");
    }
    private sealed class HiddenPrefix : IEchoTransport
    {
        public Task<EchoResult> SendAsync(IPAddress address,int ttl,int timeoutMs,CancellationToken ct) =>
            Task.FromResult(ttl<8 ? new EchoResult("TimedOut",null,null) : ttl==8 ? new("TtlExpired","80.1.1.1",10) : new("Success",address.ToString(),11));
    }
}
