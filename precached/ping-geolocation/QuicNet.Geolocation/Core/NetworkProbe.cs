using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuicNet.Geolocation;

public sealed record ProbeOptions
{
    public int Samples { get; init; } = 4;
    public int TimeoutMs { get; init; } = 1500;
    public int PacketsPerSecond { get; init; } = 5;
    public int MaxHops { get; init; } = 24;
    public bool Trace { get; init; }
    public void Validate()
    {
        if (Samples is < 1 or > 20 || TimeoutMs is < 100 or > 10_000 || PacketsPerSecond is < 1 or > 20 || MaxHops is < 1 or > 64)
            throw new InvalidDataException("Opciones de sondeo fuera de rango.");
    }
}

public sealed record EchoResult(string Status, string? Address, double? RttMs)
{
    public string? TimingMethod { get; init; }
}
public interface IEchoTransport
{
    Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct);
}

public sealed class IcmpTransport(bool measureElapsed = true) : IEchoTransport
{
    private static readonly byte[] Payload = new byte[32];
    public async Task<EchoResult> SendAsync(IPAddress address, int ttl, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        var watch = Stopwatch.StartNew();
        try
        {
            // Cancellation reaches the actual send, rather than abandoning an outstanding ping.
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(timeoutMs), Payload, new PingOptions(ttl, false), ct);
            // Stopwatch brackets the entire native/managed operation. Scheduling and local overhead
            // can only widen this duration; nothing is subtracted to infer a path-only latency.
            double elapsed = watch.Elapsed.TotalMilliseconds;
            return new(reply.Status.ToString(), reply.Address?.ToString(), reply.Status switch
            {
                IPStatus.Success => measureElapsed ? Math.Max(elapsed, reply.RoundtripTime) : reply.RoundtripTime,
                IPStatus.TtlExpired => watch.Elapsed.TotalMilliseconds,
                _ => null
            }) { TimingMethod = measureElapsed && Stopwatch.IsHighResolution && reply.Status == IPStatus.Success ? "elapsed-upper-v1" : null };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is PingException or SocketException or PlatformNotSupportedException or System.Security.SecurityException)
        { return new("Error: " + ex.Message, null, null); }
    }
}

public sealed class NetworkProbe : IDisposable
{
    private readonly ProbeOptions _options;
    private readonly IEchoTransport _transport;
    private readonly ProbeBudget _budget;
    private readonly bool _ownsBudget;
    private int _sent;
    public int SentPackets => Volatile.Read(ref _sent);
    public NetworkProbe(ProbeOptions? options = null, IEchoTransport? transport = null, ProbeBudget? sharedBudget = null)
    {
        _options = options ?? new(); _options.Validate(); _transport = transport ?? new IcmpTransport(measureElapsed: true);
        _ownsBudget = sharedBudget is null; _budget = sharedBudget ?? new(_options.PacketsPerSecond, 100000);
    }
    public async Task<Observation> MeasureAsync(string landmarkId, string address, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ip = IPAddress.Parse(address); var samples = new List<ProbeSample>(); var route = new List<RouteHop>();
        var measured = DateTimeOffset.UtcNow;
        try
        {
            for (int i = 0; i < _options.Samples; i++)
            {
                var reply = await SendAsync(ip, 128, ct).ConfigureAwait(false);
                samples.Add(new(reply.RttMs, reply.Status) { TimingMethod = reply.TimingMethod });
            }
            if (_options.Trace)
                for (int ttl = 1; ttl <= _options.MaxHops; ttl++)
                {
                    var reply = await SendAsync(ip, ttl, ct).ConfigureAwait(false);
                    route.Add(new(ttl, reply.Address, reply.RttMs, reply.Status));
                    if (reply.Status is "Success" or "DestinationHostUnreachable" or "DestinationNetworkUnreachable" ||
                        reply.Status.StartsWith("Error:", StringComparison.Ordinal)) break;
                }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ProbeBudgetExceededException) { }
        return new(landmarkId, ip.ToString(), measured, samples) { Route = route };
    }
    private async Task<EchoResult> SendAsync(IPAddress address, int ttl, CancellationToken ct)
    {
        await _budget.AcquireAsync(ct).ConfigureAwait(false); Interlocked.Increment(ref _sent);
        return await _transport.SendAsync(address, ttl, _options.TimeoutMs, ct).ConfigureAwait(false);
    }
    public void Dispose() { if (_ownsBudget) _budget.Dispose(); }
}
