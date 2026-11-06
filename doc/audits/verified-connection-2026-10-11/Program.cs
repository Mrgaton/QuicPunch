using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Multiplexer;
using static QuicPunch.QuicPunchStructures;
using QP = QuicPunch.QuicPunch;
using QConnection = QuicPunch.QuicConnection;
using Message = QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessage;
using MessageKind = QuicPunch.Multiplexer.QuicPeerMultiplexer.ControlMessageType;

// Diagnostic reproductions: exit 0 means the stated CURRENT defects were reproduced.
// These are intentionally not passing regression tests for a corrected implementation.
QuicPunchLog.EnableLogging = false;
QuicPunchLog.EnableErrorLogging = false;
QuicPunchLog.EnableFileLogging = false;
try
{
    if (args.Length == 0 || args.Contains("hello")) await Audit.HelloRouteReplay();
    if (args.Length == 0 || args.Contains("stun")) await Audit.StunInjection();
    if (args.Length == 0 || args.Contains("loss")) await Audit.PunchLoss();
    if (args.Length == 0 || args.Contains("sessions")) await Audit.SessionAdmission();
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

internal static class Audit
{
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }

    private static IPEndPoint Endpoint(UdpClient socket) => (IPEndPoint)socket.Client.LocalEndPoint!;
    private static UdpClient Socket() => new(new IPEndPoint(IPAddress.Loopback, 0));

    private static async Task<QP> Node(string name)
    {
        var node = new QP(appDataPath: Path.Combine(Path.GetTempPath(), "qp-verified-audit-" + name + Guid.NewGuid().ToString("N")))
        {
            WanEnabled = false,
            EnableUpnp = false,
            PeerTransport = new() { AutoConnectDiscoveredPeers = false }
        };
        node.StunServerEndpoints = [];
        node.Discovery.NostrRelays = [];
        node.Discovery.WanNostrDiscoveryEnabled = false;
        node.Discovery.TorNostrDiscoveryEnabled = false;
        node.ReputationFilter?.Dispose();
        node.ReputationFilter = null;
        await node.StartAsync();
        return node;
    }

    public static async Task HelloRouteReplay()
    {
        await using var a = await Node("receiver");
        await using var b = await Node("signer");
        var legitimate = new IPEndPoint(IPAddress.Loopback, 41002);
        var attacker = new IPEndPoint(IPAddress.Loopback, 49999);
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(MessageType.Interrogation, true), legitimate, QP.TransportType.Wan);
        var peer = a.AvailablePeers[b.CurrentPeer.Id];
        Require(legitimate.Equals(peer.ActiveEndPoint), "Initial discovery failed.");
        await Task.Delay(10);

        // Model an unchanged, freshly captured beacon, unseen by A, emitted by B.
        // No private key is used by the replaying party and no pending challenge exists at A.
        byte[] captured = b.GenerateHelloPayload(MessageType.Interrogation, true);
        byte[] tampered = captured.ToArray();
        tampered[^1] ^= 1;
        await a.ProcessIncomingPacketAsync(tampered, attacker, QP.TransportType.Wan);
        Require(legitimate.Equals(peer.ActiveEndPoint), "Negative control: invalid signature changed route.");
        await a.ProcessIncomingPacketAsync(captured, attacker, QP.TransportType.Wan);
        Require(attacker.Equals(peer.ActiveEndPoint), "Expected route replay was not reproduced.");
        await a.ProcessIncomingPacketAsync(captured, legitimate, QP.TransportType.Wan);
        Require(attacker.Equals(peer.ActiveEndPoint), "Original copy unexpectedly repaired the poisoned route.");

        // Even a freshly signed ACCEPT from the real endpoint is now rejected by routing checks.
        Guid id = Guid.NewGuid(), protocol = Guid.NewGuid();
        using var cancel = new CancellationTokenSource();
        var decision = a.Manager.WaitForDecisionAsync(new HandshakeRequest(id, protocol, legitimate, peer.Id, peer.CertHash),
            TimeSpan.FromSeconds(2), false, cancel.Token);
        await a.ProcessIncomingPacketAsync(b.GenerateHandshakePayload(HandShakeType.Accept, 42000, protocol, id), legitimate, QP.TransportType.Wan);
        Require(!decision.IsCompleted, "Legitimate ACCEPT unexpectedly completed after route poisoning.");
        cancel.Cancel();
        await decision;
        Console.WriteLine("REPRO HELLO: invalid signature rejected; unchanged fresh beacon changed 127.0.0.1:41002 -> 127.0.0.1:49999; original copy and legitimate ACCEPT rejected.");
    }

    private static byte[] StunResponse(byte[] tx, IPEndPoint mapped)
    {
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 0x2112A442);
        tx.CopyTo(packet, 8);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), 8);
        packet[25] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26), (ushort)mapped.Port);
        mapped.Address.GetAddressBytes().CopyTo(packet, 28);
        return packet;
    }

    public static async Task StunInjection()
    {
        using var client = Socket();
        using var server = Socket();
        using var unrelated = Socket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var legitimate = new IPEndPoint(IPAddress.Parse("9.9.9.9"), 54321);
        var gather = SimpleStunClient.GatherCandidatesAsync(client, (ushort)Endpoint(client).Port,
            [Endpoint(server)], TimeSpan.FromMilliseconds(600), deadline.Token);
        var request = await server.ReceiveAsync(deadline.Token);
        byte[] realTx = request.Buffer.AsSpan(8, 12).ToArray();
        byte[] wrongTx = realTx.ToArray();
        wrongTx[0] ^= 0xff;
        // Only the local receiver is contacted. These public IPs are inert values inside test packets.
        for (int i = 1; i <= 40; i++)
            await unrelated.SendAsync(StunResponse(wrongTx, new IPEndPoint(IPAddress.Parse("8.8.8.8"), 10000 + i)), Endpoint(client), deadline.Token);
        await Task.Delay(30, deadline.Token);
        await server.SendAsync(StunResponse(realTx, legitimate), Endpoint(client), deadline.Token);
        var candidates = await gather;
        int forged = candidates.Count(c => c.EndPoint.Address.Equals(IPAddress.Parse("8.8.8.8")));
        Require(forged == 40 && candidates.Any(c => c.EndPoint.Equals(legitimate)), "STUN setup did not receive all test responses.");

        await using var node = await Node("stun-serialization");
        byte[] handshake = node.GenerateHandshakePayload(HandShakeType.Request, (ushort)Endpoint(client).Port, Guid.Empty, Guid.NewGuid(), candidates);
        int offset = QP.MagicHeader.Length + 1 + 16 + 1 + 2 + 16 + 16 + 8;
        int count = handshake[offset++];
        var advertised = new List<IPEndPoint>();
        for (int i = 0; i < count; i++, offset += 11)
            advertised.Add(new IPEndPoint(new IPAddress(handshake.AsSpan(offset + 1, 4)), BinaryPrimitives.ReadUInt16LittleEndian(handshake.AsSpan(offset + 5, 2))));
        Require(count == 32 && !advertised.Contains(legitimate), "Valid STUN endpoint was not displaced from handshake.");
        Console.WriteLine($"REPRO STUN: {forged} replies from unrelated socket with WRONG transaction ID accepted; valid reply received, but excluded from {count} advertised candidates.");
    }

    private static X509Certificate2 Certificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Local audit", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    public static async Task PunchLoss()
    {
        // First check the exact same local path without dropping packets.
        var normal = await PunchPath(false);
        Require(normal.A && normal.B, "Loss-free negative control failed.");
        var loss = await PunchPath(true);
        Require(loss.A && !loss.B && loss.Dropped == 4 && loss.ForwardedAfterDrop > 0,
            $"Expected one-sided failure not reproduced: {loss}");
        Console.WriteLine($"REPRO LOSS: loss-free A/B=True/True; dropping only 4 terminal packets gives A/B={loss.A}/{loss.B}; {loss.ForwardedAfterDrop} later B packets delivered on restored path with no recovery.");
    }

    private static async Task<(bool A, bool B, int Dropped, int ForwardedAfterDrop)> PunchPath(bool loseFinalBurst)
    {
        using var certA = Certificate();
        using var certB = Certificate();
        using var keyA = certA.GetECDsaPrivateKey()!;
        using var keyB = certB.GetECDsaPrivateKey()!;
        using var a = Socket();
        using var b = Socket();
        using var relayA = Socket();
        using var relayB = Socket();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var peerA = new PeerInfo(certA, null) { ActiveEndPoint = Endpoint(relayB), Addresses = [], PortArray = [] };
        var peerB = new PeerInfo(certB, null) { ActiveEndPoint = Endpoint(relayA), Addresses = [], PortArray = [] };
        Guid connection = Guid.NewGuid();
        byte[]? initialAProbe = null;
        Guid bExpectedChallenge = Guid.Empty;
        int dropped = 0, forwardedAfterDrop = 0;
        bool firstAReplyForwarded = false;
        async Task RelayAtoB()
        {
            try
            {
                while (true)
                {
                    var packet = await relayA.ReceiveAsync(stop.Token);
                    Guid echo = new(packet.Buffer.AsSpan(36, 16));
                    // Deterministic scheduling: B's initial probe starts the challenge exchange.
                    if (echo == Guid.Empty && !firstAReplyForwarded) { initialAProbe = packet.Buffer; continue; }
                    if (loseFinalBurst && bExpectedChallenge != Guid.Empty && echo == bExpectedChallenge && dropped < 4)
                    { Interlocked.Increment(ref dropped); continue; }
                    await relayB.SendAsync(packet.Buffer, Endpoint(b), stop.Token);
                    if (!firstAReplyForwarded)
                    {
                        firstAReplyForwarded = true;
                        // The delayed initial probe follows the reply; this changes no signed bytes.
                        if (initialAProbe != null) await relayB.SendAsync(initialAProbe, Endpoint(b), stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        async Task RelayBtoA()
        {
            try
            {
                while (true)
                {
                    var packet = await relayB.ReceiveAsync(stop.Token);
                    if (new Guid(packet.Buffer.AsSpan(36, 16)) != Guid.Empty)
                        bExpectedChallenge = new Guid(packet.Buffer.AsSpan(20, 16));
                    if (Volatile.Read(ref dropped) == 4) Interlocked.Increment(ref forwardedAfterDrop);
                    await relayA.SendAsync(packet.Buffer, Endpoint(a), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        Task forwardA = RelayAtoB(), forwardB = RelayBtoA();
        var openA = QuicPunchConnection.OpenPortCore(peerA, a, peerB, null, 0, connection, stop.Token, keyA);
        var openB = QuicPunchConnection.OpenPortCore(peerB, b, peerA, null, 0, connection, stop.Token, keyB);
        var results = await Task.WhenAll(openA, openB);
        stop.Cancel();
        await Task.WhenAll(forwardA, forwardB);
        return (results[0].Success, results[1].Success, dropped, forwardedAfterDrop);
    }

    private sealed class Handler(Guid protocol) : QP.IProtocolHandler
    {
        public Guid ProtocolId => protocol;
        public string ProtocolName => "audit";
        public Task HandleAsync(QConnection connection, Stream stream, PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
        public Task DeniedAsync(PeerInfo peer, CancellationToken ct) => Task.CompletedTask;
    }

    public static async Task SessionAdmission()
    {
        using var cert = Certificate();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var alpn = new SslApplicationProtocol("audit-local");
        await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [alpn],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0, DefaultStreamErrorCode = 0,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = [alpn], ServerCertificate = cert }
            })
        }, lifetime.Token);
        var accept = listener.AcceptConnectionAsync(lifetime.Token);
        await using var client = await System.Net.Quic.QuicConnection.ConnectAsync(new QuicClientConnectionOptions
        {
            RemoteEndPoint = listener.LocalEndPoint,
            DefaultCloseErrorCode = 0, DefaultStreamErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost", ApplicationProtocols = [alpn],
                RemoteCertificateValidationCallback = (_, remote, _, _) => remote != null && remote.GetPublicKey().AsSpan().SequenceEqual(cert.GetPublicKey())
            }
        }, lifetime.Token);
        var server = QConnection.Wrap(await accept);
        Guid protocol = Guid.NewGuid();
        var handler = new Handler(protocol);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int decisions = 0;
        await using var mux = new QuicPeerMultiplexer(server, new PeerInfo(cert, null), true, _ => handler,
            _ => true, async (_, _, ct) => { Interlocked.Increment(ref decisions); return await release.Task.WaitAsync(ct); });
        await mux.StartAsync(lifetime.Token);
        await using var control = await client.OpenOutboundStreamAsync(System.Net.Quic.QuicStreamType.Bidirectional, lifetime.Token);
        const int total = 160;
        for (int i = 0; i < total; i++)
        {
            var msg = new Message { Type = MessageKind.SessionRequest, SessionShortId = (ushort)(2 * i + 1), SessionGuid = Guid.NewGuid(), ProtocolId = protocol };
            var frame = new byte[4 + Message.FixedHeaderSize];
            msg.Encode(frame.AsSpan(4), out int length);
            BinaryPrimitives.WriteInt32LittleEndian(frame, length);
            await control.WriteAsync(frame, lifetime.Token);
        }
        while (Volatile.Read(ref decisions) < total) await Task.Delay(10, lifetime.Token);
        release.SetResult(true);
        int accepted = 0;
        for (int i = 0; i < total; i++)
        {
            var prefix = new byte[4];
            await control.ReadExactlyAsync(prefix, lifetime.Token);
            var bytes = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
            await control.ReadExactlyAsync(bytes, lifetime.Token);
            if (Message.Decode(bytes).Accepted) accepted++;
        }
        var sessions = typeof(QuicPeerMultiplexer).GetField("_activeSessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mux)!;
        int active = (int)sessions.GetType().GetProperty("Count")!.GetValue(sessions)!;
        Require(accepted == total && active == total, "Expected concurrent admission bypass not reproduced.");
        Console.WriteLine($"REPRO SESSIONS: {decisions} concurrent approval callbacks; {accepted} ACCEPT responses and {active} allocated sessions despite limit 128 (real local QUIC).");
    }
}
