using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class SelfTests
{
    public static async Task<int> RunAsync()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS " + name); passed++;
        }
        void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidDataException) { Check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        Landmark Anchor(string id, double lat, double lon, string ip = "192.0.2.1") =>
            new(id, ip, new(lat, lon), "test-fixture") { LocationVerified = true, UncertaintyKm = 0 };
        Observation Sample(Landmark anchor, double? rtt, string status = "Success") =>
            new(anchor.Id, anchor.Address, DateTimeOffset.UtcNow, [new(rtt, status)]);
        MeasurementSession Session(params Observation[] observations) => new("target", "test-network", "ipv4", observations.ToList());
        bool Contains(LocationEstimate estimate, GeoPoint point) => estimate.Cells.Any(c => point.Latitude >= c.South && point.Latitude <= c.North &&
            point.Longitude >= c.West && point.Longitude <= c.East);

        try
        {
            JsonTests.Run(Check);
            Check(Math.Abs(Geography.DistanceKm(new(0, 0), new(0, 1)) - 111.195) < .01, "great-circle distance");
            Check(Geography.DistanceKm(new(0, 179.9), new(0, -179.9)) < 23, "antimeridian distance");
            Check(Geography.DistanceKm(new(89.99, 0), new(89.99, 180)) < 3, "polar distance");
            var estimator = new LatencyEstimator(new() { ResolutionKm = 25, TimingSlackMs = 0 });
            var a = Anchor("a", 0, 0);
            var b = Anchor("b", 0, 90, "192.0.2.2");
            var catalog = new Catalog([a, b]);
            Check(estimator.Estimate(catalog, Session()).Status == "insufficient-data", "no location invented without measurements");
            var failed = estimator.Estimate(catalog, Session(Sample(a, null, "TimedOut")));
            Check(failed.Constraints.Count == 0, "packet loss never treated as zero RTT");
            var jitter = Sample(a, 20) with { Samples = [new(20, "Success"), new(2, "Success"), new(400, "Success"), new(null, "TimedOut")] };
            var single = estimator.Estimate(catalog, Session(jitter));
            Check(single.Constraints.Single().RttMs == 2 && single.CoveringRadiusKm > 100, "minimum RTT and honest single-anchor ambiguity");
            var incompatible = estimator.Estimate(catalog, Session(Sample(a, 1), Sample(b, 1)));
            Check(incompatible.Status == "inconsistent" && incompatible.Representative == null, "incompatible evidence returns no point");
            Check(estimator.Estimate(new Catalog([a with { Anycast = true }]), Session(Sample(a, 1))).Status == "insufficient-data", "anycast excluded");
            Check(estimator.Estimate(new Catalog([a with { LocationVerified = false }]), Session(Sample(a, 1))).Status == "insufficient-data", "unverified coordinates excluded");
            var stale = Sample(a, 1) with { MeasuredUtc = DateTimeOffset.UtcNow.AddDays(-1) };
            Check(estimator.Estimate(catalog, Session(stale)).Constraints.Count == 0, "stale evidence excluded");
            var future = Sample(a, 1) with { MeasuredUtc = DateTimeOffset.UtcNow.AddHours(1) };
            Check(estimator.Estimate(catalog, Session(future)).Constraints.Count == 0, "future evidence excluded");
            Reject(() => estimator.Estimate(catalog, Session(Sample(a, 1) with { Address = "192.0.2.99" })), "changed endpoint rejected");
            Reject(() => estimator.Estimate(catalog, Session(Sample(a, -1))), "negative RTT rejected");
            Reject(() => estimator.Estimate(catalog, Session(Sample(a, 1) with { Address = "2001:db8::1" })), "mixed address families rejected");
            Reject(() => new GeoPoint(double.NaN, 0).Validate(), "nonfinite coordinates rejected");
            Reject(() => new Catalog([a, a]).Validate(), "duplicate anchor IDs rejected");
            Reject(() => estimator.Estimate(catalog, Session() with { Observations = null! }), "null measurement list rejected");
            Reject(() => estimator.Estimate(catalog, Session(Sample(a, 1) with { Samples = null! })), "null samples rejected");
            Check(single.Model.ResolutionKm == 25 && single.Model.TimingSlackMs == 0, "reported model matches actual estimator settings");

            var router = Anchor("router", 0, .05, "192.0.2.3") with { Kind = "router" };
            var traced = Sample(a, 10) with { Route = [new(1, router.Address, 1, "TtlExpired"), new(2, "192.0.2.77", .1, "TtlExpired")] };
            var topology = estimator.Estimate(new([a, router]), Session(traced));
            Check(topology.Constraints.Count == 2 && topology.Constraints.Any(c => c.Evidence == "known-router-ttl-rtt"), "known router adds independent geometric constraint");
            var observerSession = Session(traced) with { Mode = "observer-to-target", TargetAddress = a.Address };
            Check(estimator.Estimate(new([a, router]), observerSession).Constraints.Count == 1, "observer trace cannot locate the target by its intermediate RTTs");
            var duplicateRouter = router with { Id = "router2", Position = new(40, 40) };
            Check(estimator.Estimate(new([a, router, duplicateRouter]), Session(traced)).Constraints.Count == 1, "ambiguous router address excluded");

            var dateAnchor = Anchor("dateline", 0, 179.9);
            var dateEstimate = estimator.Estimate(new([dateAnchor]), Session(Sample(dateAnchor, 3)));
            Check(Contains(dateEstimate, new(0, -179.9)) && Contains(dateEstimate, new(0, 179.8)), "both sides of antimeridian retained");
            var polarAnchor = Anchor("north", 89.9, 0);
            Check(Contains(estimator.Estimate(new([polarAnchor]), Session(Sample(polarAnchor, 3))), new(89.99, 160)), "polar candidate retained");
            var capped = new LatencyEstimator(new() { MaxCells = 72, ResolutionKm = 1 }).Estimate(new([a]), Session(Sample(a, 100)));
            Check(capped.ResolutionLimited && capped.Cells.Count <= 72 && Contains(capped, a.Position), "cell budget retains coarse candidate region");

            var (demoCatalog, demoSession, truth) = SyntheticNetwork.Create();
            var initialPlan = AdaptivePlanner.Next(demoCatalog, demoSession, null, 6);
            Check(initialPlan.Any(n => n.Id == "tokyo") && initialPlan.Any(n => n.Id == "new-york") &&
                initialPlan.Any(n => n.Id == "cape-town") && initialPlan.Any(n => n.Id == "sydney"), "initial plan covers continents");
            var localAnchor = demoCatalog.Landmarks.Single(n => n.Id == "valencia");
            var focusedSession = Session(Sample(localAnchor, 2));
            var focusedEstimate = estimator.Estimate(demoCatalog, focusedSession);
            Check(AdaptivePlanner.Next(demoCatalog, focusedSession, focusedEstimate, 3).All(n => Geography.DistanceKm(n.Position, truth) < 1500), "low RTT drives regional follow-up probes");
            double previousArea = double.PositiveInfinity;
            for (int round = 0; round < 5; round++)
            {
                var before = estimator.Estimate(demoCatalog, demoSession);
                var next = AdaptivePlanner.Next(demoCatalog, demoSession, before, 3);
                Check(next.All(n => demoSession.Observations.All(o => o.LandmarkId != n.Id)), "planner does not repeat completed anchors " + round);
                demoSession.Observations.AddRange(next.Select(n => SyntheticNetwork.Observe(n, truth)));
                var after = estimator.Estimate(demoCatalog, demoSession);
                Check(Contains(after, truth) && after.CandidateAreaKm2 <= previousArea, "refinement retains truth and reduces area " + round);
                previousArea = after.CandidateAreaKm2;
            }
            Check(previousArea < 100_000, "synthetic local anchors narrow region");
            var pairCatalog = new Catalog([a with { SiteId = "same" }, b with { SiteId = "same" }]);
            Check(AdaptivePlanner.Next(pairCatalog, Session(), null, 2).Count == 1, "planner counts colocated references once");

            var fake = new FakeEcho();
            using (var probe = new NetworkProbe(new() { Samples = 2, Trace = true, MaxHops = 3, PacketsPerSecond = 20 }, fake))
            {
                var observed = await probe.MeasureAsync(a.Id, a.Address);
                Check(observed.Samples.Count == 2 && observed.Route.Count == 2 && fake.Ttls.SequenceEqual(new[] { 128, 128, 1, 2 }), "probe separates samples and traceroute, stops at destination");
                Check(probe.SentPackets == 4, "bounded packet accounting");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await probe.MeasureAsync(a.Id, a.Address, cancelled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { Check(probe.SentPackets == 4, "cancelled probe sends no packets"); }
            }
            string directory = Path.Combine(Path.GetTempPath(), "geo-latency-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                string file = Path.Combine(directory, "session.json");
                await JsonFiles.WriteAsync(file, demoSession);
                var restored = await JsonFiles.ReadAsync<MeasurementSession>(file);
                Check(restored.Synthetic && restored.Observations.Count == demoSession.Observations.Count &&
                    estimator.Estimate(demoCatalog, restored).Status == "candidate-region", "session JSON roundtrip remains usable offline");


            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            await EmbeddedTests.Run(Check);
            await WorkbenchTests.Run(Check);
            await PrecisionTests.Run(Check);
            await AffinityTests.Run(Check);
            LocalityTests.Run(Check);
            CalibrationTests.Run(Check);
            await TopologyTests.Run(Check);
            Console.WriteLine($"OK: {passed} comprobaciones sin sondeos a Internet.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

    }
}

sealed class FakeEcho : IEchoTransport
{
    public List<int> Ttls { get; } = [];
    public Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct)
    {
        Ttls.Add(ttl);
        return Task.FromResult(ttl == 1 ? new EchoResult("TtlExpired", "192.0.2.3", 1) : new("Success", address.ToString(), 2));
    }
}
