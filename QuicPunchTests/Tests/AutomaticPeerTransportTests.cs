using System.Diagnostics;
using System.Net;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Multiplexer;
using static QuicPunch.QuicPunchStructures;
using QP = QuicPunch.QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicPunchTests.Tests;

internal static class AutomaticPeerTransportTests
{
    private static readonly Guid Voice = new("00000000-0000-0000-0000-000000000003");

    private sealed class Receiver(Guid protocol) : QP.IProtocolHandler
    {
        public Guid ProtocolId => protocol;
        public string ProtocolName => "Transport policy test";
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
        {
            byte[] value = new byte[1];
            await stream.ReadExactlyAsync(value, ct);
            if (value[0] != 42) throw new Exception("Incorrect application data.");
            Received.TrySetResult();
            try { await stream.ReadExactlyAsync(value, ct); }
            catch (EndOfStreamException) { }
            catch (ObjectDisposedException) { }
        }
        public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine($"[PASS] {description}");
    }

    private static async Task Wait(Func<bool> condition, string description, int seconds = 15)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { throw new Exception($"Timed out: {description}"); }
    }

    private static async Task<QP> Node(string path, PeerTransportOptions? options = null)
    {
        var node = new QP(appDataPath: path) { EnableUpnp = false };
        if (options != null) node.PeerTransport = options;
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        node.Discovery.WanNostrDiscoveryEnabled = false;
        node.Discovery.TorNostrDiscoveryEnabled = false;
        node.Discovery.LanDiscoveryPort = 0;
        node.ReputationFilter?.Dispose();
        node.ReputationFilter = null;
        await node.StartAsync();
        await node.Mailbox.StopAsync();
        node.CurrentPeer.Addresses = [IPAddress.Loopback];
        node.CurrentPeer.PortArray = [(ushort)node.LocalBoundPort];
        return node;
    }

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-automatic-transport-" + Guid.NewGuid().ToString("N"));
        try
        {
            await TestWarmTransportAndPermissions(root);
            await TestSavedContactRemainsAvailable(root);
            await TestLimitsAndRetry(root);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task TestWarmTransportAndPermissions(string root)
    {
        await using var a = await Node(Path.Combine(root, "a"));
        await using var b = await Node(Path.Combine(root, "b"));
        var receiver = new Receiver(Voice);
        a.RegisterProtocol(new Receiver(Voice));
        b.RegisterProtocol(receiver);
        int petitions = 0;
        bool approve = false;
        b.Manager.HandshakeRequested += (request, _) =>
        {
            Check(request.ProtocolId == Voice, "transport never raises application approval");
            Interlocked.Increment(ref petitions);
            return Task.FromResult(new HandshakeDecision(approve, 0, CancellationToken.None));
        };

        await a.FindPeerFromInvitationAsync(b.GetWanToken());
        await Wait(() => a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var am) && am != null && !am.IsDisposed &&
            b.TryGetPeerMultiplexer(a.CurrentPeer.Id, out var bm) && bm != null && !bm.IsDisposed, "automatic native QUIC without trust");
        a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var mux);
        Check(!a.IsTrustedPeer(b.CurrentPeer.CertHash) && !b.IsTrustedPeer(a.CurrentPeer.CertHash) && petitions == 0,
            "both nodes automatically connect with no trust or application approvals");
        Check(a.ActiveProtocolSessionCount == 0 && b.ActiveProtocolSessionCount == 0, "warm transport opens no application sessions");
        int negotiations = a.IncomingHandshakeSessions.Count + b.IncomingHandshakeSessions.Count;

        await ExpectDenied(() => mux!.OpenVirtualSessionAsync(Voice), "untrusted local caller cannot use the public multiplexer to open applications");
        a.TrustPeer(b.CurrentPeer.CertHash);
        await ExpectDenied(() => mux!.OpenVirtualSessionAsync(Voice), "untrusted remote identity is declined before user approval");
        Check(petitions == 0, "untrusted protocol requests never reach the approval callback");
        b.TrustPeer(a.CurrentPeer.CertHash);
        await ExpectDenied(() => mux!.OpenVirtualSessionAsync(Voice), "trust alone does not grant voice permission");
        Check(petitions == 1, "voice requires its own decision");
        approve = true;
        await using var connection = await mux!.OpenVirtualSessionAsync(Voice);
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
        await stream.WriteAsync(new byte[] { 42 });
        await stream.FlushAsync();
        await receiver.Received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var same) && ReferenceEquals(mux, same) &&
            negotiations == a.IncomingHandshakeSessions.Count + b.IncomingHandshakeSessions.Count,
            "authorized application data reuses the warm QUIC connection without another negotiation");
        a.PeerTransport = a.PeerTransport with { IdleTimeout = TimeSpan.FromMilliseconds(500) };
        b.PeerTransport = b.PeerTransport with { IdleTimeout = TimeSpan.FromMinutes(5) };
        await Task.Delay(800);
        Check(!mux.IsDisposed, "active application sessions survive transport idle cleanup");
        a.PeerTransport = a.PeerTransport with { IdleTimeout = TimeSpan.FromMinutes(5) };
        a.UntrustPeer(b.CurrentPeer.CertHash);
        await Wait(() => !mux.HasApplicationSessions, "revoked sessions close", 3);
        Check(!mux.IsDisposed, "revoking trust closes application sessions while retaining the transport");
        a.PeerTransport = a.PeerTransport with { IdleTimeout = TimeSpan.FromMilliseconds(500) };
        await Wait(() => mux.IsDisposed, "idle transport closes", 4);
        await Task.Delay(750);
        Check(!a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var reopened) || reopened == null || reopened.IsDisposed,
            "idle transport stays parked instead of entering a reconnect loop");
        Check(!b.TryGetPeerMultiplexer(a.CurrentPeer.Id, out var remoteReopened) || remoteReopened == null || remoteReopened.IsDisposed,
            "remote idle notification parks the other endpoint even with a different idle timeout");
        await a.StopAsync();
        await b.StopAsync();
        Check(a.ReservedPeerTransportCount == 0 && b.ReservedPeerTransportCount == 0, "stop releases all transport preparation slots");
    }

    private static async Task TestSavedContactRemainsAvailable(string root)
    {
        await using var a = await Node(Path.Combine(root, "saved-a"));
        await using var b = await Node(Path.Combine(root, "saved-b"));
        await a.FindPeerFromInvitationAsync(b.GetWanToken());
        await Wait(() => a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var m) && m is { IsDisposed: false }, "invitation establishes a bidirectional transport");
        a.TrustPeer(b.CurrentPeer.CertHash); b.TrustPeer(a.CurrentPeer.CertHash);
        Check(a.AvailablePeers.TryGetValue(b.CurrentPeer.Id, out var remote) && a.SavePeer(remote), "save a connected contact");
        a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var mux);
        a.PeerTransport = a.PeerTransport with { IdleTimeout = TimeSpan.FromMilliseconds(250) };
        await Task.Delay(900);
        Check(mux is { IsDisposed: false }, "saved automatic contacts remain available while idle");
        Check(a.PeerStore!.TryGet(b.CurrentPeer.CertHash, out var history) && history!.LastConnectedUtc != null && history.LastKnownIp != null, "saving an already-connected contact records its verified history");
        b.PeerTransport = b.PeerTransport with { IdleTimeout = TimeSpan.FromMilliseconds(250) };
        await Wait(() => mux!.IsDisposed, "remote idle policy closes the old transport", 4);
        b.PeerTransport = b.PeerTransport with { IdleTimeout = TimeSpan.FromMinutes(5) };
        await Wait(() => a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var reopened) && reopened is { IsDisposed: false } && !ReferenceEquals(reopened, mux), "saved automatic contact reconnects after a remote idle close", 6);
        a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var beforeStop);
        await a.StopWanAsync();
        Check(!a.IsWanStarted && beforeStop is { IsDisposed: true }, "stopping WAN closes its established native transport");
        await Task.Delay(700);
        Check(!a.TryGetPeerMultiplexer(b.CurrentPeer.Id, out var afterStop) || afterStop == null || afterStop.IsDisposed, "saved automatic contact cannot reopen WAN while the service is stopped");
    }

    private static async Task ExpectDenied(Func<Task<QuicConnection>> attempt, string description)
    {
        try { await using var unexpected = await attempt(); }
        catch (InvalidOperationException) { Check(true, description); return; }
        throw new Exception(description);
    }

    private static async Task TestLimitsAndRetry(string root)
    {
        await using var node = await Node(Path.Combine(root, "limits"), new PeerTransportOptions
        {
            AutoConnectDiscoveredPeers = false,
            MaxConnectedPeers = 1,
            MaxConcurrentAttempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(200),
            InitialRetryDelay = TimeSpan.FromMilliseconds(600),
            MaxRetryDelay = TimeSpan.FromSeconds(2)
        });
        long generation = node.LifecycleGeneration;
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        Check(node.TryReservePeerTransport(first, generation) && node.TryReservePeerTransport(first, generation) &&
            !node.TryReservePeerTransport(second, generation), "capacity counts distinct peers and shares glare preparation slots");
        node.ReleasePeerTransport(first, generation);
        Check(!node.TryReservePeerTransport(second, generation), "one completed owner cannot release another owner's slot");
        node.ReleasePeerTransport(first, generation);
        Check(node.TryReservePeerTransport(second, generation), "failed or cancelled preparation frees capacity");
        node.ReleasePeerTransport(second, generation);

        await using var remote = await Node(Path.Combine(root, "unreachable"), new PeerTransportOptions { AutoConnectDiscoveredPeers = false });
        using var blackhole = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await node.ProcessIncomingPacketAsync(remote.GenerateHelloPayload(MessageType.Interrogation, true),
            (IPEndPoint)blackhole.Client.LocalEndPoint!, QP.TransportType.Wan);
        var peer = node.AvailablePeers[remote.CurrentPeer.Id];
        Check(node.TryReservePeerTransport(first, generation), "fixture fills incoming transport capacity");
        Guid incoming = Guid.NewGuid();
        await node.ProcessIncomingPacketAsync(remote.GenerateHandshakePayload(HandShakeType.Request, 0, Guid.Empty, incoming),
            (IPEndPoint)blackhole.Client.LocalEndPoint!, QP.TransportType.Wan);
        var rejected = await node.IncomingHandshakeSessions[incoming].ResponsePayloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(rejected[QP.MagicHeader.Length + 1 + 16] == (byte)HandShakeType.Decline && node.ActiveIncomingWorkerCount == 0,
            "authenticated incoming transport is declined at capacity without starting preparation");
        node.ReleasePeerTransport(first, generation);
        await Task.Delay(350);
        Check(peer.LastQuicConnectionAttemptUtc == null, "manual transport mode does not start automatic attempts");
        node.PeerTransport = node.PeerTransport with { AutoConnectDiscoveredPeers = true };
        await Wait(() => peer.LastQuicConnectionError != null, "first timed out attempt", 3);
        var firstAttempt = peer.LastQuicConnectionAttemptUtc;
        await Task.Delay(350);
        Check(peer.LastQuicConnectionAttemptUtc == firstAttempt && node.ReservedPeerTransportCount == 0,
            "failed automatic attempts release their slot and respect retry backoff");
        await Wait(() => peer.LastQuicConnectionAttemptUtc > firstAttempt, "automatic retry", 3);
        Check(!node.IsTrustedPeer(peer), "automatic retries never grant trust");
        await using var other = await Node(Path.Combine(root, "unreachable-2"), new PeerTransportOptions { AutoConnectDiscoveredPeers = false });
        using var otherBlackhole = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await node.ProcessIncomingPacketAsync(other.GenerateHelloPayload(MessageType.Interrogation, true),
            (IPEndPoint)otherBlackhole.Client.LocalEndPoint!, QP.TransportType.Wan);
        node.PeerTransport = node.PeerTransport with { MaxConnectedPeers = 2 };
        var secondPeer = node.AvailablePeers[other.CurrentPeer.Id];
        await Wait(() => secondPeer.LastQuicConnectionAttemptUtc != null, "second automatic peer attempt", 4);
        long observation = Stopwatch.GetTimestamp();
        bool bounded = true;
        while (Stopwatch.GetElapsedTime(observation) < TimeSpan.FromSeconds(2))
        {
            bounded &= node.ReservedPeerTransportCount <= 1;
            await Task.Delay(50);
        }
        Check(bounded, "scheduler respects concurrent attempt limit across multiple identities");
        await node.StopAsync();
        await node.StartAsync();
        Check(!node.TryReservePeerTransport(first, generation), "obsolete lifecycle cannot reserve transport after restart");
        Check(node.ReservedPeerTransportCount == 0, "restart clears preparation state");
    }
}
