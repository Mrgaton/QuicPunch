using System.Net;
using System.Text.Json;
using QuicNet.Geolocation.Workbench;

namespace QuicNet.Geolocation.App.Diagnostics;

internal static class WebIntegrationTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); checks++; }
        string root = Path.Combine(Path.GetTempPath(), "quicnet-web-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            JsonTests.Run(Check);
            var settings = WorkbenchSettings.Preset("Rápido") with
            {
                EnableTunnelAssessment = false, EnableTopology = false, EnablePathMtu = true,
                EnableTcp = true, MaximumProbes = 4, TraceRepetitions = 3
            };
            var request = settings.ToRequest();
            Check(request.UseHierarchicalEngine && !request.EnableTunnelAssessment && !request.EnableTopology &&
                request.EnablePathMtu && request.EnableTcp && request.MaximumProbes == 4 && request.TraceRepetitions == 3,
                "web settings reach every new engine option");
            foreach (var bad in new[] { settings with { MaximumProbes = 0 }, settings with { MaximumProbes = 100001 }, settings with { TraceRepetitions = 4 } })
            {
                bool rejected = false; try { bad.Validate(); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "invalid probe limits rejected before starting");
            }
            var old = JsonSerializer.Deserialize("{}", GeoJsonContext.Default.WorkbenchSettings)!;
            Check(old.EnableTunnelAssessment && old.EnableTopology && !old.EnablePathMtu && !old.EnableTcp && old.MaximumProbes == 4000 && old.MaximumTraceRoutes == 16,
                "older settings keep safe defaults for new options");
            var echo = new Echo();
            await using var session = new WorkbenchSession(root, record: false, transport: echo);
            // Disable supplemental native transports: only the deterministic synthetic echo may run.
            await session.StartAsync(settings with { EnablePathMtu = false, EnableTcp = false, Seconds = 5 });
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var limited = session.Preview(); var update = limited.Update!;
            Check(limited.Error is null && !limited.Running && update.Enhanced is not null && update.SentPackets == 4 && echo.Sends == 4,
                "real hierarchical engine obeys the UI launch budget with synthetic transport");
            Check(update.Enhanced!.Measurements.Count > 0 && update.Enhanced.Measurements.All(m =>
                m.MinimumMs == 40 && m.MedianMs == 40 && m.Successes > 0 && m.Attempts >= m.Successes && !string.IsNullOrEmpty(m.Address)),
                "the engine exposes actual per-reference minimum, median, response count and provenance for explanation");
            var analysis = WorkbenchAnalysis.Create(update, limited.Settings, false);
            Check(analysis.Status == "Límite de sondeos alcanzado" && analysis.References > 0 && analysis.Evidence!.Hierarchy.Hypotheses.Count > 0,
                "partial budget result retains geographic alternatives");
            var json = JsonSerializer.SerializeToElement(analysis, GeoJsonContext.Default.WorkbenchAnalysis);
            Check(json.GetProperty("evidence").GetProperty("hierarchy").GetProperty("hypotheses").GetArrayLength() > 0 &&
                json.GetProperty("probes").GetInt32() == 4, "new web analysis contract serializes without reflection");
            await session.StartAsync(settings with { EnablePathMtu = false, EnableTcp = false, MaximumProbes = 1000, Seconds = 10 });
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                while (session.Preview().PacketCount == 0) await Task.Delay(10, deadline.Token);
            await session.StopAsync();
            var stopped = session.Preview(); int sends = echo.Sends;
            Check(!stopped.Running && stopped.Update?.Phase == "cancelled-partial" &&
                WorkbenchAnalysis.Create(stopped.Update, stopped.Settings, false).Status == "Detenido · resultado parcial",
                "stop awaits cancellation and preserves a visibly partial result");
            await session.StartAsync(WorkbenchSettings.Preset("Sin red")); await session.Completion;
            Check(echo.Sends == sends && session.Preview().Update?.Enhanced is null && !Directory.Exists(root),
                "offline restart clears previous evidence and never probes or writes an archive");
            foreach (string phase in new[] { "time-budget-exhausted", "network-context-changed", "no-landmarks" })
                Check(WorkbenchAnalysis.Create(update with { Phase = phase }, settings, false).Status != "Análisis terminado", "special completion state stays visible: " + phase);
            var tunnel = update.Enhanced!.Tunnel with { State = TunnelState.LocalTunnelIdentified, MixedRoutes = true };
            var mixed = update with { Enhanced = update.Enhanced with { Tunnel = tunnel, Fingerprints = new("mixed-network-context", [], "fixture") } };
            var mixedAnalysis = WorkbenchAnalysis.Create(mixed, settings with { EnableTunnelAssessment = true }, false);
            Check(mixedAnalysis.Tunnel == "Túnel local identificado" && mixedAnalysis.Fingerprints.Contains("suspendida"),
                "tunnel evidence never appears as an absent or confirmed-safe VPN");
            Check(WorkbenchCoverage.Current.Destinations == EmbeddedCatalog.Default.Anchors.Count &&
                WorkbenchCoverage.Current.Places == LocalityCatalog.Default.Count, "coverage reports the actual bundled data");
            Console.WriteLine($"OK: {checks} comprobaciones de integración web sin sondeos a Internet."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private sealed class Echo : IEchoTransport
    {
        public int Sends;
        public async Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Sends); await Task.Delay(5, token);
            return new("Success", address.ToString(), 40) { TimingMethod = "elapsed-upper-v1" };
        }
    }
}
