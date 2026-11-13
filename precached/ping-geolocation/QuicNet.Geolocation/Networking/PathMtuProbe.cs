using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuicNet.Geolocation;

public interface IPayloadEchoTransport
{
    Task<string> SendAsync(IPAddress destination, int payloadBytes, bool dontFragment, int timeoutMs, CancellationToken ct);
}
public sealed class PayloadEchoTransport : IPayloadEchoTransport
{
    public async Task<string> SendAsync(IPAddress destination, int payloadBytes, bool dontFragment, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs),
                new byte[payloadBytes], new PingOptions(128, dontFragment), ct).ConfigureAwait(false);
            return reply.Status.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is PingException or SocketException or PlatformNotSupportedException or System.Security.SecurityException)
        { return "Error: " + ex.GetType().Name; }
    }
}

/// <summary>IPv4 PMTU bracket with small-packet controls. Timeout alone never proves fragmentation or VPN.</summary>
public sealed class PathMtuProbe(ProbeBudget budget, IPayloadEchoTransport? transport = null)
{
    private readonly IPayloadEchoTransport _transport = transport ?? new PayloadEchoTransport();
    public async Task<PathMtuResult> MeasureAsync(string destination, int? interfaceMtu = null,
        int timeoutMs = 1000, CancellationToken ct = default)
    {
        var ip = IPAddress.Parse(destination);
        if (!EmbeddedCatalog.IsPublic(ip)) throw new ArgumentException("PMTU requires a public catalog destination.");
        var attempts = new List<MtuAttempt>();
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return new(destination, "ipv6", "unsupported-portable-ipv6-df", null, null, attempts,
                "IPv6 requires an OS-specific no-fragment/Packet-Too-Big transport. IPv4 DF is not applied to IPv6.");
        int maximum = Math.Clamp((interfaceMtu ?? 1500) - 28, 68, 1472);
        async Task<string> Send(int size)
        {
            await budget.AcquireAsync(ct).ConfigureAwait(false);
            string status = await _transport.SendAsync(ip, size, true, timeoutMs, ct).ConfigureAwait(false);
            attempts.Add(new(size, status));
            return status;
        }
        int low = 0;
        int? failing = null;
        try
        {
            if (await Send(32).ConfigureAwait(false) != "Success")
                return Result("control-unreachable", "Small echo did not succeed; PMTU is indeterminate.");
            low = 32;
            int high = maximum;
            for (int iteration = 0; iteration < 12 && high > low; iteration++)
            {
                int size = iteration == 0 ? high : (low + high + 1) / 2;
                string status = await Send(size).ConfigureAwait(false);
                if (status == "Success") { low = size; continue; }
                if (status == "PacketTooBig") { failing = Math.Min(failing ?? int.MaxValue, size); high = size - 1; continue; }
                // A retry and a small control distinguish loss from a reported MTU error.
                string repeat = await Send(size).ConfigureAwait(false);
                if (repeat == "Success") { low = size; continue; }
                if (repeat == "PacketTooBig") { failing = Math.Min(failing ?? int.MaxValue, size); high = size - 1; continue; }
                string control = await Send(32).ConfigureAwait(false);
                return Result("indeterminate-filter-or-loss", control == "Success"
                    ? "Small packets work but a large packet timed out twice; no MTU upper bound can be inferred."
                    : "Small control also failed: congestion, filtering or route change.");
            }
            return Result(failing.HasValue ? "bounded" : "at-least-probed-size",
                "Bounds are IP bytes (payload + 28). A reduced MTU is not proof of VPN; success does not exclude a tunnel.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { return Result("interrupted", "Completed PMTU attempts retained."); }
        catch (ProbeBudgetExceededException)
        { return Result("probe-budget", "Completed PMTU attempts retained; launch budget exhausted."); }
        PathMtuResult Result(string status, string reason) => new(destination, "ipv4", status,
            low > 0 ? low + 28 : null, failing.HasValue ? failing.Value + 27 : null, attempts.ToArray(), reason);
    }
}
