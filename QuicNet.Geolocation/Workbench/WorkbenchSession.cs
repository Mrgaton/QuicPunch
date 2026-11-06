using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using QuicNet.Geolocation;

namespace QuicNet.Geolocation.Workbench;

public sealed record PacketRecord(int Id, string Destination, int Ttl, int TimeoutMs, string Status, DateTimeOffset StartedUtc)
{
    public string? Responder { get; init; }
    public double? RttMs { get; init; }
    public double? ElapsedMs { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public PublishedLocation? RouterLocation { get; init; }
    public bool Reconstructed { get; init; }
}
public sealed record WorkbenchEvent(int Protocol, string Kind, DateTimeOffset At, JsonElement Data);
public sealed record WorkbenchStarted(WorkbenchSettings Settings, string OutputDirectory, string? SourceDirectory = null, bool Reanalysis = false);
public sealed record WorkbenchFinished(bool Cancelled, string OutputDirectory, bool Reanalysis = false, bool Failed = false);
public sealed record WorkbenchError(string Message);
public sealed record WorkbenchSnapshot(bool Running, DateTimeOffset? StartedUtc, string? Directory, WorkbenchSettings Settings,
    LocationUpdate[] Updates, PacketRecord[] Packets, Landmark[] Landmarks, string[] Logs, string? Error);

public sealed record WorkbenchPreview(bool Running, DateTimeOffset? StartedUtc, WorkbenchSettings Settings,
    LocationUpdate? Update, int PacketCount, string? Error);
public sealed record WorkbenchInspection(long Version, WorkbenchPreview Preview, PacketRecord[] Packets, string[] Logs);

/// <summary>In-process C# workbench state. Recording is optional; previews avoid copying packet history.</summary>
public sealed class WorkbenchSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly string _root;
    private readonly bool _record;
    private readonly EmbeddedCatalog _data;
    private readonly IEchoTransport? _transport;
    private readonly List<LocationUpdate> _updates = [];
    private readonly Dictionary<int, PacketRecord> _packets = [];
    private readonly Dictionary<string, Landmark> _landmarks = [];
    private readonly List<string> _logs = [];
    private WorkbenchSettings _settings = new();
    private CancellationTokenSource? _stop;
    private Task _work = Task.CompletedTask;
    private StreamWriter? _journal;
    private string? _directory, _error;
    private bool _running, _disposed;
    private DateTimeOffset? _started;
    private long _version;
    public long Version { get { lock (_gate) return _version; } }
    public string RootDirectory => _root;

    public WorkbenchSession(string rootDirectory, EmbeddedCatalog? data = null, IEchoTransport? transport = null)
        : this(rootDirectory, true, data, transport) { }
    public WorkbenchSession(string rootDirectory, bool record, EmbeddedCatalog? data = null, IEchoTransport? transport = null)
    { _root = Path.GetFullPath(rootDirectory); _data = data ?? EmbeddedCatalog.Default; _transport = transport; _record = record; if (_record) Directory.CreateDirectory(_root); }
    public WorkbenchPreview Preview()
    { lock (_gate) return new(_running, _started, _settings with { }, _updates.LastOrDefault(), _packets.Count, _error); }
    public WorkbenchSnapshot Snapshot()
    {
        lock (_gate) return new(_running, _started, _directory, _settings with { }, _updates.ToArray(),
            _packets.Values.OrderBy(p => p.Id).ToArray(), _landmarks.Values.ToArray(), _logs.ToArray(), _error);
    }
    public WorkbenchInspection Inspect(int limit = 100)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) return new(_version, Preview(), _packets.Values.OrderByDescending(p => p.Id).Take(limit).ToArray(), _logs.TakeLast(limit).ToArray());
    }
    public async Task StartAsync(WorkbenchSettings settings, CancellationToken ct = default)
    {
        var request = settings.ToRequest();
        await _lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await CancelCoreAsync();
            Reset(settings);
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string directory = _directory ?? "";
            var token = _stop.Token;
            _work = Task.Run(() => RunAsync(request, directory, token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync()
    { await _lifecycle.WaitAsync(); try { await CancelCoreAsync(); } finally { _lifecycle.Release(); } }
    public Task Completion { get { lock (_gate) return _work; } }
    private async Task CancelCoreAsync()
    { _stop?.Cancel(); await _work.ConfigureAwait(false); _stop?.Dispose(); _stop = null; }
    private void Reset(WorkbenchSettings settings)
    {
        lock (_gate)
        {
            _updates.Clear(); _packets.Clear(); _landmarks.Clear(); _logs.Clear(); _error = null;
            _version++;
            _settings = settings with { }; _started = DateTimeOffset.UtcNow; _running = true;
            _directory = null;
            if (_record)
            {
                _directory = Path.Combine(_root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(_directory);
                _journal = new StreamWriter(Path.Combine(_directory, "events.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }
    }
    private void Emit<T>(string kind, T value)
    {
        lock (_gate)
        {

            _version++;
            if (_journal is not null)
                _journal.WriteLine(JsonSerializer.Serialize(new WorkbenchEvent(1, kind, DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(value, JsonFiles.GetTypeInfo<T>())), CompactJsonContext.WorkbenchEvent));
            switch (kind)
            {
                case "packet" when value is PacketRecord p: _packets[p.Id] = p; break;
                case "update" when value is LocationUpdate u: _updates.Add(u); Log($"Revisión {u.Revision}: {u.Phase}. {u.Change}"); break;
                case "error" when value is WorkbenchError e: _error = e.Message; Log(_error); break;
            }
        }
    }
    private static GeoJsonContext CompactJsonContext { get; } = new(new(JsonFiles.Options) { WriteIndented = false });
    private void Log(string text) { _logs.Add($"{DateTime.Now:HH:mm:ss} {text}"); if (_logs.Count > 500) _logs.RemoveAt(0); }
    private async Task RunAsync(LocateRequest request, string directory, CancellationToken ct)
    {
        bool cancelled = false, failed = false;
        try
        {
            Emit("started", new WorkbenchStarted(_settings, directory));
            if (_record) await JsonFiles.WriteAsync(Path.Combine(directory, "settings.json"), _settings, ct);
            using var manifest = JsonDocument.Parse(_data.Manifest); Emit("manifest", manifest.RootElement);
            var catalog = request.EnableNetwork ? new Catalog(_data.Anchors.ToList()) : new Catalog([]);
            lock (_gate) foreach (var a in catalog.Landmarks) _landmarks[a.Address] = a;
            Emit("catalog", catalog);
            var streaming = new ObservedTransport(_transport ?? new IcmpTransport(measureElapsed: true), _data, p => Emit("packet", p));
            var locator = new ProgressiveLocator(_data, streaming);
            using var run = locator.Start(request, update => Emit("update", update), ct);
            var result = await run.Completion.ConfigureAwait(false);
            cancelled = result.Final.Phase == "cancelled-partial";
            lock (_gate) foreach (var a in result.Catalog.Landmarks) _landmarks[a.Address] = a;
            Emit("session", result.Session);
            if (_record) await JsonFiles.WriteAsync(Path.Combine(directory, "session.json"), result.Session);
            if (_record) await JsonFiles.WriteAsync(Path.Combine(directory, "catalog.json"), result.Catalog);
            if (_record) await JsonFiles.WriteAsync(Path.Combine(directory, "result.json"), result.Final);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { failed = true; Emit("error", new WorkbenchError(ex.Message)); }
        finally
        {
            try
            {
                if (_record)
                {
                    var snapshot = Snapshot();
                    await JsonFiles.WriteAsync(Path.Combine(directory, "updates.json"), snapshot.Updates);
                    if ((cancelled || failed) && snapshot.Updates.LastOrDefault() is { } last)
                        await JsonFiles.WriteAsync(Path.Combine(directory, "partial-result.json"), last);
                }
                Emit("finished", new WorkbenchFinished(cancelled, directory, Failed: failed));
            }
            catch (Exception ex) { lock (_gate) { _error = ex.Message; Log("No se pudo guardar el resultado: " + ex.Message); } }
            lock (_gate) { _journal?.Dispose(); _journal = null; _running = false; _version++; }
        }
    }
    public string[] SavedRuns() => Directory.Exists(_root) ? Directory.EnumerateDirectories(_root).OrderDescending().Take(100).Select(Path.GetFileName).OfType<string>().ToArray() : [];
    public string ResolveRun(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name is "." or "..") throw new ArgumentException("Sesión inválida.");
        string path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Sesión fuera del directorio de trabajo.");
        return path;
    }
    public async Task LoadAsync(string directory, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            await CancelCoreAsync();
            var archive = await WorkbenchArchive.ReadAsync(directory, ct);
            lock (_gate)
            {
                _updates.Clear(); _updates.AddRange(archive.Updates); _packets.Clear(); foreach (var p in archive.Packets) _packets[p.Id] = p;
                _landmarks.Clear(); foreach (var a in archive.Landmarks) _landmarks[a.Address] = a;
                _settings = archive.Settings with { }; _directory = Path.GetFullPath(directory); _error = null; _started = null; _running = false;
                _logs.Clear(); Log("Sesión abierta sin tráfico de red: " + _directory);
                _version++;
            }
        }
        finally { _lifecycle.Release(); }
    }
    public async Task ReanalyseAsync(WorkbenchSettings settings, CancellationToken ct = default)
    {
        settings.Validate();
        if (!_record) throw new InvalidOperationException("El registro está desactivado; usa ProgressiveLocator.Replay para reanalizar datos originales.");
        await _lifecycle.WaitAsync(ct);
        try
        {
            await CancelCoreAsync();
            string source = _directory ?? throw new InvalidDataException("Abre una sesión completa antes de recalcular.");
            if (!File.Exists(Path.Combine(source, "session.json")) || !File.Exists(Path.Combine(source, "catalog.json")))
                throw new InvalidDataException("Esta exportación no contiene las muestras y el catálogo originales. Puedes inspeccionarla; para recalcular el modelo necesitas la sesión completa.");
            var session = await JsonFiles.ReadAsync<MeasurementSession>(Path.Combine(source, "session.json"), ct);
            var catalog = await JsonFiles.ReadAsync<Catalog>(Path.Combine(source, "catalog.json"), ct);
            var update = new ProgressiveLocator(_data).Replay(catalog, session, settings.ToModel());
            var packets = Snapshot().Packets;
            Reset(settings with { Offline = true, Family = session.AddressFamily });
            string target = _directory!;
            try
            {
                lock (_gate) { foreach (var a in catalog.Landmarks) _landmarks[a.Address] = a; foreach (var p in packets) _packets[p.Id] = p; }
                Emit("started", new WorkbenchStarted(_settings, target, source, true)); Emit("catalog", catalog); Emit("session", session);
                foreach (var packet in packets) Emit("packet", packet);
                Emit("update", update);
                await JsonFiles.WriteAsync(Path.Combine(target, "settings.json"), _settings, ct);
                await JsonFiles.WriteAsync(Path.Combine(target, "session.json"), session, ct);
                await JsonFiles.WriteAsync(Path.Combine(target, "catalog.json"), catalog, ct);
                await JsonFiles.WriteAsync(Path.Combine(target, "result.json"), update, ct);
                await JsonFiles.WriteAsync(Path.Combine(target, "updates.json"), new[] { update }, ct);
                Emit("finished", new WorkbenchFinished(false, target, true));
            }
            finally { lock (_gate) { _journal?.Dispose(); _journal = null; _running = false; _version++; } }
        }
        finally { _lifecycle.Release(); }
    }
    public async ValueTask DisposeAsync()
    { await _lifecycle.WaitAsync(); try { if (_disposed) return; _disposed = true; await CancelCoreAsync(); } finally { _lifecycle.Release(); } }

    private sealed class ObservedTransport(IEchoTransport inner, EmbeddedCatalog data, Action<PacketRecord> emit) : IEchoTransport
    {
        private int _sequence;
        public async Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct)
        {
            var packet = new PacketRecord(Interlocked.Increment(ref _sequence), address.ToString(), ttl, timeoutMs, "Sending", DateTimeOffset.UtcNow);
            emit(packet); var watch = Stopwatch.StartNew();
            try
            {
                var reply = await inner.SendAsync(address, ttl, timeoutMs, ct);
                var location = reply.Status == "TtlExpired" && IPAddress.TryParse(reply.Address, out var ip) ? data.LookupRouter(ip) : null;
                emit(packet with
                {
                    Status = reply.Status,
                    Responder = reply.Address,
                    RttMs = reply.RttMs,
                    ElapsedMs = watch.Elapsed.TotalMilliseconds,
                    CompletedUtc = DateTimeOffset.UtcNow,
                    RouterLocation = location
                });
                return reply;
            }
            catch (OperationCanceledException)
            { emit(packet with { Status = "Cancelled", ElapsedMs = watch.Elapsed.TotalMilliseconds, CompletedUtc = DateTimeOffset.UtcNow }); throw; }
        }
    }
}
