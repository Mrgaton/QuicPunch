using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace QuicNet.Geolocation;

public interface ITraceProvider
{
    Task<IReadOnlyList<RouteHop>> TraceAsync(string address, int maxHops, int timeoutMs,
        ProbeBudget budget, CancellationToken ct);
}

/// <summary>Repeated ICMP traceroute. Not advertised as Paris traceroute: managed Ping does not expose flow identity.</summary>
public sealed class RepeatedIcmpTraceProvider(IEchoTransport? transport = null, int repetitions = 2) : ITraceProvider
{
    private readonly IEchoTransport _transport = transport ?? new IcmpTransport(measureElapsed: true);
    public async Task<IReadOnlyList<RouteHop>> TraceAsync(string address, int maxHops, int timeoutMs,
        ProbeBudget budget, CancellationToken ct)
    {
        var ip = IPAddress.Parse(address);
        var hops = new List<RouteHop>();
        if (maxHops is < 1 or > 64 || repetitions is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(maxHops));
        try
        {
            // Separate end-to-end sweeps expose temporal changes. Silent TTLs do not end a trace:
            // public routers may become visible again after a filtered mobile/MPLS segment.
            for (int sweep = 0; sweep < repetitions; sweep++)
            {
                for (int ttl = 1; ttl <= maxHops; ttl++)
                {
                    await budget.AcquireAsync(ct).ConfigureAwait(false);
                    var reply = await _transport.SendAsync(ip, ttl, timeoutMs, ct).ConfigureAwait(false);
                    hops.Add(new(ttl, reply.Address, reply.RttMs, reply.Status) { Sweep = sweep + 1 });
                    if (reply.Status.StartsWith("Error:", StringComparison.Ordinal)) return hops;
                    if (reply.Status == "Success") break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ProbeBudgetExceededException) { }
        return hops;
    }
}

/// <summary>Ingests locally saved scamper JSON, including icmp-paris/udp-paris traces. Does not launch an external executable.</summary>
public sealed class OfflineScamperTraceProvider(IReadOnlyDictionary<string, string> jsonByDestination) : ITraceProvider
{
    public Task<IReadOnlyList<RouteHop>> TraceAsync(string address, int maxHops, int timeoutMs,
        ProbeBudget budget, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!jsonByDestination.TryGetValue(address, out var json)) return Task.FromResult<IReadOnlyList<RouteHop>>([]);
        if (json.Length > 2_000_000) throw new InvalidDataException("Scamper trace is too large.");
        using var doc = JsonDocument.Parse(json, new() { MaxDepth = 32 });
        var root = doc.RootElement;
        if (!root.TryGetProperty("dst", out var dst) || dst.GetString() != address)
            throw new InvalidDataException("Scamper destination missing or mismatched.");
        if (!root.TryGetProperty("start", out var start) || !start.TryGetProperty("sec", out var seconds) || !seconds.TryGetInt64(out long stamp))
            throw new InvalidDataException("Scamper trace requires an original measurement timestamp.");
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(stamp);
        if (age < TimeSpan.FromMinutes(-1) || age > TimeSpan.FromMinutes(5))
            throw new InvalidDataException("Imported traces are stale for a live run; use offline replay instead.");
        var result = new List<RouteHop>();
        if (root.TryGetProperty("hops", out var hops))
            foreach (var hop in hops.EnumerateArray().Take(192))
            {
                int ttl = hop.TryGetProperty("probe_ttl", out var t) ? t.GetInt32() : 0;
                string? ip = hop.TryGetProperty("addr", out var a) ? a.GetString() : null;
                double? rtt = hop.TryGetProperty("rtt", out var r) && r.TryGetDouble(out var d) && d >= 0 && double.IsFinite(d) ? d : null;
                if (ttl < 1 || ttl > maxHops || !IPAddress.TryParse(ip, out _)) continue;
                result.Add(new(ttl, ip, rtt, ip == address ? "Success" : "TtlExpired"));
            }
        return Task.FromResult<IReadOnlyList<RouteHop>>(result);
    }
}

/// <summary>Supplementary TCP connect timing. Never mixed into ICMP physical constraints.</summary>
public sealed class TcpLatencyProbe(ProbeBudget budget)
{
    public async Task<SupplementalObservation> MeasureAsync(Landmark landmark, int port = 443,
        int samples = 3, int timeoutMs = 1000, CancellationToken ct = default)
    {
        if (!landmark.TcpProbeAllowed || port is < 1 or > 65535 || samples is < 1 or > 20 || timeoutMs is < 100 or > 10000 ||
            !IPAddress.TryParse(landmark.Address, out var publicIp) || !EmbeddedCatalog.IsPublic(publicIp))
            throw new ArgumentException("TCP probing requires explicit permission in the landmark catalog.");
        var results = new List<ProbeSample>();
        var ip = IPAddress.Parse(landmark.Address);
        for (int i = 0; i < samples; i++)
        {
            try { await budget.AcquireAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ProbeBudgetExceededException) { break; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var clock = Stopwatch.StartNew();
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, port), timeout.Token).ConfigureAwait(false);
                results.Add(new(clock.Elapsed.TotalMilliseconds, "Success") { TimingMethod = "tcp-connect-upper-v1" });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { results.Add(new(null, "TimedOut")); }
            catch (SocketException ex) { results.Add(new(null, ex.SocketErrorCode.ToString())); }
        }
        return new(landmark.Id, "tcp-connect", RobustStatistics.Summarize(results), DateTimeOffset.UtcNow);
    }
}
