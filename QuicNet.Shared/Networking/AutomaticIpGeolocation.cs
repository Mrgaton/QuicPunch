using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using QuicNet.Geolocation;
using QuicNet.Serialization;

namespace QuicNet.Networking;

// A compact summary is enough for the app; do not broadcast large rasters/cell lists in every status snapshot.
internal sealed record ConnectionLocation(string address, string state, string? level = null,
    string? phase = null, double? latitude = null, double? longitude = null, double? radiusKm = null,
    int measuredLandmarks = 0, int sentPackets = 0, string? error = null,
    string? publishedCity = null, string? countryCode = null)
{
    public string? localitySummary { get; init; }
    public string? localityStatus { get; init; }
    public double? modelSpreadKm { get; init; }
}
internal sealed record AutomaticGeolocationStatus(bool running, int pending, DateTimeOffset? nextAllowedUtc,
    ConnectionLocation[] locations, string? error);
internal sealed record GeolocationHistory(DateTimeOffset? nextAllowedUtc, ConnectionLocation[] locations);

/// <summary>On-device measurements only. Coalesces current public IPs and never probes a contact from this machine.</summary>
internal sealed class AutomaticIpGeolocation : IDisposable
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly string _path;
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Func<IPAddress, Action<LocationUpdate>, CancellationToken, Task> _analyze;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<string, ConnectionLocation> _history = new(StringComparer.Ordinal);
    private HashSet<string> _current = new(StringComparer.Ordinal);
    private CancellationTokenSource? _active;
    private TaskCompletionSource? _activeCompletion;
    private int _manualPauses;
    private DateTimeOffset? _nextAllowed;
    private string? _error;
    private bool _disposed;
    internal event Action? Changed;
    internal Task Completion { get; }
    private Task? _monitor;

    // Explicit manual tests can run again, but must never overlap an automatic measurement.
    internal async Task<IDisposable> BeginManualAsync(CancellationToken token)
    {
        Task idle;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _manualPauses++;
            _active?.Cancel();
            idle = _activeCompletion?.Task ?? Task.CompletedTask;
        }
        try { await idle.WaitAsync(token).ConfigureAwait(false); return new ManualLease(this); }
        catch { EndManual(); throw; }
    }

    private sealed class ManualLease(AutomaticIpGeolocation owner) : IDisposable
    {
        private AutomaticIpGeolocation? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndManual();
    }

    private void EndManual()
    {
        lock (_gate) _nextAllowed = _now() + MinimumInterval;
        Save();
        lock (_gate)
        {
            _manualPauses--;
            if (!_disposed && _changed.CurrentCount == 0) _changed.Release();
        }
        Changed?.Invoke();
    }

    internal void StartMonitoring(Func<IEnumerable<IPAddress>> addresses)
    {
        lock (_gate)
        {
            if (_disposed || _monitor != null) return;
            var token = _stop.Token;
            _monitor = Task.Run(async () =>
            {
                try
                {
                    // Inspect already known addresses; this timer never performs network discovery.
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                    do { Observe(addresses()); }
                    while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            });
        }
    }

    internal AutomaticIpGeolocation(string directory, CancellationToken cancellation = default,
        Func<IPAddress, Action<LocationUpdate>, CancellationToken, Task>? analyze = null,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _path = Path.Combine(directory, "geoip", "connection-locations.json");
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _analyze = analyze ?? AnalyzeAsync;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), SharedJsonContext.Default.GeolocationHistory);
                if (saved?.locations != null)
                {
                    _nextAllowed = saved.nextAllowedUtc;
                    foreach (var item in saved.locations)
                        if (item != null && IPAddress.TryParse(item.address, out var ip) && EmbeddedCatalog.IsPublic(ip))
                        {
                            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                            // Keep measured results/cooldowns, but refresh the published
                            // labels after a catalogue update instead of retaining DB-IP cities.
                            var published = EmbeddedCatalog.Default.Lookup(ip);
                            _history[ip.ToString()] = item with
                            {
                                address = ip.ToString(), state = item.state == "running" ? "interrupted" : item.state,
                                publishedCity = string.IsNullOrWhiteSpace(published?.City) ? null : published.City,
                                countryCode = published?.Country
                            };
                        }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _error = "No se pudo leer la caché de geolocalización: " + ex.Message; }
        Completion = Task.Run(WorkAsync);
    }

    internal AutomaticGeolocationStatus Status
    {
        get
        {
            lock (_gate) return new(_active != null, _current.Count(a => !_history.ContainsKey(a)), _nextAllowed,
                _current.Order(StringComparer.Ordinal).Select(a => _history.GetValueOrDefault(a) ?? new(a, "pending")).ToArray(), _error);
        }
    }

    internal void Observe(IEnumerable<IPAddress> addresses)
    {
        var current = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(EmbeddedCatalog.IsPublic).Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            if (_disposed || _current.SetEquals(current)) return;
            // A changed network invalidates a measurement in flight, even if another address survives.
            _active?.Cancel();
            _current = current;
            if (_changed.CurrentCount == 0) _changed.Release();
        }
        Changed?.Invoke();
    }

    private async Task WorkAsync()
    {
        var token = _stop.Token;
        try
        {
            while (true)
            {
                await _changed.WaitAsync(token).ConfigureAwait(false);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string? address;
                    TimeSpan wait;
                    lock (_gate)
                    {
                        if (_manualPauses > 0) break;
                        address = _current.Order(StringComparer.Ordinal).FirstOrDefault(a => !_history.ContainsKey(a));
                        wait = (_nextAllowed ?? _now()) - _now();
                    }
                    if (address == null) break;
                    if (wait > TimeSpan.Zero) { await _delay(wait, token).ConfigureAwait(false); continue; }
                    CancellationTokenSource run;
                    lock (_gate)
                    {
                        if (_manualPauses > 0 || !_current.Contains(address) || _history.ContainsKey(address)) continue;
                        run = CancellationTokenSource.CreateLinkedTokenSource(token);
                        _active = run;
                        _activeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _history[address] = new(address, "running");
                        _nextAllowed = _now() + MinimumInterval;
                    }
                    // Persist the attempt before sending packets: restarting must not bypass the limit.
                    Save(); Changed?.Invoke();
                    try
                    {
                        await _analyze(IPAddress.Parse(address), update =>
                        {
                            lock (_gate)
                            {
                                if (run.IsCancellationRequested) return;
                                var region = update.Region;
                                bool bounded = region?.Status == "candidate-region" && update.Level != EvidenceLevel.Conflicting;
                                _history[address] = new(address, "running", update.Level.ToString(), update.Phase,
                                    bounded ? region?.Representative?.Latitude : null,
                                    bounded ? region?.Representative?.Longitude : null,
                                    bounded ? region?.CoveringRadiusKm : null, update.MeasuredLandmarks, update.SentPackets,
                                    publishedCity: update.DatabasePrior?.City, countryCode: update.DatabasePrior?.Country)
                                    { localitySummary = update.Localities?.Summary, localityStatus = update.Localities?.Status, modelSpreadKm = update.Localities?.ModelSpreadKm };
                            }
                            Changed?.Invoke();
                        }, run.Token).ConfigureAwait(false);
                        lock (_gate) _history[address] = _history[address] with { state = run.IsCancellationRequested ? "interrupted" : "complete" };
                    }
                    catch (OperationCanceledException) when (run.IsCancellationRequested)
                    { lock (_gate) _history[address] = _history[address] with { state = "interrupted" }; }
                    catch (Exception ex)
                    { lock (_gate) _history[address] = _history[address] with { state = "failed", error = ex.Message }; }
                    finally
                    {
                        TaskCompletionSource? idle;
                        lock (_gate) { _active = null; idle = _activeCompletion; _activeCompletion = null; _nextAllowed = _now() + MinimumInterval; }
                        run.Dispose();
                        Save(); idle?.TrySetResult(); Changed?.Invoke();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Save()
    {
        lock (_saveGate) SaveCore();
    }

    private void SaveCore()
    {
        string temporary = _path + ".tmp";
        try
        {
            GeolocationHistory snapshot;
            lock (_gate) snapshot = new(_nextAllowed, _history.Values.ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, SharedJsonContext.Default.GeolocationHistory));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { lock (_gate) _error = "No se pudo guardar la caché de geolocalización: " + ex.Message; }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static async Task AnalyzeAsync(IPAddress address, Action<LocationUpdate> publish, CancellationToken token)
    {
        using var run = new ProgressiveLocator().Start(new LocateRequest
        {
            PublicAddress = address, IPv6 = address.AddressFamily == AddressFamily.InterNetworkV6,
            EnableNetwork = true, Budget = TimeSpan.FromSeconds(45), Rounds = 2,
            Samples = 2, TraceCount = 2, MaxHops = 12, Parallelism = 2, PacketsPerSecond = 5
        }, publish, token);
        await run.Completion.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _stop.Cancel();
        _ = Task.WhenAll(Completion, _monitor ?? Task.CompletedTask).ContinueWith(_ => { _stop.Dispose(); _changed.Dispose(); }, TaskScheduler.Default);
    }
}
