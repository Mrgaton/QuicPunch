using System.Net;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Geolocation.Workbench;
using QuicNet.Networking;
using QuicNet.Serialization;

namespace QuicNet.WebUi.Modules;

internal sealed record GeolocationMapMarker(double x, double y, string label);
internal sealed record GeolocationMapScene(bool reset, SpatialSupport? heat, GeoPoint? center, double? radius,
    GeolocationMapMarker[] markers, PublishedLocation? prior)
{
    public GeoPoint? estimate { get; init; }
    public double? estimateRadius { get; init; }
    public LocalityCandidate[] places { get; init; } = [];
    public SpatialSupport? conditionalHeat { get; init; }
    public GeoPoint? modelPoint { get; init; }
    public LandmarkMeasurement[] references { get; init; } = [];
}
internal sealed record GeolocationWorkbenchView(long version, bool running, string? runKey, WorkbenchSettings settings,
    string title, string description, string[] notes, GeolocationMapScene map, int packetCount,
    PacketRecord[] packets, string[] logs, string[] addresses, string? error)
{
    public LocalityAssessment? localities { get; init; }
    public WorkbenchAnalysis? analysis { get; init; }
    public WorkbenchCoverage coverage { get; init; } = WorkbenchCoverage.Current;
    public WorkbenchExplanation? explanation { get; init; }
}

/// <summary>Created only when the settings panel is opened. No workbench state enters global status broadcasts.</summary>
internal sealed class GeolocationWorkbenchApiModule : IAsyncDisposable
{
    private static readonly SharedJsonContext BrowserJson = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private readonly SemaphoreSlim _edits = new(1, 1);
    private readonly Func<IEnumerable<IPAddress>> _addresses;
    private readonly AutomaticIpGeolocation? _automatic;
    private readonly string _directory;
    private readonly CancellationToken _shutdown;
    private readonly IEchoTransport? _transport;
    private WorkbenchSession? _session;
    private CancellationTokenSource? _panelStop;
    private string? _owner;
    private DateTimeOffset _lastSeen;
    private Task _manual = Task.CompletedTask;
    private bool _disposed;

    internal GeolocationWorkbenchApiModule(string directory, Func<IEnumerable<IPAddress>> addresses,
        AutomaticIpGeolocation? automatic, CancellationToken shutdown, IEchoTransport? transport = null)
    { _directory = directory; _addresses = addresses; _automatic = automatic; _shutdown = shutdown; _transport = transport; }

    internal async Task<GeolocationWorkbenchView> OpenAsync(string owner)
    {
        if (!Guid.TryParse(owner, out _)) throw new InvalidDataException("Panel no válido.");
        await _edits.WaitAsync(_shutdown);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_owner != owner)
            {
                await CloseCoreAsync();
                _session = new WorkbenchSession(_directory, record: false, transport: _transport);
                _panelStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
                _owner = owner;
                _ = WatchPanelAsync(owner, _panelStop.Token);
            }
            _lastSeen = DateTimeOffset.UtcNow;
            return View();
        }
        finally { _edits.Release(); }
    }

    private async Task WatchPanelAsync(string owner, CancellationToken token)
    {
        try
        {
            using var clock = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await clock.WaitForNextTickAsync(token))
            {
                await _edits.WaitAsync(token);
                try
                {
                    if (_owner != owner) return;
                    if (DateTimeOffset.UtcNow - _lastSeen > TimeSpan.FromSeconds(20)) { await CloseCoreAsync(); return; }
                }
                finally { _edits.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task<GeolocationWorkbenchView?> ReadAsync(string owner, long? version)
    {
        await _edits.WaitAsync(_shutdown);
        try
        {
            RequireOwner(owner); _lastSeen = DateTimeOffset.UtcNow;
            return version == _session!.Version ? null : View();
        }
        finally { _edits.Release(); }
    }

    internal async Task<GeolocationWorkbenchView> StartAsync(string owner, WorkbenchSettings settings)
    {
        settings = settings with { }; settings.Validate();
        await _edits.WaitAsync(_shutdown);
        try
        {
            RequireOwner(owner); _lastSeen = DateTimeOffset.UtcNow;
            if (!settings.Offline)
            {
                var ips = _addresses().Where(EmbeddedCatalog.IsPublic).Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToArray();
                if (ips.Length == 0) throw new InvalidDataException("Activa la conexión directa para medir esta red, o elige Sin red.");
                if (!string.IsNullOrWhiteSpace(settings.Ip) && !ips.Contains(IPAddress.Parse(settings.Ip.Trim())))
                    throw new InvalidDataException("Selecciona una IP pública de este equipo; los sondeos miden esta conexión.");
            }
            await _session!.StopAsync(); await _manual;
            var lease = !settings.Offline && _automatic != null ? await _automatic.BeginManualAsync(_panelStop!.Token) : null;
            try { await _session.StartAsync(settings, _panelStop!.Token); }
            catch { lease?.Dispose(); throw; }
            _manual = FinishManualAsync(_session.Completion, lease);
            return View();
        }
        finally { _edits.Release(); }
    }

    private static async Task FinishManualAsync(Task completion, IDisposable? lease)
    { try { await completion.ConfigureAwait(false); } finally { lease?.Dispose(); } }

    internal async Task StopAsync(string owner, bool close = false)
    {
        await _edits.WaitAsync();
        try
        {
            // A delayed close from a former iframe must not stop the replacement panel.
            if (_owner != owner) return;
            if (close) await CloseCoreAsync();
            else { _lastSeen = DateTimeOffset.UtcNow; await _session!.StopAsync(); await _manual; }
        }
        finally { _edits.Release(); }
    }

    private void RequireOwner(string owner)
    { if (_owner != owner || _session == null) throw new InvalidDataException("Vuelve a abrir el panel de localización."); }

    private GeolocationWorkbenchView View()
    {
        var inspection = _session!.Inspect(); var preview = inspection.Preview; var update = preview.Update;
        var description = LocationDescription.Describe(update, preview.Running);
        return new(inspection.Version, preview.Running, preview.StartedUtc?.ToString("O"), preview.Settings,
            description.Title, description.Coordinates + " " + description.Explanation, update?.Notes.ToArray() ?? [],
            new(false, update?.Support, update?.Region?.Representative, update?.Region?.CoveringRadiusKm,
                (update?.Routers ?? []).Select(r => new GeolocationMapMarker(r.Published.Position.Longitude,
                    r.Published.Position.Latitude, $"{r.Published.City} · {r.RttMs:F2} ms · router sin verificar")).ToArray(), update?.DatabasePrior)
                { conditionalHeat = update?.RegionalRefinement is { Region.Status: "candidate-region" } conditional ? conditional.Support : null,
                  modelPoint = update?.Localities?.Fit?.Position, references = update?.Enhanced?.Measurements.ToArray() ?? [], places = update?.Localities?.Candidates.ToArray() ?? [] },
            preview.PacketCount, inspection.Packets, inspection.Logs,
            _addresses().Where(EmbeddedCatalog.IsPublic).Select(a => a.ToString()).Distinct().ToArray(), preview.Error)
            { localities = update?.Localities, analysis = WorkbenchAnalysis.Create(update, preview.Settings, preview.Running), explanation = WorkbenchExplanation.Create(update) };
    }

    private async Task CloseCoreAsync()
    {
        _panelStop?.Cancel();
        if (_session != null) await _session.DisposeAsync();
        await _manual;
        _session = null; _owner = null; _panelStop?.Dispose(); _panelStop = null;
    }

    internal async Task<bool> HandleAsync(string path, HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!path.StartsWith("/api/geolocation/workbench/", StringComparison.Ordinal)) return false;
        response.Headers["Cache-Control"] = "no-store";
        string action = path["/api/geolocation/workbench/".Length..];
        try
        {
            if (action == "state" && request.HttpMethod == "GET")
            {
                var view = await ReadAsync(request.QueryString["client"] ?? "", long.TryParse(request.QueryString["version"], out long v) ? v : null);
                if (view == null) response.StatusCode = 204;
                else await WriteAsync(response, view);
                return true;
            }
            if (action is not ("open" or "start" or "stop" or "close")) { response.StatusCode = 404; return true; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; response.Headers["Allow"] = "POST"; return true; }
            using var body = await WebUiContext.ReadJsonAsync(request, path);
            if (body.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Faltan los datos del panel.");
            string owner = body.RootElement.TryGetProperty("client", out var client) ? client.GetString() ?? "" : "";
            if (action == "open") await WriteAsync(response, await OpenAsync(owner));
            else if (action == "start")
            {
                if (!body.RootElement.TryGetProperty("settings", out var configuration)) throw new InvalidDataException("Falta la configuración.");
                var settings = configuration.Deserialize(GeoJsonContext.Default.WorkbenchSettings)
                    ?? throw new InvalidDataException("Falta la configuración.");
                await WriteAsync(response, await StartAsync(owner, settings));
            }
            else { await StopAsync(owner, close: action == "close"); response.StatusCode = 204; }
        }
        catch (ArgumentException ex) { await WebUiContext.WriteJsonAsync(response, new WebUiServerMessage2(false, ex.Message), 400); }
        return true;
    }

    private static async Task WriteAsync(HttpListenerResponse response, GeolocationWorkbenchView view)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(view, BrowserJson.GeolocationWorkbenchView);
        response.ContentType = "application/json; charset=utf-8"; response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    public async ValueTask DisposeAsync()
    {
        await _edits.WaitAsync();
        try { if (_disposed) return; _disposed = true; await CloseCoreAsync(); }
        finally { _edits.Release(); }
    }
}
