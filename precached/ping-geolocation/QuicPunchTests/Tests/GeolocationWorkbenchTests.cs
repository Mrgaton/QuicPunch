using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;
using QuicNet.WebUi;
using QuicNet.WebUi.Modules;

namespace QuicPunchTests.Tests;

internal static class GeolocationWorkbenchTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Echo : IEchoTransport
    {
        private readonly Dictionary<string, Landmark> _anchors = EmbeddedCatalog.Default.Anchors.GroupBy(a => a.Address).ToDictionary(g => g.Key, g => g.First());
        internal int Requests;
        public async Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken token)
        {
            Interlocked.Increment(ref Requests); await Task.Delay(5, token);
            double rtt = _anchors.TryGetValue(address.ToString(), out var anchor) ? Geography.DistanceKm(anchor.Position, new(39.47, -.376)) * 2 / 180 + 2 : 100;
            return new("Success", address.ToString(), rtt) { TimingMethod = "elapsed-upper-v1" };
        }
    }

    private static int FreePort()
    { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }

    internal static async Task RunAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "QuicNet-workbench-" + Guid.NewGuid().ToString("N"));
        using var shutdown = new CancellationTokenSource();
        var echo = new Echo();
        await using (var module = new GeolocationWorkbenchApiModule(directory, () => [IPAddress.Parse("81.2.69.160")], null, shutdown.Token, echo))
        {
            string owner = Guid.NewGuid().ToString();
            var initial = await module.OpenAsync(owner);
            Check(echo.Requests == 0 && !Directory.Exists(directory), "Opening the panel started probes or created an archive");
            Check(await module.ReadAsync(owner, initial.version) == null, "Unchanged state should not be copied/transferred");
            bool rejected = false;
            try { await module.StartAsync(owner, new() { Ip = "8.8.8.8" }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && echo.Requests == 0, "Another device's IP was accepted for on-device measurements");
            await module.StartAsync(owner, new() { Ip = "81.2.69.160", Rounds = 1, Samples = 1, BatchSize = 6,
                Seconds = 3, TraceCount = 1, MaxHops = 1, PacketsPerSecond = 20, MaxCells = 300, ResolutionKm = 1000 });
            GeolocationWorkbenchView? result;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                do { await Task.Delay(20, deadline.Token); result = await module.ReadAsync(owner, null); } while (result!.running);
            }
            Check(echo.Requests > 0 && result.packetCount > 0 && result.logs.Length > 0 && result.map.prior != null,
                "The real workbench pipeline did not publish packets, logs and map data");
            Check(result.packets.Length <= 100 && !Directory.Exists(directory), "Panel inspection is unbounded or wrote an archive");
            Check(result.localities != null && !string.IsNullOrWhiteSpace(result.localities.Summary),
                "The real workbench omitted its locality evidence assessment");
            var offline = await module.StartAsync(owner, WorkbenchSettings.Preset("Rápido") with { Ip = "81.2.69.160", Offline = true });
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                while (offline.running) { await Task.Delay(10, deadline.Token); offline = (await module.ReadAsync(owner, null))!; }
            Check(offline.localities is { Status: "published-only" } && offline.analysis?.Probes == 0,
                "Offline workbench omitted the published reference or reported network probes");
            Check(result.analysis?.Evidence is not null && result.analysis.References > 0,
                "The workbench omitted the hierarchical engine evidence");
            Check(result.explanation?.Measurements.Length > 0 && result.map.references.Length > 0 && result.map.estimate is null,
                "The web omitted measured reference evidence or promoted a model point to device location");
            await module.StartAsync(owner, WorkbenchSettings.Preset("Rápido") with { Ip = "81.2.69.160" });
            await module.StopAsync(owner, close: true);
            string replacement = Guid.NewGuid().ToString();
            await module.OpenAsync(replacement); await module.StopAsync(owner, close: true);
            Check(await module.ReadAsync(replacement, null) != null, "An old iframe closed a replacement panel");
            await module.StopAsync(replacement, close: true);
        }

        var server = new WebUiServer(capabilities: NodeCapabilities.CreateDesktop(false), cts: shutdown, port: FreePort()); server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(server.Url) };
            var field = typeof(WebUiServer).GetField("_geolocationWorkbench", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await http.GetStringAsync("settings.html"); await http.GetStringAsync("api/status");
            Check(field.GetValue(server) == null, "Ordinary app/settings startup created the workbench");
            string owner = Guid.NewGuid().ToString();
            StringContent Body() => new(JsonSerializer.Serialize(new { client = owner }), Encoding.UTF8, "application/json");
            using var denied = await http.PostAsync("api/geolocation/workbench/open", Body());
            Check(denied.StatusCode == HttpStatusCode.Forbidden && field.GetValue(server) == null, "Missing CSRF created a session");
            using var html = await http.GetAsync("geolocation/workbench.html");
            Check(html.IsSuccessStatusCode && html.Headers.GetValues("Content-Security-Policy").Single().Contains("https://tile.openstreetmap.org"), "The isolated map cannot display street tiles");
            using var root = await http.GetAsync("/");
            Check(!root.Headers.GetValues("Content-Security-Policy").Single().Contains("tile.openstreetmap.org"), "Street tiles weakened global app CSP");
            Check((await http.GetStringAsync("geolocation/map.js")).Contains("export async function create"), "Shared renderer is not served");
            using var geography = JsonDocument.Parse(await http.GetStringAsync("geolocation/map-data"));
            Check(geography.RootElement.GetProperty("countries").GetArrayLength() > 100, "Local map is missing");
            using var localities = JsonDocument.Parse(await http.GetStringAsync("geolocation/localities?south=41.2&west=1.8&north=41.6&east=2.4"));
            Check(localities.RootElement.EnumerateArray().Any(p => p.GetProperty("name").GetString() == "Barcelona"),
                "Local map endpoint lost the bundled infrastructure labels or camelCase serialization");
            using var invalidBounds = await http.GetAsync("geolocation/localities?south=NaN&west=0&north=90&east=10");
            Check(invalidBounds.StatusCode == HttpStatusCode.BadRequest, "Invalid map bounds must be rejected");
            http.DefaultRequestHeaders.Add("X-QuicPunch-CSRF", server.CsrfToken);
            using var open = await http.PostAsync("api/geolocation/workbench/open", Body()); open.EnsureSuccessStatusCode();
            using var opened = JsonDocument.Parse(await open.Content.ReadAsStringAsync());
            Check(opened.RootElement.GetProperty("settings").TryGetProperty("rounds", out _), "Nested browser contracts must use camelCase");
            Check(opened.RootElement.GetProperty("settings").GetProperty("maximumProbes").GetInt32() == 4000 &&
                opened.RootElement.GetProperty("analysis").GetProperty("status").GetString() == "Listo para empezar" &&
                opened.RootElement.GetProperty("coverage").GetProperty("places").GetInt32() == LocalityCatalog.Default.Count,
                "The browser contract omitted new engine settings, analysis or actual catalog coverage");
            using var close = await http.PostAsync("api/geolocation/workbench/close", Body()); close.EnsureSuccessStatusCode();
        }
        finally { server.Stop(); }
        Console.WriteLine("Verified lazy workbench, synthetic network through the real engine, packets/logs, bounded inspections, close/reopen ownership, CSRF, isolated CSP and embedded map assets.");
    }

    // Isolated browser fixture: the real HTTP server, without a node or any automatic network measurements.
    internal static async Task<int> ServeAsync()
    {
        using var shutdown = new CancellationTokenSource();
        var server = new WebUiServer(capabilities: NodeCapabilities.CreateDesktop(false), cts: shutdown, port: FreePort()); server.Start();
        Console.WriteLine("GEOLOCATION-TEST-URL=" + server.Url); Console.Out.Flush();
        try { await Console.In.ReadLineAsync(); }
        finally { server.Stop(); shutdown.Cancel(); }
        return 0;
    }
}
