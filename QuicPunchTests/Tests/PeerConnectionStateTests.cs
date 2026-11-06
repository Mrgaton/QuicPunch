using System.Net;
using System.Diagnostics;
using System.Reflection;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.WebUi;
using System.Text.Json;

namespace QuicPunchTests.Tests;

internal static class PeerConnectionStateTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync()
    {
        var manager = new HandshakeManager();
        var request = new HandshakeRequest(Guid.NewGuid(), Guid.Empty, new IPEndPoint(IPAddress.Loopback, 12345));
        var timedOut = await manager.WaitForDecisionAsync(request, TimeSpan.FromMilliseconds(50), false, CancellationToken.None);
        Check(!timedOut.Accepted && timedOut.FailureReason == "timeout", "silence is a timeout, not a remote rejection");
        Check(manager.PendingDecisionsCount == 0, "timeout removes pending negotiation");
        using var cancelled = new CancellationTokenSource();
        var pending = manager.WaitForDecisionAsync(request with { Id = Guid.NewGuid() }, TimeSpan.FromSeconds(5), false, cancelled.Token);
        cancelled.Cancel();
        Check((await pending).FailureReason == "cancelled", "local cancellation is distinct from rejection");
        var rejectedRequest = request with { Id = Guid.NewGuid() };
        pending = manager.WaitForDecisionAsync(rejectedRequest, TimeSpan.FromSeconds(5), false, CancellationToken.None);
        manager.Reject(rejectedRequest.Id);
        Check((await pending).FailureReason == null, "explicit rejection is not reported as timeout");
        Check(manager.PendingDecisionsCount == 0, "all negotiations cleaned up");

        await using var a = await Node();
        await using var b = await Node();
        var epB = new IPEndPoint(IPAddress.Loopback, b.LocalBoundPort);
        var epA = new IPEndPoint(IPAddress.Loopback, a.LocalBoundPort);
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true), epB, QuicPunch.QuicPunch.TransportType.Wan);
        await b.ProcessIncomingPacketAsync(a.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true), epA, QuicPunch.QuicPunch.TransportType.Wan);
        var peer = a.AvailablePeers[b.CurrentPeer.Id];
        Check(peer.IsAvailableAndResponsive() && !a.TryGetPeerMultiplexer(peer.Id, out _), "authenticated UDP discovery is available without a QUIC connection");
        peer.LastPingResponseUtc = DateTime.UtcNow.AddMinutes(-5);
        peer.IsCertVerified = true;
        a.PeerStore!.AddOrUpdate(peer, autoConnect: false);
        var announced = DateTime.UtcNow.AddMinutes(-2);
        a.Discovery.RecordDiscoveredPeer(peer, b.GetWanToken(), announcedAtUtc: announced);
        using var uiCancellation = new CancellationTokenSource();
        using var voice = new VoiceCallHandler(a);
        var web = new WebUiServer(a, new ChatHandler(), null, voice, new SpeedTestHandler(),
            new AppPreferencesStore(Path.Combine(Path.GetTempPath(), "qp-state-ui-" + Guid.NewGuid().ToString("N"))), uiCancellation, port: 5970);
        try
        {
            web.Start();
            using var http = new HttpClient();
            using var status = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{web.Port}/api/status"));
            var root = status.RootElement;
            Check(root.GetProperty("connectedPeers").GetArrayLength() == 0, "status API never counts UDP peers as connected");
            var entry = root.GetProperty("peers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == peer.Id.ToString());
            Check(entry.GetProperty("unresponsiveSeconds").GetInt32() < 60, "recent authenticated discovery overrides stale transport ping age");
            Check(entry.GetProperty("connectionState").GetString() == "available", "status exposes discovery availability explicitly");
            Check(entry.GetProperty("telemetry").ValueKind == JsonValueKind.Null && entry.GetProperty("ping").ValueKind == JsonValueKind.Null,
                "discovery does not expose invented or cached QUIC RTT");
            var deadlineUtc = announced + NostrDiscovery.AnnouncementMaxAge;
            Check(entry.GetProperty("discoveryAvailableUntilUtc").GetDateTime() == deadlineUtc,
                "active peer exposes the signed announcement expiry without claiming a QUIC connection");
            var saved = root.GetProperty("savedPeers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == peer.Id.ToString());
            Check(saved.GetProperty("discoveryAvailableUntilUtc").GetDateTime() == deadlineUtc,
                "saved contacts also expose discovery availability");
            var discovered = a.Discovery.DiscoveredPeers[Convert.ToHexString(peer.CertHash)];
            discovered.LastNostrAnnouncementUtc = DateTime.UtcNow.AddMinutes(-4);
            using var expiredStatus = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{web.Port}/api/status"));
            var expired = expiredStatus.RootElement.GetProperty("savedPeers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == peer.Id.ToString());
            Check(expired.GetProperty("discoveryAvailableUntilUtc").GetDateTime() < DateTime.UtcNow,
                "old announcements keep their expired deadline instead of becoming fresh when status is read");
        }
        finally { web.Stop(); }

        // Age the availability clock so the running maintenance loop must send a signed challenge.
        var old = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 25);
        typeof(PeerInfo).GetProperty(nameof(PeerInfo.LastActivityTimestampMonotonic))!.SetValue(peer, old);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (peer.LastActivityTimestampMonotonic == old)
            await Task.Delay(50, deadline.Token);
        Check(peer.LastActivityTimestampMonotonic > old, "signed Hello keepalive refreshes availability over real UDP sockets");
        Check(!a.TryGetPeerMultiplexer(peer.Id, out _), "keepalive does not turn discovery into a QUIC connection");
    }

    private static async Task<QuicPunch.QuicPunch> Node()
    {
        var qp = new QuicPunch.QuicPunch(appDataPath: Path.Combine(Path.GetTempPath(), "qp-state-" + Guid.NewGuid().ToString("N")), listeningPort: 0) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
        qp.EnableUpnp = false;
        qp.StunServerEndpoints = [];
        qp.Discovery.NostrRelays = [];
        qp.PeerTransport = new() { AutoConnectDiscoveredPeers = false };
        qp.ReputationFilter?.Dispose();
        qp.ReputationFilter = null;
        await qp.StartAsync();
        return qp;
    }
}
