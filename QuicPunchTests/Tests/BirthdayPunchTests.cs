using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Security;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class BirthdayPunchTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync()
    {
        TestNetworkEvents();
        TestObservedPublicAddress();
        TestTranslatedSinglePortWindows();
        foreach (int width in new[] { 1, 2, 8, 63, 64, 257, 1024, 64512, 65535 })
        {
            var schedule = new PortProbeSchedule(713);
            var seen = new HashSet<ushort>();
            for (int offset = 0; offset < width; offset += 17)
                foreach (ushort port in schedule.Take(1, width, Math.Min(17, width - offset)))
                    if (!seen.Add(port)) throw new Exception("Port permutation repeated before exhausting its search space.");
            Check(seen.Count == width && seen.Min() == 1 && seen.Max() == width,
                $"random traversal covers all {width} ports without replacement");
        }
        using var peer = new PeerInfo { Addresses = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("192.168.5.47")],
            PortArray = [7042, 65310], PreferredPort = 49123, ConnectionFlags = new() { PortMode = PortMode.Range } };
        var sampler = new PortProbeSchedule(17);
        var targetsSeen = new HashSet<int>();
        for (int round = 0; round < 12; round++)
        {
            sampler.NextRound();
            var targets = QuicPunchConnection.BuildProbeTargets(peer, null, 0, 16, sampler);
            Check(targets.Count <= 16 && targets.Count(ep => ep.Address.Equals(peer.Addresses[1])) == 1 &&
                targets.Single(ep => ep.Address.Equals(peer.Addresses[1])).Port == 49123,
                "bounded rotating batches preserve the exact LAN endpoint");
            foreach (var ep in targets.Where(ep => ep.Address.Equals(peer.Addresses[0]))) targetsSeen.Add(ep.Port);
        }
        Check(targetsSeen.Count > 100, "waiting adds new public ports instead of repeating the same 16/32/64-point grid");
        peer.PortArray = [40000, 50000]; peer.ConnectionFlags.PortMode = PortMode.Multiple;
        sampler = new PortProbeSchedule(10);
        for (int i = 0; i < 4; i++) sampler.NextRound();
        var expanded = QuicPunchConnection.BuildProbeTargets(peer, null, 0, 16, sampler);
        Check(expanded.Any(ep => ep.Address.Equals(peer.Addresses[0]) && ep.Port != 40000 && ep.Port != 50000),
            "an explicit STUN list does not prevent searching for a new destination-dependent mapping");
        peer.ConnectionFlags.PortMode = PortMode.Single; peer.PortArray = [40000];
        Check(QuicPunchConnection.BuildProbeTargets(peer, null, 0, 16, sampler).Count == 2,
            "stable peers do not trigger speculative port expansion");

        string root = Path.Combine(Path.GetTempPath(), "qp-birthday-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var hard = new QP(appDataPath: Path.Combine(root, "hard")) { WanEnabled = false, EnableUpnp = false };
            await using var easy = new QP(appDataPath: Path.Combine(root, "easy")) { WanEnabled = false, EnableUpnp = false };
            hard.ReputationFilter?.Dispose(); easy.ReputationFilter?.Dispose();
            hard.ReputationFilter = easy.ReputationFilter = null;
            hard.CurrentPeer.ConnectionFlags.PortMode = PortMode.Range;
            easy.CurrentPeer.ConnectionFlags.PortMode = PortMode.Single;
            easy.CurrentPeer.Addresses = [IPAddress.Parse("93.184.216.34")];
            Check(QuicPunchConnection.BirthdayTarget(hard.CurrentPeer, easy.CurrentPeer, null, 45000)?.Port == 45000,
                "one hard NAT enables bounded many-socket punching toward a stable peer");
            hard.CurrentPeer.ConnectionFlags.PortMode = PortMode.Single;
            Check(QuicPunchConnection.BirthdayTarget(hard.CurrentPeer, easy.CurrentPeer, null, 45000) == null,
                "an unchanged single local port does not open a birthday socket pool");
            hard.CurrentPeer.ConnectionFlags.ExternalPortMayDiffer = true;
            Check(QuicPunchConnection.BirthdayTarget(hard.CurrentPeer, easy.CurrentPeer, null, 45000)?.Port == 45000,
                "a translated single local port enables the bounded birthday socket pool");
            hard.CurrentPeer.ConnectionFlags.ExternalPortMayDiffer = false;
            hard.CurrentPeer.ConnectionFlags.PortMode = PortMode.Range;
            easy.CurrentPeer.ConnectionFlags.PortMode = PortMode.Range;
            Check(QuicPunchConnection.BirthdayTarget(hard.CurrentPeer, easy.CurrentPeer, null, 45000) == null,
                "two hard NATs do not trigger an ineffective socket explosion");
            await TestMappingFastPath(hard);
            await TestRestartHandshake(hard, easy);
            await TestLastSocketAndHandoff(hard, easy);
            await TestCancellation(hard, easy);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void TestTranslatedSinglePortWindows()
    {
        var wan = IPAddress.Parse("93.184.216.34");
        var lan = IPAddress.Parse("192.168.5.47");
        using var peer = new PeerInfo
        {
            Addresses = [wan, lan], PortArray = [40000], PreferredPort = 49123,
            ConnectionFlags = new() { PortMode = PortMode.Single, ExternalPortMayDiffer = true }
        };
        CandidateEndpoint[] candidates = [new(new(lan, 27104), CandidateType.Host), new(new(wan, 50000), CandidateType.ServerReflexive)];
        var schedule = new PortProbeSchedule(713);
        var fullWindow = new HashSet<int>();
        for (int round = 1; round <= 240; round++)
        {
            schedule.NextRound();
            var targets = QuicPunchConnection.BuildProbeTargets(peer, candidates, 27104, 16, schedule);
            if (targets.Count > 16 || targets.Count != targets.Distinct().Count() ||
                !targets.Contains(new IPEndPoint(wan, 50000)) ||
                !targets.Where(ep => ep.Address.Equals(lan)).Select(ep => ep.Port).SequenceEqual(new[] { 27104 }))
                throw new Exception("Translated Single probing lost exact candidate priority, LAN scope or its 16-endpoint budget.");
            int minimum = round <= 16 ? 50000 : 48976;
            int maximum = round <= 8 ? 50512 : 51024;
            var guesses = targets.Where(ep => ep.Address.Equals(wan) && ep.Port != 27104 && ep.Port != 40000).ToArray();
            if (guesses.Length < 2 || guesses.Any(ep => ep.Port < minimum || ep.Port > maximum))
                throw new Exception("Single-port guesses escaped the staged QUIC-candidate window or used the control port as their anchor.");
            if (round > 16) foreach (var endpoint in guesses) fullWindow.Add(endpoint.Port);
        }
        Check(fullWindow.Count == 2049 && fullWindow.Min() == 48976 && fullWindow.Max() == 51024,
            "translated Single rotates through +512, +1024 and the full +/-1024 window while preserving exact/LAN candidates");

        // Stable Single and existing list/range traversal must keep their old targets.
        peer.ConnectionFlags.ExternalPortMayDiffer = false;
        var exact = QuicPunchConnection.BuildProbeTargets(peer, candidates, 27104, 16, schedule);
        Check(exact.All(ep => ep.Port is 27104 or 40000 or 50000), "Single without the translation bit never guesses neighbouring ports");
        foreach (var mode in new[] { PortMode.Multiple, PortMode.Range })
        {
            peer.PortArray = [40000, 41000]; peer.ConnectionFlags.PortMode = mode;
            var before = new PortProbeSchedule(17); var after = new PortProbeSchedule(17);
            for (int i = 0; i < 20; i++) { before.NextRound(); after.NextRound(); }
            peer.ConnectionFlags.ExternalPortMayDiffer = false;
            var original = QuicPunchConnection.BuildProbeTargets(peer, candidates, 27104, 16, before);
            peer.ConnectionFlags.ExternalPortMayDiffer = true;
            Check(original.SequenceEqual(QuicPunchConnection.BuildProbeTargets(peer, candidates, 27104, 16, after)),
                $"the translation bit does not alter existing {mode} traversal");
        }

        peer.Addresses = [wan]; peer.ConnectionFlags.PortMode = PortMode.Single;
        foreach (ushort port in new ushort[] { 1, 65535 })
        {
            peer.PortArray = [port];
            var edge = new PortProbeSchedule(19);
            for (int i = 0; i < 17; i++) edge.NextRound();
            var targets = QuicPunchConnection.BuildProbeTargets(peer, null, port, 16, edge);
            Check(targets.Count > 1 && targets.All(ep => ep.Port >= Math.Max(1, port - 1024) && ep.Port <= Math.Min(65535, port + 1024)),
                $"translated port {port} clips its window without overflow or UDP port zero");
        }

        // A fresh public candidate may be absent from the control address list.
        var otherWan = IPAddress.Parse("1.2.3.4");
        peer.PortArray = [40000];
        var next = new PortProbeSchedule(7); next.NextRound();
        var alternate = QuicPunchConnection.BuildProbeTargets(peer,
            [new(new(otherWan, 52000), CandidateType.ServerReflexive)], 27104, 16, next);
        Check(alternate.Any(ep => ep.Address.Equals(otherWan) && ep.Port > 52000 && ep.Port <= 52512),
            "a public QUIC candidate on a new IP also receives neighbouring-port probes");
    }

    private static void TestObservedPublicAddress()
    {
        var lan = IPAddress.Parse("192.168.5.2");
        var advertised = IPAddress.Parse("79.112.165.78");
        var observed = IPAddress.Parse("79.116.27.132");
        using var peer = new PeerInfo { Addresses = [lan, advertised], PortArray = [60000, 60001],
            ActiveEndPoint = new IPEndPoint(observed, 65310), ConnectionFlags = new() { PortMode = PortMode.Multiple } };
        CandidateEndpoint[] candidates = [new(new(lan, 27104), CandidateType.Host), new(new(advertised, 53200), CandidateType.ServerReflexive)];
        var schedule = new PortProbeSchedule(17);
        for (int i = 0; i < 4; i++) schedule.NextRound();
        var targets = QuicPunchConnection.BuildProbeTargets(peer, candidates, 27104, 16, schedule);
        Check(targets.Contains(new IPEndPoint(observed, 53200)),
            "a verified current public address is tried with the tunnel's reflexive port when STUN advertises another IP");
        Check(targets.Any(ep => ep.Address.Equals(observed) && ep.Port != 27104 && ep.Port != 53200 && !peer.PortArray.Contains((ushort)ep.Port)),
            "destination-dependent port search includes the observed public address, not only stale advertised IPs");
        Check(targets.Count <= 16 && targets.Count(ep => ep.Address.Equals(lan)) == 1 && targets.Contains(new IPEndPoint(lan, 27104)),
            "public IP fallback keeps the bounded probe budget and exact LAN candidate");
    }

    private static void TestNetworkEvents()
    {
        var tracker = new QuicNet.Settings.NetworkPathTracker("wifi-121", "wlan0|192.168.5.47/24|default-v4");
        using var attempt = new CancellationTokenSource();
        for (int i = 0; i < 3; i++)
            if (tracker.Update("wifi-121", "wlan0|192.168.5.47/24|default-v4")) attempt.Cancel();
        Check(!attempt.IsCancellationRequested, "initial OnAvailable/repeated LinkProperties events preserve an in-flight Android connection");
        Check(!tracker.Lost("old-120"), "a late loss event for a previous network does not reset the current path");
        Check(tracker.Update("cell-122", "rmnet|10.0.0.2/24|cell-default") && !tracker.Lost("wifi-121"),
            "real handover refreshes once and ignores the superseded Wi-Fi loss");
        Check(tracker.Lost("cell-122") && !tracker.Lost("cell-122"), "real loss refreshes once");
        Check(tracker.Update("wifi-121", "wlan0|192.168.5.48/24|default-v4") &&
            tracker.Update("wifi-121", "wlan0|192.168.5.48/24|new-gateway"),
            "IP and IPv4 route changes still trigger recovery");
    }

    private static async Task TestRestartHandshake(QP node, QP remote)
    {
        node.PeerTransport = node.PeerTransport with { AutoConnectDiscoveredPeers = false };
        node.WanEnabled = true;
        node.EnableUpnp = false;
        node.Discovery.LanDiscoveryPort = 0;
        node.Discovery.WanNostrDiscoveryEnabled = false;
        node.Discovery.TorNostrDiscoveryEnabled = false;
        node.StunServerEndpoints = [new IPEndPoint(IPAddress.Loopback, 9)];
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        await node.StartAsync();
        remote.CurrentPeer.PortArray = [41002];
        remote.CurrentPeer.Addresses = [IPAddress.Loopback];
        var endpoint = new IPEndPoint(IPAddress.Loopback, 41002);
        await node.ProcessIncomingPacketAsync(remote.GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true),
            endpoint, QP.TransportType.Wan);
        var peer = node.AvailablePeers[remote.CurrentPeer.Id];
        node.TrustPeer(peer.CertHash);
        using var stream = new SessionRecoveryTests.SilentStream();
        await using var stale = new QuicPunch.Multiplexer.QuicPeerMultiplexer(
            QuicConnection.FromTransport(new SessionRecoveryTests.SilentConnection()), peer, true, _ => null, _ => true,
            initialControlStream: stream, transportOptions: new()
            { HeartbeatInterval = TimeSpan.FromSeconds(2), HeartbeatTimeout = TimeSpan.FromSeconds(8) });
        await stale.StartAsync();
        node.RegisterPeerMultiplexer(peer.Id, stale);
        Guid request = Guid.NewGuid();
        await node.ProcessIncomingPacketAsync(remote.GenerateHandshakePayload(QuicPunchStructures.HandShakeType.Request,
            45002, Guid.Empty, request), endpoint, QP.TransportType.Wan);
        var session = node.IncomingHandshakeSessions[request];
        Check(!session.ResponsePayloadTcs.Task.IsCompleted, "replacement handshake probes an apparently open old tunnel before declining it");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        byte[] response = await session.ResponsePayloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(response.Length > QP.MagicHeader.Length + 1 + 16 && response[QP.MagicHeader.Length + 1 + 16] == (byte)QuicPunchStructures.HandShakeType.Accept && stale.IsDisposed,
            "a silent previous mobile tunnel is replaced in the SAME request, avoiding decline/backoff loops");
        Check(clock.Elapsed < TimeSpan.FromSeconds(3), "an idle previous tunnel does not impose the full eight-second heartbeat deadline on mobile restart");
        if (node._activeIncomingWorkers.TryGetValue(request, out var worker))
        {
            worker.Cts.Cancel();
            await worker.Task;
        }
    }

    private static async Task TestMappingFastPath(QP node)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        node.EnableUpnp = true;
        var map = typeof(QP).GetMethod("MapTunnelAsync", flags)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await (Task<QuicPunch.Discovery.PortMapping.PortMappingResult?>)map.Invoke(node, new object[] { 45123, timeout.Token })!;
        Check(result == null && watch.Elapsed < TimeSpan.FromMilliseconds(200),
            "no successful control mapping means data candidates skip repeated gateway-discovery timeouts");
        typeof(QP).GetProperty("LocalDiscoveryPort")!.SetValue(node, 45123);
        var router = typeof(QP).GetField("_advertisedMapping", flags)!;
        var changed = typeof(QP).GetMethod("OnNatMappingChanged", flags)!;
        var nat = new QuicPunch.Discovery.NatMappingResult
        {
            DiscoveredAddresses = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("1.2.3.4")],
            PortArray = [7042, 65310], ConnectionFlags = new() { PortMode = PortMode.Range, IsNat = true }
        };
        router.SetValue(node, QuicPunch.Discovery.PortMapping.PortMappingResult.Succeeded("UPnP IGD",
            IPAddress.Parse("93.184.216.34"), 45124, 45123, TimeSpan.FromMinutes(2)));
        changed.Invoke(node, new object[] { node, nat });
        Check(node.CurrentPeer.ConnectionFlags.PortMode == PortMode.Single && node.CurrentPeer.PortArray.SequenceEqual(new ushort[] { 45124 })
            && !node.CurrentPeer.Addresses.Contains(IPAddress.Parse("1.2.3.4")),
            "a renewed public UPnP lease stays an exact port/IP instead of becoming a random STUN range");
        router.SetValue(node, QuicPunch.Discovery.PortMapping.PortMappingResult.Succeeded("UPnP IGD",
            IPAddress.Parse("100.64.0.1"), 45124, 45123, TimeSpan.FromMinutes(2)));
        changed.Invoke(node, new object[] { node, nat });
        Check(node.CurrentPeer.ConnectionFlags.PortMode == PortMode.Range && node.CurrentPeer.PortArray.SequenceEqual(new ushort[] { 7042, 65310 }),
            "an inner CGNAT/private mapping does not pretend to be a stable public port");
        router.SetValue(node, null);
    }

    private static async Task TestLastSocketAndHandoff(QP hard, QP easy)
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)remote.Client.LocalEndPoint!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Guid connection = Guid.NewGuid(), challenge = Guid.NewGuid();
        byte[] password = RandomNumberGenerator.GetBytes(32);
        var opening = QuicPunchConnection.OpenBirthdaySocketsAsync(endpoint, connection,
            hard.CurrentPeer, easy.CurrentPeer, hard.CertManager.Curve, password, deadline.Token);
        var ports = new HashSet<int>();
        IPEndPoint? selected = null;
        Guid firstChallenge = Guid.Empty;
        while (ports.Count < QuicPunchConnection.MaxBirthdaySockets)
        {
            var request = await remote.ReceiveAsync(deadline.Token);
            Check(ConnectivityProbe.TryVerify(request.Buffer, connection, easy.CurrentPeer, hard.CurrentPeer,
                password, out firstChallenge, out _), "birthday socket carries authenticated peer/password proof");
            ports.Add(request.RemoteEndPoint.Port);
            selected = request.RemoteEndPoint;
        }
        // Model a NAT/firewall that only lets the LAST opened mapping return.
        await remote.SendAsync(ConnectivityProbe.Create(connection, challenge, firstChallenge,
            easy.CurrentPeer, hard.CurrentPeer, easy.CertManager.Curve, password), selected!, deadline.Token);
        while (true)
        {
            var response = await remote.ReceiveAsync(deadline.Token);
            if (!response.RemoteEndPoint.Equals(selected)) continue;
            if (!ConnectivityProbe.TryVerify(response.Buffer, connection, easy.CurrentPeer, hard.CurrentPeer,
                password, out var newChallenge, out var echo) || echo != challenge) continue;
            await remote.SendAsync(ConnectivityProbe.Create(connection, Guid.NewGuid(), newChallenge,
                easy.CurrentPeer, hard.CurrentPeer, easy.CertManager.Curve, password), selected!, deadline.Token);
            break;
        }
        var result = await opening;
        using var winning = result.Socket ?? throw new Exception("No birthday path was nominated.");
        Check(((IPEndPoint)winning.Client.LocalEndPoint!).Port == selected!.Port && result.Endpoint!.Equals(endpoint),
            "the verified winning SOCKET, not the original port, is retained for QUIC handoff");
        foreach (int port in ports.Where(p => p != selected.Port).Take(3))
        {
            using var reusable = new UdpClient { ExclusiveAddressUse = true };
            reusable.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        Check(true, "losing sockets are closed before the successful attempt returns");
        int localPort = ((IPEndPoint)winning.Client.LocalEndPoint!).Port;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = QuicPunchConnection.TryRunServer(localPort, hard.CertManager.PeerCertificate,
            easy.CurrentPeer.CertHash, winning, deadline.Token, () => { ready.TrySetResult(); return Task.CompletedTask; });
        await ready.Task.WaitAsync(deadline.Token);
        var originalLogger = QuicPunchLog.LogHandler;
        int bindRetries = 0;
        QuicPunchLog.LogHandler = line => { if (line.Contains("[QUIC CLIENT ATTEMPT]")) Interlocked.Increment(ref bindRetries); originalLogger?.Invoke(line); };
        (QuicConnection? Connection, QuicStream? Stream) client;
        try { client = await QuicPunchConnection.TryRunClient(new IPEndPoint(IPAddress.Loopback, localPort),
            easy.CertManager.PeerCertificate, hard.CurrentPeer.CertHash, endpoint.Port, remote, deadline.Token); }
        finally { QuicPunchLog.LogHandler = originalLogger; }
        var accepted = await accepting;
        Check(client.Connection != null && accepted.Connection != null, "native QUIC binds the winning birthday port with pinned peer certificates");
        try
        {
            Check(bindRetries == 0 && ((IPEndPoint)client.Connection!.LocalEndPoint!).Port == endpoint.Port,
                "QUIC takes over the nominated UDP port without overlapping binds or retrying its own socket");
            await client.Stream!.WriteAsync(new byte[] { 42 }, deadline.Token);
            byte[] payload = new byte[1];
            await accepted.Stream!.ReadExactlyAsync(payload, deadline.Token);
            Check(payload[0] == 42, "application bytes cross the native QUIC stream after birthday socket handoff");
        }
        finally
        {
            if (client.Stream != null) await client.Stream.DisposeAsync();
            if (accepted.Stream != null) await accepted.Stream.DisposeAsync();
            if (client.Connection != null) await client.Connection.DisposeAsync();
            if (accepted.Connection != null) await accepted.Connection.DisposeAsync();
        }
    }

    private static async Task TestCancellation(QP hard, QP easy)
    {
        using var remote = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        var result = await QuicPunchConnection.OpenBirthdaySocketsAsync((IPEndPoint)remote.Client.LocalEndPoint!,
            Guid.NewGuid(), hard.CurrentPeer, easy.CurrentPeer, hard.CertManager.Curve, null, deadline.Token);
        Check(result.Socket == null && result.Endpoint == null, "cancellation closes all temporary paths without nominating an endpoint");
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        result = await QuicPunchConnection.OpenBirthdaySocketsAsync((IPEndPoint)remote.Client.LocalEndPoint!,
            Guid.NewGuid(), hard.CurrentPeer, easy.CurrentPeer, hard.CertManager.Curve, null, alreadyCancelled.Token);
        Check(result.Socket == null, "cancellation during direct-path head start leaves the pool gate reusable");
    }
}
