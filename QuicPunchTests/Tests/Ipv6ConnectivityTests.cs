using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;

internal static class Ipv6ConnectivityTests
{
    public static async Task RunAsync()
    {
        Fixtures.LoopbackOnlyNetwork.Configure();
        if (!Socket.OSSupportsIPv6) throw new Exception("IPv6 unavailable on this test host.");
        Check(Utilities.ResolveEndpoint("[::1]:3478")?.Single().Equals(new IPEndPoint(IPAddress.IPv6Loopback, 3478)) == true,
            "literal IPv6 STUN endpoints resolve without IPv4 filtering");
        using var peer = new PeerInfo().SetCertificateHash(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        peer.Addresses = [IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("2001:4860:4860::8888")];
        peer.PortArray = [45000];
        using var decoded = Utilities.DecodeEndpointToken(Utilities.EncodeEndpointToken(peer));
        Check(decoded.Addresses.SequenceEqual(peer.Addresses), "mixed IPv4/IPv6 token roundtrip");
        peer.PreferredPort = 40000;
        var targets = QuicPunchConnection.BuildProbeTargets(peer, [], 0);
        Check(targets.Contains(new IPEndPoint(peer.Addresses[2], 40000)) && !targets.Contains(new IPEndPoint(peer.Addresses[2], 45000)),
            "IPv6 uses its host port instead of unrelated IPv4 NAT ports");
        using (var nat = new QuicPunch.Discovery.NatPinCoordinator())
        {
            nat.AddManualSample(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 45000), 10);
            nat.AddManualSample(new IPEndPoint(peer.Addresses[2], 40000), 10);
            Check(nat.CurrentMapping.DiscoveredAddresses.Length == 2 && nat.CurrentMapping.PortArray.SequenceEqual(new ushort[] { 45000 }),
                "dual-stack STUN preserves both families without corrupting IPv4 port prediction");
        }
        using var writerStream = new MemoryStream();
        using (var writer = new BinaryWriter(writerStream, System.Text.Encoding.UTF8, true))
            EndpointAddressCodec.WriteList(writer, peer.Addresses);
        writerStream.Position = 0;
        using var reader = new BinaryReader(writerStream);
        Check(EndpointAddressCodec.ReadList(reader).SequenceEqual(peer.Addresses), "versioned address list roundtrip");
        foreach (byte[] malformed in new byte[][] { [129, 15], [161], [129, 16, 1, 2] })
        {
            try { EndpointAddressCodec.ReadList(new BinaryReader(new MemoryStream(malformed))); throw new Exception("Malformed address accepted."); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var client = EndpointAddressCodec.CreateUdpClient();
        client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        var stun = new SimpleStunClient(client, [(IPEndPoint)server.Client.LocalEndPoint!]);
        await stun.SendRequest(deadline.Token);
        var request = await server.ReceiveAsync(deadline.Token);
        byte[] response = new byte[44];
        request.Buffer.CopyTo(response, 0);
        BinaryPrimitives.WriteUInt16BigEndian(response, 0x101);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 24);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(20), 0x20);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(22), 20);
        response[25] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(26), (ushort)(45000 ^ 0x2112));
        byte[] address = peer.Addresses[2].GetAddressBytes();
        for (int i = 0; i < 16; i++) response[28 + i] = (byte)(address[i] ^ response[4 + i]);
        Check(stun.TryProcessIncoming(response, (IPEndPoint)server.Client.LocalEndPoint!) &&
            stun.GetActiveHitsSnapshot().Keys.Single().Address.Equals(peer.Addresses[2]), "IPv6 XOR-MAPPED-ADDRESS with transaction mask");
        string root = Path.Combine(Path.GetTempPath(), "qp-ipv6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var a = new QuicPunch.QuicPunch(appDataPath: Path.Combine(root, "a"));
            using var b = new QuicPunch.QuicPunch(appDataPath: Path.Combine(root, "b"));
            using var socketA = EndpointAddressCodec.CreateUdpClient();
            using var socketB = EndpointAddressCodec.CreateUdpClient();
            socketA.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            socketB.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            var epA = new IPEndPoint(IPAddress.IPv6Loopback, ((IPEndPoint)socketA.Client.LocalEndPoint!).Port);
            var epB = new IPEndPoint(IPAddress.IPv6Loopback, ((IPEndPoint)socketB.Client.LocalEndPoint!).Port);
            Guid attempt = Guid.NewGuid();
            var paths = await Task.WhenAll(
                QuicPunchConnection.OpenPortCore(a.CurrentPeer, socketA, b.CurrentPeer, [new(epB, CandidateType.Host)], (ushort)epB.Port, attempt, deadline.Token, a.CertManager.Curve, null),
                QuicPunchConnection.OpenPortCore(b.CurrentPeer, socketB, a.CurrentPeer, [new(epA, CandidateType.Host)], (ushort)epA.Port, attempt, deadline.Token, b.CertManager.Curve, null));
            Check(paths[0].remoteEndpoint?.Equals(epB) == true && paths[1].remoteEndpoint?.Equals(epA) == true,
                "authenticated IPv6 connectivity checks nominate the actual endpoints");
            socketA.Close(); socketB.Close();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listening = QuicPunchConnection.TryRunServer(epA.Port, a.CertManager.PeerCertificate, b.CurrentPeer.CertHash,
                null, deadline.Token, () => { ready.TrySetResult(); return Task.CompletedTask; });
            await ready.Task.WaitAsync(deadline.Token);
            var connected = await QuicPunchConnection.TryRunClient(epA, b.CertManager.PeerCertificate, a.CurrentPeer.CertHash,
                epB.Port, null, deadline.Token);
            var accepted = await listening;
            await using var clientConnection = connected.Connection!;
            await using var serverConnection = accepted.Connection!;
            await using var clientStream = connected.Stream!;
            await using var serverStream = accepted.Stream!;
            await clientStream.WriteAsync(new byte[] { 42 }, deadline.Token);
            byte[] data = new byte[1];
            await serverStream.ReadExactlyAsync(data, deadline.Token);
            Check(data[0] == 42, "production pinned-certificate QUIC transfers data over IPv6");
        }
        finally { Directory.Delete(root, true); }
        await SessionRecoveryTests.RunIpv6Async();
    }

    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
