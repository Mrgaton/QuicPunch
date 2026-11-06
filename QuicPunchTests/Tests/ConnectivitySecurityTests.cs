using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Security;
using QP = QuicPunch.QuicPunch;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunchTests.Tests;

internal static class ConnectivitySecurityTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task<QP> Node(string name)
    {
        var node = new QP(appDataPath: Path.Combine(Path.GetTempPath(), "qp-connectivity-" + name + Guid.NewGuid().ToString("N"))) { PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
        node.WanEnabled = false;
        node.EnableUpnp = false;
        node.StunServerEndpoints = [];
        node.Discovery.NostrRelays = [];
        node.ReputationFilter?.Dispose();
        node.ReputationFilter = null;
        await node.StartAsync();
        return node;
    }

    private static async Task<PeerInfo> Discover(QP receiver, QP sender, IPEndPoint endpoint)
    {
        await receiver.ProcessIncomingPacketAsync(sender.GenerateHelloPayload(MessageType.Interrogation, true), endpoint, QP.TransportType.Wan);
        return receiver.AvailablePeers[sender.CurrentPeer.Id];
    }

    public static async Task RunAsync()
    {
        await using var a = await Node("a");
        await using var b = await Node("b");
        await using var c = await Node("c");
        var epB = new IPEndPoint(IPAddress.Loopback, 41002);
        var epC = new IPEndPoint(IPAddress.Loopback, 41003);
        var attackerEp = new IPEndPoint(IPAddress.Loopback, 49999);
        var peerB = await Discover(a, b, epB);
        var peerC = await Discover(a, c, epC);
        a.TrustPeer(peerB.CertHash);
        Check(!a.IsTrustedPeer(peerC), "attacking identity is discovered but untrusted");

        var localApproval = new TaskCompletionSource<HandshakeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<HandshakeRequest, CancellationToken, Task<HandshakeDecision>> localHandler = (_, ct) => localApproval.Task.WaitAsync(ct);
        a.Manager.HandshakeRequested += localHandler;
        var localId = Guid.NewGuid();
        var localProtocol = Guid.NewGuid();
        var localPending = a.Manager.WaitForDecisionAsync(new HandshakeRequest(localId, localProtocol, epB, peerB.Id, peerB.CertHash),
            TimeSpan.FromSeconds(10), true, CancellationToken.None);
        await a.ProcessIncomingPacketAsync(b.GenerateHandshakePayload(HandShakeType.Accept, 45678, localProtocol, localId), epB, QP.TransportType.Wan);
        Check(!localPending.IsCompleted, "remote response cannot resolve an incoming local approval");
        localApproval.SetResult(new HandshakeDecision(false, null, null));
        Check(!(await localPending).Accepted, "local approval remains under application control");
        a.Manager.HandshakeRequested -= localHandler;

        using (var cancelIdentityTest = new CancellationTokenSource())
        {
            var identityId = Guid.NewGuid();
            var identityProtocol = Guid.NewGuid();
            var identityPending = a.Manager.WaitForDecisionAsync(new HandshakeRequest(identityId, identityProtocol, epB, peerB.Id, peerC.CertHash),
                TimeSpan.FromSeconds(10), false, cancelIdentityTest.Token);
            await a.ProcessIncomingPacketAsync(b.GenerateHandshakePayload(HandShakeType.Accept, 45678, identityProtocol, identityId), epB, QP.TransportType.Wan);
            Check(!identityPending.IsCompleted, "response must also match the pinned certificate hash");
            cancelIdentityTest.Cancel();
            Check(!(await identityPending).Accepted, "cancellation removes the bound pending decision");
        }

        foreach (var response in new[] { HandShakeType.Accept, HandShakeType.Decline, HandShakeType.Unsupported })
        {
            var id = Guid.NewGuid();
            var protocol = Guid.NewGuid();
            var pending = a.Manager.WaitForDecisionAsync(new HandshakeRequest(id, protocol, epB, peerB.Id, peerB.CertHash),
                TimeSpan.FromSeconds(10), false, CancellationToken.None);
            await a.ProcessIncomingPacketAsync(c.GenerateHandshakePayload(response, 45678, protocol, id), epC, QP.TransportType.Wan);
            Check(!pending.IsCompleted, response + " from another signer cannot resolve B's negotiation");
            await a.ProcessIncomingPacketAsync(b.GenerateHandshakePayload(response, 45678, Guid.NewGuid(), id), epB, QP.TransportType.Wan);
            Check(!pending.IsCompleted, response + " for another protocol is rejected");
            byte[] valid = b.GenerateHandshakePayload(response, 45678, protocol, id);
            await a.ProcessIncomingPacketAsync(valid, epB, QP.TransportType.Tor);
            Check(!pending.IsCompleted, response + " on another transport is rejected");
            await a.ProcessIncomingPacketAsync(valid, attackerEp, QP.TransportType.Wan);
            Check(!pending.IsCompleted && epB.Equals(peerB.ActiveEndPoint), "replayed response cannot select a new control address");
            await a.ProcessIncomingPacketAsync(valid, epB, QP.TransportType.Wan);
            var decision = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(decision.Accepted == (response == HandShakeType.Accept), "matching " + response + " resolves the negotiation");
            await a.ProcessIncomingPacketAsync(valid, attackerEp, QP.TransportType.Wan);
            Check(epB.Equals(peerB.ActiveEndPoint) && peerB.ActiveTransport == QP.TransportType.Wan, "completed response replay leaves routing unchanged");
        }

        var timedId = Guid.NewGuid();
        var timedProtocol = Guid.NewGuid();
        var expired = a.Manager.WaitForDecisionAsync(new HandshakeRequest(timedId, timedProtocol, epB, peerB.Id, peerB.CertHash),
            TimeSpan.FromMilliseconds(20), false, CancellationToken.None);
        Check(!(await expired).Accepted, "pending negotiation expires");
        await a.ProcessIncomingPacketAsync(b.GenerateHandshakePayload(HandShakeType.Accept, 45678, timedProtocol, timedId), attackerEp, QP.TransportType.Wan);
        Check(a.Manager.PendingDecisionsCount == 0 && epB.Equals(peerB.ActiveEndPoint), "late response cannot resurrect or reroute a negotiation");

        var requestId = Guid.NewGuid();
        var requestProtocol = Guid.NewGuid(); // unsupported: no QUIC socket is needed
        byte[] request = b.GenerateHandshakePayload(HandShakeType.Request, 45678, requestProtocol, requestId);
        await a.ProcessIncomingPacketAsync(request, attackerEp, QP.TransportType.Wan);
        Check(!a.IncomingHandshakeSessions.ContainsKey(requestId), "request replay from another endpoint creates no session");
        await a.ProcessIncomingPacketAsync(request, epB, QP.TransportType.Wan);
        var session = a.IncomingHandshakeSessions[requestId];
        await session.ResponsePayloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await a.ProcessIncomingPacketAsync(request, epB, QP.TransportType.Wan);
        Check(ReferenceEquals(session, a.IncomingHandshakeSessions[requestId]), "legitimate request retransmission remains idempotent");
        Check(a.GetOrAddIncomingHandshakeSession(requestId, peerC.Id, requestProtocol) == null
            && a.GetOrAddIncomingHandshakeSession(requestId, peerB.Id, Guid.NewGuid()) == null
            && a.GetOrAddIncomingHandshakeSession(requestId, peerB.Id, requestProtocol, QP.TransportType.Tor) == null,
            "incoming session cannot be reused by a different identity, protocol or transport");

        var staleId = Guid.NewGuid();
        byte[] stale = b.GenerateHandshakePayload(HandShakeType.Request, 45678, requestProtocol, staleId);
        int timestampOffset = QP.MagicHeader.Length + 1 + 16 + 1 + 2 + 16 + 16;
        BinaryPrimitives.WriteInt64LittleEndian(stale.AsSpan(timestampOffset), PreciseTime.GetCorrectTime().Ticks - TimeSpan.FromMinutes(1).Ticks);
        b.CertManager.Curve.SignData(stale.AsSpan(0, stale.Length - CertManager.SignatureLength), stale.AsSpan(stale.Length - CertManager.SignatureLength), HashAlgorithmName.SHA3_256);
        await a.ProcessIncomingPacketAsync(stale, epB, QP.TransportType.Wan);
        Check(!a.IncomingHandshakeSessions.ContainsKey(staleId), "correctly signed expired request is rejected");

        peerB.LastPingResponseUtc = DateTime.UtcNow.AddMinutes(-10);
        var lastPing = peerB.LastPingResponseUtc;
        var lastTimestamp = peerB.LastSeenPingTimestamp;
        var lastSeen = peerB.LastSeen;
        var lastActivity = peerB.LastActivityTimestampMonotonic;
        var latency = peerB.Ping;
        foreach (var source in new[] { attackerEp, epB })
            await a.ProcessIncomingPacketAsync(b.BuildPingPacket(PreciseTime.GetCorrectTime().Ticks - 10000, true), source, QP.TransportType.Wan);
        Check(peerB.LastPingResponseUtc == lastPing && peerB.LastSeenPingTimestamp == lastTimestamp
            && peerB.LastSeen == lastSeen && peerB.LastActivityTimestampMonotonic == lastActivity && peerB.Ping == latency,
            "unsigned pongs change neither liveness nor latency, even from the expected address");

        var migrated = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 54321);
        QuicPunchConnection.UpdateQuicEndPoint(peerB, migrated);
        Check(peerB.QuicEndPoint!.Equals(migrated) && epB.Equals(peerB.ActiveEndPoint), "QUIC migration preserves the control route");

        await TestPunchRejection(a, b, peerB);
        await TestPunchAndQuic(a, b, null, runQuic: false);
        await TestPunchAndQuic(a, b, RandomNumberGenerator.GetBytes(32), runQuic: true);
        Console.WriteLine("CONNECTIVITY SECURITY: all regression checks passed");
    }

    private static async Task TestPunchRejection(QP a, QP b, PeerInfo peerB)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var attacker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var otherEndpoint = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var target = (IPEndPoint)receiver.Client.LocalEndPoint!;
        var connection = Guid.NewGuid();
        byte[] password = RandomNumberGenerator.GetBytes(32);
        var pending = QuicPunchConnection.ReceiveHoleLoopAsync(receiver, connection, a.CurrentPeer, peerB,
            a.CertManager.Curve, password, timeout.Token);
        foreach (var type in new[] { MessageType.Ack, MessageType.FinalHandshake })
        {
            byte[] legacy = new byte[QP.MagicHeader.Length + 33];
            QP.MagicHeader.CopyTo(legacy, 0);
            legacy[QP.MagicHeader.Length] = (byte)type;
            connection.TryWriteBytes(legacy.AsSpan(QP.MagicHeader.Length + 1, 16));
            peerB.IdRaw.CopyTo(legacy, QP.MagicHeader.Length + 17);
            await attacker.SendAsync(legacy, target);
        }
        byte[] rawPunch = QuicPunchHolePacket.Create(b.CurrentPeer.Id, a.CurrentPeer.Id, b.CurrentPeer.CertHash,
            a.CurrentPeer.CertHash, b.CertManager.Curve, password).Encode();
        await attacker.SendAsync(rawPunch, target);
        await attacker.SendAsync(ConnectivityProbe.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty,
            b.CurrentPeer, a.CurrentPeer, b.CertManager.Curve, password), target);
        await attacker.SendAsync(ConnectivityProbe.Create(connection, Guid.NewGuid(), Guid.Empty,
            b.CurrentPeer, a.CurrentPeer, b.CertManager.Curve, null), target);
        await attacker.SendAsync(ConnectivityProbe.Create(connection, Guid.NewGuid(), Guid.Empty,
            b.CurrentPeer, a.CurrentPeer, b.CertManager.Curve, RandomNumberGenerator.GetBytes(32)), target);
        byte[] tampered = ConnectivityProbe.Create(connection, Guid.NewGuid(), Guid.Empty,
            b.CurrentPeer, a.CurrentPeer, b.CertManager.Curve, password);
        tampered[^1] ^= 1;
        await attacker.SendAsync(tampered, target);

        // A valid packet acts as a receive barrier: invalid packets ahead of it must
        // neither nominate an endpoint nor cause any authenticated response.
        byte[] initial = ConnectivityProbe.Create(connection, Guid.NewGuid(), Guid.Empty,
            b.CurrentPeer, a.CurrentPeer, b.CertManager.Curve, password);
        await attacker.SendAsync(initial, target);
        var challengePacket = await attacker.ReceiveAsync(timeout.Token);
        Check(ConnectivityProbe.TryVerify(challengePacket.Buffer, connection, b.CurrentPeer, a.CurrentPeer,
            password, out var challenge, out _), "receiver answers only the authenticated probe with a signed challenge");
        Check(!pending.IsCompleted, "legacy ACK/FinalHandshake, raw punch, wrong attempt, missing/wrong password and tampered probe cannot nominate a path");

        byte[] answer = ConnectivityProbe.Create(connection, Guid.NewGuid(), challenge,
            b.CurrentPeer, a.CurrentPeer, b.CertManager.Curve, password);
        await otherEndpoint.SendAsync(answer, target);
        await otherEndpoint.ReceiveAsync(timeout.Token);
        Check(!pending.IsCompleted, "signed challenge response replayed from another port cannot nominate that port");
        await attacker.SendAsync(answer, target);
        Check((await pending)!.Equals(attacker.Client.LocalEndPoint), "matching password and endpoint-bound signed response nominates the proven path");

        using var secondTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var second = QuicPunchConnection.ReceiveHoleLoopAsync(receiver, Guid.NewGuid(), a.CurrentPeer, peerB,
            a.CertManager.Curve, password, secondTimeout.Token);
        await attacker.SendAsync(answer, target);
        Check(await second == null, "captured response cannot nominate a later attempt");
    }

    private static async Task TestPunchAndQuic(QP a, QP b, byte[]? password, bool runQuic)
    {
        using var socketA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var socketB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var epA = (IPEndPoint)socketA.Client.LocalEndPoint!;
        var epB = (IPEndPoint)socketB.Client.LocalEndPoint!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var connection = Guid.NewGuid();
        var holeA = QuicPunchConnection.OpenPortCore(a.CurrentPeer, socketA, b.CurrentPeer,
            [new CandidateEndpoint(epB, CandidateType.Host)], (ushort)epB.Port, connection,
            timeout.Token, a.CertManager.Curve, password);
        var holeB = QuicPunchConnection.OpenPortCore(b.CurrentPeer, socketB, a.CurrentPeer,
            [new CandidateEndpoint(epA, CandidateType.Host)], (ushort)epA.Port, connection,
            timeout.Token, b.CertManager.Curve, password);
        var paths = await Task.WhenAll(holeA, holeB);
        Check(paths[0].Success && paths[1].Success && epB.Equals(paths[0].remoteEndpoint) && epA.Equals(paths[1].remoteEndpoint),
            "production two-way hole punch succeeds " + (password == null ? "without" : "with") + " a password");
        if (!runQuic) return;
        // The native listener takes over the nominated server port.
        socketA.Close();
        socketB.Close();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = QuicPunchConnection.TryRunServer(epA.Port, a.CertManager.PeerCertificate!, b.CurrentPeer.CertHash,
            null, timeout.Token, () => { ready.TrySetResult(); return Task.CompletedTask; });
        await ready.Task.WaitAsync(timeout.Token);
        var client = await QuicPunchConnection.TryRunClient(epA, b.CertManager.PeerCertificate!, a.CurrentPeer.CertHash,
            epB.Port, null, timeout.Token);
        var accepted = await server;
        Check(client.Connection != null && accepted.Connection != null, "production QUIC establishes mTLS with certificate pinning after secure hole punch");
        try
        {
            await client.Stream!.WriteAsync(new byte[] { 42 }, timeout.Token);
            byte[] data = new byte[1];
            await accepted.Stream!.ReadExactlyAsync(data, timeout.Token);
            Check(data[0] == 42, "authenticated QUIC stream transfers application data");
        }
        finally
        {
            await client.Stream!.DisposeAsync();
            await accepted.Stream!.DisposeAsync();
            await client.Connection!.DisposeAsync();
            await accepted.Connection!.DisposeAsync();
        }
    }
}
