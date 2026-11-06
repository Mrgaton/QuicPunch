using System.Diagnostics;
using System.Net;
using QuicPunch;
using QuicPunch.Helpers;
using QP = QuicPunch.QuicPunch;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunchTests.Tests;

internal static class ConnectionRecoveryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task<QP> Node(bool wan = false)
    {
        var node = new QP(appDataPath: Path.Combine(Path.GetTempPath(), "qp-recovery-" + Guid.NewGuid().ToString("N")), listeningPort: 0);
        node.WanEnabled = wan;
        node.EnableUpnp = false;
        node.StunServerEndpoints = [];
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        node.Discovery.LanDiscoveryPort = 0;
        node.PeerTransport = new() { AutoConnectDiscoveredPeers = false };
        node.ReputationFilter?.Dispose();
        node.ReputationFilter = null;
        await node.StartAsync();
        await node.Mailbox.StopAsync();
        node.CurrentPeer.Addresses = [IPAddress.Loopback];
        node.CurrentPeer.PortArray = [(ushort)(wan ? node.LocalBoundPort : 41500)];
        return node;
    }

    private static PeerInfo MailboxPeer(QP receiver, QP sender, string nostrKey)
    {
        receiver.TrustPeer(sender.CurrentPeer.CertHash);
        sender.CurrentPeer.NostrPubKey = nostrKey;
        Check(receiver.PeerStore!.AddOrUpdate(sender.CurrentPeer, autoConnect: false, save: true, nostrPubKey: nostrKey), "fixture saves peer with registered mailbox key");
        return receiver.ResolveOrCreatePeerByNostrPubKey(nostrKey, "Mailbox sender");
    }

    public static async Task RunAsync()
    {
        await TestMailboxBeforeHello();
        await TestHelloBeforeMailbox();
        await TestCallbackFailureDoesNotDisposePeer();
        await TestCancelledHelloWait();
        for (int i = 0; i < 3; i++) await TestSimultaneousQuicStartup(i);
    }

    private static async Task TestMailboxBeforeHello()
    {
        await using var a = await Node();
        await using var b = await Node();
        string nostrKey = new('a', 64);
        var placeholder = MailboxPeer(a, b, nostrKey);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 41502);
        Check(!placeholder.HasCertificate && placeholder.Id == b.CurrentPeer.Id, "saved mailbox sender initially has only a certificate hash");

        var id = Guid.NewGuid();
        var pending = a.Manager.WaitForDecisionAsync(new HandshakeRequest(id, Guid.Empty, endpoint, b.CurrentPeer.Id, b.CurrentPeer.CertHash),
            TimeSpan.FromSeconds(5), false, CancellationToken.None);
        var accept = b.GenerateHandshakePayload(HandShakeType.Accept, 41602, Guid.Empty, id);
        await a.ProcessIncomingPacketAsync(accept, endpoint, QP.TransportType.Wan);
        Check(!pending.IsCompleted, "handshake before certificate is ignored without throwing or accepting it");

        byte[] hello = b.GenerateHelloPayload(MessageType.Interrogation, true);
        var tampered = (byte[])hello.Clone();
        tampered[^1] ^= 1;
        await a.ProcessIncomingPacketAsync(tampered, endpoint, QP.TransportType.Wan);
        Check(ReferenceEquals(a.AvailablePeers[placeholder.Id], placeholder), "invalid Hello cannot replace the mailbox placeholder");

        int availableEvents = 0;
        a.OnPeerAvailable += _ => availableEvents++;
        await a.ProcessIncomingPacketAsync(hello, endpoint, QP.TransportType.Wan);
        var authenticated = a.AvailablePeers[b.CurrentPeer.Id];
        Check(authenticated.HasCertificate && authenticated.IsSessionReady && endpoint.Equals(authenticated.ActiveEndPoint),
            "first valid Hello immediately replaces placeholder with authenticated session");
        Check(authenticated.NostrPubKey == nostrKey && a.IsTrustedPeer(authenticated), "promotion preserves mailbox identity and explicit trust");
        Check(a.TryGetPeerByCertHash(b.CurrentPeer.CertHash, out var indexed) && ReferenceEquals(indexed, authenticated), "certificate index points to authenticated replacement");
        Check(ReferenceEquals(a.ResolveOrCreatePeerByNostrPubKey(nostrKey), authenticated), "later mailbox messages resolve the authenticated peer");
        await a.ProcessIncomingPacketAsync(accept, endpoint, QP.TransportType.Wan);
        Check((await pending.WaitAsync(TimeSpan.FromSeconds(1))).Accepted, "same negotiation accepts signed response immediately after Hello");
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(MessageType.Interrogation, true), endpoint, QP.TransportType.Wan);
        Check(availableEvents == 1 && ReferenceEquals(a.AvailablePeers[b.CurrentPeer.Id], authenticated), "subsequent Hello reuses authenticated peer without duplicate availability event");
    }

    private static async Task TestHelloBeforeMailbox()
    {
        await using var a = await Node();
        await using var b = await Node();
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(MessageType.Interrogation, true), new IPEndPoint(IPAddress.Loopback, 41702), QP.TransportType.Wan);
        var authenticated = a.AvailablePeers[b.CurrentPeer.Id];
        var mailbox = MailboxPeer(a, b, new string('b', 64));
        Check(ReferenceEquals(mailbox, authenticated) && mailbox.HasCertificate, "Hello before mailbox returns canonical peer instead of detached placeholder");
    }

    private static async Task TestCallbackFailureDoesNotDisposePeer()
    {
        await using var a = await Node();
        await using var b = await Node();
        a.OnPeerAvailable += _ => throw new InvalidOperationException("application callback failure");
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(MessageType.Interrogation, true), new IPEndPoint(IPAddress.Loopback, 41802), QP.TransportType.Wan);
        var peer = a.AvailablePeers[b.CurrentPeer.Id];
        byte[] message = [1, 2, 3];
        Check(peer.IsSessionReady && peer.VerifySignature(message, Sha3Crypto.SignData(b.CertManager.Curve, message)),
            "application callback exception does not dispose admitted peer cryptography");
    }

    private static async Task TestSimultaneousQuicStartup(int iteration)
    {
        await using var a = await Node(wan: true);
        await using var b = await Node(wan: true);
        a.CurrentPeer.Addresses = [IPAddress.Loopback];
        b.CurrentPeer.Addresses = [IPAddress.Loopback];
        a.CurrentPeer.PortArray = [(ushort)a.LocalBoundPort];
        b.CurrentPeer.PortArray = [(ushort)b.LocalBoundPort];
        var peerB = MailboxPeer(a, b, new string('c', 64));
        var peerA = MailboxPeer(b, a, new string('d', 64));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var watch = Stopwatch.StartNew();
        var connectA = a.EnsureMultiplexerConnectedAsync(peerB, cancellationToken: deadline.Token);
        // Also exercise an incoming worker that wins before the other caller
        // enters outbound negotiation, instead of relying on scheduler timing.
        if (iteration == 1)
        {
            while (b.ActiveIncomingWorkerCount == 0 && !b.TryGetPeerMultiplexer(peerA.Id, out _))
                await Task.Delay(25, deadline.Token);
            Check(b.ActiveOutboundNegotiationCount == 0, "second caller starts after incoming connection preparation owns the attempt");
        }
        var muxes = await Task.WhenAll(connectA,
            b.EnsureMultiplexerConnectedAsync(peerA, cancellationToken: deadline.Token));
        Check(muxes.All(m => !m.IsDisposed) && muxes[0].Peer.HasCertificate && muxes[1].Peer.HasCertificate,
            $"simultaneous QUIC startup from mailbox placeholders succeeds on first attempt ({iteration + 1}, {watch.Elapsed.TotalSeconds:F2}s)");
        Check(ReferenceEquals(await a.EnsureMultiplexerConnectedAsync(peerB, cancellationToken: deadline.Token), muxes[0]),
            "stale placeholder reference reuses established multiplexer");
    }

    private static async Task TestCancelledHelloWait()
    {
        await using var a = await Node(wan: true);
        await using var b = await Node();
        using var blackhole = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        b.CurrentPeer.PortArray = [(ushort)((IPEndPoint)blackhole.Client.LocalEndPoint!).Port];
        var placeholder = MailboxPeer(a, b, new string('e', 64));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await a.EnsureMultiplexerConnectedAsync(placeholder, cancellationToken: cancellation.Token);
            throw new Exception("Missing Hello unexpectedly established a multiplexer.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!a.ActiveInterrogations.IsEmpty) await Task.Delay(25, deadline.Token);
        Check(a.ActiveOutboundNegotiationCount == 0 && !a.IsPeerMultiplexerActiveOrConnecting(placeholder.Id),
            "cancelling Hello wait cleans up interrogation and connection flight without starting a QUIC negotiation");
    }
}
