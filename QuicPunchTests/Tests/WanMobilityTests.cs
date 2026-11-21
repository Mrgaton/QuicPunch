using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using QuicPunch;
using QuicPunch.Discovery;
using QuicPunch.Helpers;
using QP = QuicPunch.QuicPunch;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunchTests.Tests;

internal static class WanMobilityTests
{
    public static async Task RunAsync()
    {
        TestExactPairsAndNat64();
        await TestProgressiveStun();
        string root = Path.Combine(Path.GetTempPath(), "qp-wan-mobility-" + Guid.NewGuid().ToString("N"));
        try { await TestAuthenticatedRouteChange(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void TestExactPairsAndNat64()
    {
        using var peer = new PeerInfo
        {
            Addresses = new[] { "93.184.216.31", "93.184.216.32", "93.184.216.33", "93.184.216.34" }
                .Select(IPAddress.Parse).ToArray(),
            PortArray = [49000],
            ConnectionFlags = new() { PortMode = PortMode.Single }
        };
        peer.WanCandidates = peer.Addresses.Select(ip => new IPEndPoint(ip, 49000)).ToArray();
        Check(QuicPunchConnection.BuildProbeTargets(peer, null, 0).ToHashSet().SetEquals(peer.WanCandidates),
            "four IPv4 candidates sharing one port are all tried in the first round");
        peer.PortArray = [15001, 45001, 49000, 62000];
        peer.ConnectionFlags.PortMode = PortMode.Multiple;
        peer.WanCandidates = peer.Addresses.Zip(peer.PortArray, (ip, port) => new IPEndPoint(ip, port)).ToArray();
        Check(QuicPunchConnection.BuildProbeTargets(peer, null, 0).ToHashSet().SetEquals(peer.WanCandidates),
            "four sparse per-IP ports stay four destinations");
        CandidateEndpoint[] data = [new(new IPEndPoint(peer.Addresses[0], 60444), CandidateType.ServerReflexive)];
        Check(QuicPunchConnection.BuildProbeTargets(peer, data, 48100, exactDataFirst: true).SequenceEqual([data[0].EndPoint]),
            "first data rounds use the data socket's exact candidate without guessing the control ports");

        var examples = new (string Prefix, string Mapped)[]
        {
            ("2001:db8::/32", "2001:db8:c000:221::"),
            ("2001:db8:100::/40", "2001:db8:1c0:2:21::"),
            ("2001:db8:122::/48", "2001:db8:122:c000:2:2100::"),
            ("2001:db8:122:300::/56", "2001:db8:122:3c0:0:221::"),
            ("2001:db8:122:344::/64", "2001:db8:122:344:c0:2:2100::"),
            ("2001:db8:122:344::/96", "2001:db8:122:344::192.0.2.33")
        };
        Check(examples.All(example => Nat64Prefix.Embed(IPNetwork.Parse(example.Prefix), IPAddress.Parse("192.0.2.33"))
            .Equals(IPAddress.Parse(example.Mapped))), "NAT64 synthesis matches all six RFC 6052 prefix layouts");
    }

    private static async Task TestProgressiveStun()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var firstServer = (IPEndPoint)first.Client.LocalEndPoint!;
        var secondServer = (IPEndPoint)second.Client.LocalEndPoint!;
        using var coordinator = new NatPinCoordinator([firstServer, secondServer]);
        coordinator.SetUdpClient(socket);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.MappingChanged += (_, map) => { if (map.HasMapping) published.TrySetResult(); };
        var burst = coordinator.ExecuteBurstAsync(deadline.Token);
        byte[] firstRequest = (await first.ReceiveAsync(deadline.Token)).Buffer;
        byte[] secondRequest = (await second.ReceiveAsync(deadline.Token)).Buffer;
        var firstMap = new IPEndPoint(IPAddress.Parse("93.184.216.31"), 49000);
        var secondMap = new IPEndPoint(IPAddress.Parse("93.184.216.32"), 45001);
        coordinator.TryProcessIncoming(StunAnswer(firstRequest, firstMap), firstServer);
        await published.Task.WaitAsync(deadline.Token);
        Check(!burst.IsCompleted && coordinator.CurrentMapping.AggregatedHits.ContainsKey(firstMap),
            "the first validated mapping can be announced during the initial burst");
        coordinator.TryProcessIncoming(StunAnswer(secondRequest, secondMap), secondServer);
        Check((await burst).AggregatedHits.Keys.ToHashSet().SetEquals([firstMap, secondMap]),
            "the completed burst retains both exact IP and port pairs");

        var departing = coordinator.ExecuteBurstAsync(deadline.Token);
        byte[] late = (await first.ReceiveAsync(deadline.Token)).Buffer;
        coordinator.InvalidateNetworkPath();
        Check(!coordinator.TryProcessIncoming(StunAnswer(late, firstMap), firstServer),
            "late responses from the old network cannot recreate discarded mappings");
        Check(!(await departing).HasMapping && !coordinator.CurrentMapping.HasMapping,
            "switching networks clears the previous burst");

        coordinator.SetServerEndpoints([firstServer]);
        var silentRebind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.MappingChanged += (_, map) =>
        {
            if (map.AggregatedHits.ContainsKey(secondMap))
                silentRebind.TrySetResult();
        };
        coordinator.Start(socket, deadline.Token);
        try
        {
            byte[] reference = (await first.ReceiveAsync(deadline.Token)).Buffer;
            long sampled = Stopwatch.GetTimestamp();
            Check(coordinator.TryProcessIncoming(StunAnswer(reference, firstMap), firstServer), "periodic sampler establishes a reference mapping");
            byte[] changed = (await first.ReceiveAsync(deadline.Token)).Buffer;
            Check(coordinator.TryProcessIncoming(StunAnswer(changed, secondMap), firstServer), "periodic sampler observes a changed mapping from the same server");
            await silentRebind.Task.WaitAsync(deadline.Token);
            Check(Stopwatch.GetElapsedTime(sampled) < TimeSpan.FromSeconds(2),
                "a silent remap is detected during fast sampling without an OS network callback");
        }
        finally { await coordinator.StopAsync(); }
    }

    private static byte[] StunAnswer(byte[] request, IPEndPoint mapped)
    {
        byte[] response = new byte[32];
        request.CopyTo(response, 0);
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 8);
        response[25] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(mapped.Port ^ 0x2112));
        byte[] address = mapped.Address.GetAddressBytes();
        for (int i = 0; i < 4; i++) response[28 + i] = (byte)(address[i] ^ response[4 + i]);
        return response;
    }

    private static async Task TestAuthenticatedRouteChange(string root)
    {
        await using var desktop = CreateNode(Path.Combine(root, "desktop"));
        await using var phone = CreateNode(Path.Combine(root, "phone"));
        using var oldRoute = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var oldEndPoint = (IPEndPoint)oldRoute.Client.LocalEndPoint!;
        await Task.WhenAll(desktop.StartAsync(deadline.Token), phone.StartAsync(deadline.Token));
        await Task.WhenAll(desktop.Mailbox.StopAsync(), phone.Mailbox.StopAsync());
        desktop.TrustPeer(phone.CurrentPeer.CertHash);
        phone.TrustPeer(desktop.CurrentPeer.CertHash);

        phone.CurrentPeer.Addresses = [IPAddress.Loopback];
        phone.CurrentPeer.PortArray = [(ushort)oldEndPoint.Port];
        phone.CurrentPeer.PreferredPort = (ushort)oldEndPoint.Port;
        desktop.CurrentPeer.Addresses = [IPAddress.Loopback];
        desktop.CurrentPeer.PortArray = [(ushort)desktop.LocalBoundPort];
        desktop.CurrentPeer.PreferredPort = (ushort)desktop.LocalBoundPort;
        await desktop.ProcessIncomingPacketAsync(phone.GenerateHelloPayload(MessageType.Interrogation, true),
            oldEndPoint, QP.TransportType.Wan);
        var contact = desktop.AvailablePeers[phone.CurrentPeer.Id];
        desktop.PeerStore!.AddOrUpdate(contact, autoConnect: true, save: true, nostrPubKey: phone.CertManager.NostrPublicKeyHex);
        desktop.Discovery.WanNostrDiscoveryEnabled = true;
        var connecting = desktop.EnsureMultiplexerConnectedAsync(contact, cancellationToken: deadline.Token);
        UdpReceiveResult packet;
        do { packet = await oldRoute.ReceiveAsync(deadline.Token); }
        while (packet.Buffer.Length <= QP.MagicHeader.Length || packet.Buffer[QP.MagicHeader.Length] != (byte)MessageType.Handshake);
        Check(Equals(contact.ActiveEndPoint, oldEndPoint) && !connecting.IsCompleted, "negotiation starts against the silent old control path");

        phone.CurrentPeer.PortArray = [(ushort)phone.LocalBoundPort];
        phone.CurrentPeer.PreferredPort = (ushort)phone.LocalBoundPort;
        phone.InvalidateTokenCache();
        string announcement = phone.Discovery.GetWanNostrDiscoveryPayload()!;
        long announced = Stopwatch.GetTimestamp();
        desktop.Discovery.OnWanNostrEventDiscovered(phone.CertManager.NostrPublicKeyHex, announcement);
        while (contact.ActiveEndPoint?.Port != phone.LocalBoundPort)
            await Task.Delay(5, deadline.Token);
        Check(Stopwatch.GetElapsedTime(announced) < TimeSpan.FromMilliseconds(500),
            "a signed update verifies the new route within 500 ms without waiting for the old handshake");

        var mux = await connecting.WaitAsync(TimeSpan.FromSeconds(8), deadline.Token);
        Check(!mux.IsDisposed && ReferenceEquals(mux.Peer, contact), "the original connection task finishes over the new authenticated path");
        desktop.Discovery.OnWanNostrEventDiscovered(phone.CertManager.NostrPublicKeyHex, announcement);
        Check(!desktop.ActiveInterrogations.Values.Any(attempt => attempt.VerifyRoute && attempt.Peer.Id == contact.Id),
            "a repeated announcement does not restart an already validated route check");
    }

    private static QP CreateNode(string path)
    {
        var node = new QP(appDataPath: path, listeningPort: 0)
        {
            EnableUpnp = false,
            StunServerEndpoints = [new IPEndPoint(IPAddress.Loopback, 9)],
            PeerTransport = new() { AutoConnectDiscoveredPeers = false }
        };
        node.Discovery.LanDiscoveryPort = 0;
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        node.ReputationFilter?.Dispose();
        node.ReputationFilter = null;
        return node;
    }
}
