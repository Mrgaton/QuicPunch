using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.Vpn;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunch.Vpn;
using QuicPunch.Vpn.Broker;
using static QuicPunch.QuicPunchStructures;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class VpnOrchestrationTests
{
    private sealed class Session : IVpnSession
    {
        private readonly ConcurrentQueue<byte[]> _capture = new();
        public readonly ConcurrentQueue<byte[]> Received = new();
        public string[] Routes = [];
        public string[] InboundDestinations = [];
        public bool Forwarding, Broadcast;
        public volatile bool FailCapture;
        public long LastReceivedAt;
        public bool IsHealthy { get; private set; } = true;
        public void Capture(byte[] bytes) => _capture.Enqueue(bytes);
        public bool WaitForPacket(int timeoutMilliseconds, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); if (FailCapture) throw new IOException("Simulated adapter failure."); if (_capture.IsEmpty) Thread.Sleep(Math.Min(10, timeoutMilliseconds)); return !_capture.IsEmpty; }
        public int ReceivePacket(Span<byte> destination)
        { if (!_capture.TryDequeue(out var bytes)) return 0; bytes.CopyTo(destination); return bytes.Length; }
        public void SendPacket(ReadOnlySpan<byte> packet)
        {
            Volatile.Write(ref LastReceivedAt, Stopwatch.GetTimestamp());
            Received.Enqueue(packet.ToArray());
        }
        public void Close() => Dispose();
        public void Dispose() => IsHealthy = false;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Adapter(Action<Session> started) : IVpnAdapter, IVpnRouteAdapter
    {
        private Session? _session;
        private string[] _routes = [];
        private string[] _inbound = [];
        private bool _forwarding, _broadcast;
        public string Name => "test";
        public bool IsActive => _session?.IsHealthy == true;
        public IPAddress? LocalIp { get; private set; }
        public string? SubnetMask { get; private set; }
        public int Mtu { get; private set; }
        public void SetIpAddress(IPAddress ip, string subnetMask) { LocalIp = ip; SubnetMask = subnetMask; }
        public void SetMtu(int mtu) => Mtu = mtu;
        public void SetRoutes(string[] prefixes) => _routes = prefixes;
        public void SetForwarding(bool enabled) => _forwarding = enabled;
        public void SetAccess(string[] inboundDestinations, bool broadcast) { _inbound = inboundDestinations; _broadcast = broadcast; }
        public IVpnSession StartSession(uint bufferCapacity = 0x400000 * 4) { _session = new() { Routes = _routes, InboundDestinations = _inbound, Forwarding = _forwarding, Broadcast = _broadcast }; started(_session); return _session; }
        public void Close() => Dispose();
        public void Dispose() => _session?.Dispose();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("[PASS] " + label); }
    private static async Task Wait(Func<bool> condition, string label, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try { while (!condition()) await Task.Delay(20, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception("Timed out: " + label); }
        Check(true, label);
    }
    private static async Task<QP> Node(string path)
    {
        var node = new QP(appDataPath: path) { EnableUpnp = false };
        node.Discovery.NostrRelays = ["ws://127.0.0.1:9"];
        node.Discovery.WanNostrDiscoveryEnabled = false; node.Discovery.TorNostrDiscoveryEnabled = false;
        node.Discovery.LanDiscoveryPort = 0;
        node.ReputationFilter?.Dispose(); node.ReputationFilter = null;
        await node.StartAsync(); await node.Mailbox.StopAsync();
        node.CurrentPeer.Addresses = [IPAddress.Loopback]; node.CurrentPeer.PortArray = [(ushort)node.LocalBoundPort];
        return node;
    }
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "quicnet-vpn-orchestrator-" + Guid.NewGuid().ToString("N"));
        try { await ExerciseMixedModesAsync(Path.Combine(root, "mixed")); await ExerciseDeferredStartupAsync(Path.Combine(root, "deferred")); await ExerciseAsync(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task RunWintunAsync()
    {
        if (!OperatingSystem.IsWindows() || !QuicNet.WebUi.Modules.LanApiModule.IsRunningAsAdmin())
            throw new InvalidOperationException("The Wintun smoke test requires an isolated, elevated Windows instance.");
        const string adapterName = "QuicPunchAdapter";
        if (NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Wintun smoke test needs its own adapter. A QuicPunchAdapter already exists.");

        string root = Path.Combine(Path.GetTempPath(), "quicnet-wintun-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var a = await Node(Path.Combine(root, "a"));
            await using var b = await Node(Path.Combine(root, "b"));
            a.TrustPeer(b.CurrentPeer.CertHash);
            b.TrustPeer(a.CurrentPeer.CertHash);
            using var aHandler = new VirtualLanHandler(() => VpnAdapterFactory.Create(adapterName, "QuicPunchTunnel"));
            Session? bSession = null;
            using var bHandler = new VirtualLanHandler(() => new Adapter(session => bSession = session));
            a.RegisterProtocol(aHandler);
            b.RegisterProtocol(bHandler);
            var aIp = IPAddress.Parse("10.203.17.1");
            var bIp = IPAddress.Parse("10.203.17.2");
            using var aStore = new VpnConfigurationStore(Path.Combine(root, "a-vpn.json"), Configuration(aIp, bIp, b.CurrentPeer));
            using var bStore = new VpnConfigurationStore(Path.Combine(root, "b-vpn.json"), Configuration(bIp, aIp, a.CurrentPeer));
            using var stop = new CancellationTokenSource();
            await using var aController = new VpnController(a, aHandler, aStore, stop.Token);
            await using var bController = new VpnController(b, bHandler, bStore, stop.Token);
            await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(MessageType.Interrogation, true), new IPEndPoint(IPAddress.Loopback, b.LocalBoundPort), QP.TransportType.Wan);
            await b.ProcessIncomingPacketAsync(a.GenerateHelloPayload(MessageType.Interrogation, true), new IPEndPoint(IPAddress.Loopback, a.LocalBoundPort), QP.TransportType.Wan);
            await Wait(() => aHandler.IsActive || aHandler.LastError != null, "Wintun finished adapter setup");
            if (!aHandler.IsActive) throw new InvalidOperationException(aHandler.LastError ?? "Wintun adapter did not start.");
            await Wait(() => aHandler.HasActivePeer(b.CurrentPeer.Id) && bHandler.HasActivePeer(a.CurrentPeer.Id), "Wintun tunnel and test peer are ready over native QUIC");
            Check(NetworkInterface.GetAllNetworkInterfaces().Any(adapter => adapter.Name == adapterName &&
                adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(aIp) &&
                    address.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)), "Windows can use the Wintun IPv4 address when the tunnel is ready");

            using var udp = new UdpClient(new IPEndPoint(aIp, 0));
            int clientPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            const ushort peerPort = 42017;
            var peerEndpoint = new IPEndPoint(bIp, peerPort);
            byte[] payload = Guid.NewGuid().ToByteArray();
            var aTunnel = aHandler.ActivePeers.Values.Single();
            var bTunnel = bHandler.ActivePeers.Values.Single();
            await Wait(() => aTunnel.SupportsFragmentedDatagrams && aTunnel.Connection.DatagramChannel?.IsSendEnabled == true,
                "Wintun and the peer negotiated native datagrams");
            // The fake peer shares this Windows stack, so its policy correctly rejects
            // the Wintun address as a local source. Observe the packet on its QUIC channel.
            var channel = bTunnel.Connection.DatagramChannel ?? throw new InvalidOperationException("The peer has no datagram channel.");
            var outbound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(byte[] packet) { if (packet.AsSpan().EndsWith(payload)) outbound.TrySetResult(); }
            channel.OnDatagramReceived += Observe;
            try
            {
                await udp.SendAsync(payload, peerEndpoint);
                await outbound.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally { channel.OnDatagramReceived -= Observe; }
            Check(true, "OS UDP crosses Wintun and reaches the peer's QUIC channel");
            bSession!.Capture(VpnPolicyTests.Packet(bIp.ToString(), aIp.ToString(), peerPort, (ushort)clientPort));
            using var receiveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var reply = await udp.ReceiveAsync(receiveDeadline.Token);
            Check(reply.RemoteEndPoint.Equals(peerEndpoint) && reply.Buffer.Length == 0, "peer UDP returns through Wintun to the OS socket");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        static VpnConfiguration Configuration(IPAddress local, IPAddress remote, PeerInfo peer) => new()
        {
            Enabled = true, AutoAssign = false, LocalAddress = local.ToString(), Mode = "advanced",
            Peers = [new() { CertHash = Convert.ToHexString(peer.CertHash), VirtualAddress = remote.ToString(),
                AutoConnect = true, AutoAccept = true, AccessibleDestinations = [local + "/32"] }]
        };
    }
    private static async Task ExerciseMixedModesAsync(string root)
    {
        using var node = new QP(appDataPath: Path.Combine(root, "node"));
        byte[] a = Enumerable.Repeat((byte)21, 32).ToArray(), b = Enumerable.Repeat((byte)22, 32).ToArray();
        node.TrustPeer(a); node.TrustPeer(b);
        using var store = new VpnConfigurationStore(Path.Combine(root, "vpn.json"), new() {
            AutoAssign = false, AutoConnect = false, LocalAddress = "10.203.0.1", Peers = [
                new() { CertHash = Convert.ToHexString(a), Mode = "advanced", VirtualAddress = "10.203.0.2", AutoConnect = true,
                    AllowedSources = ["192.168.50.0/24"], AccessibleDestinations = ["192.168.1.0/24"], Routes = ["192.168.50.0/24"], AllowForwarding = true },
                new() { CertHash = Convert.ToHexString(b), Mode = "simple", VirtualAddress = "10.203.0.2", AutoConnect = false,
                    AllowedSources = ["192.168.50.0/24"], AccessibleDestinations = ["192.168.1.0/24"], Routes = ["192.168.50.0/24"], AllowForwarding = true }
            ] });
        Session? session = null;
        using var handler = new VirtualLanHandler(() => new Adapter(s => session = s));
        await using var controller = new VpnController(node, handler, store, CancellationToken.None);
        var config = controller.Configuration;
        var policy = controller.Compile(config);
        var advanced = policy.Peer(Convert.ToHexString(a))!;
        var simple = policy.Peer(Convert.ToHexString(b))!;
        Check(advanced.Forwarding && advanced.Routes.Any(r => r.ToString() == "192.168.50.0/24") && advanced.AutoConnect,
            "advanced peer retains routes and startup choice even with legacy global mode simple and autoconnect disabled");
        Check(simple.Broadcast && !simple.Forwarding && !simple.AutoConnect && simple.Routes.Length == 1 && simple.Sources.Length == 1 &&
            simple.Destinations.Single().ToString() == "10.203.0.1/32" && simple.Address != advanced.Address,
            "automatic peer uses its automatic address and ignores dormant advanced ACLs and routes");
        config.Mode = "advanced"; config.Enabled = true;
        controller.SaveConfiguration(config, config.Revision);
        Check(session is { Forwarding: true, Broadcast: true } && session.InboundDestinations.Contains("192.168.1.0/24") && session.Routes.Contains("192.168.50.0/24"),
            "adapter receives the effective forwarding, broadcast, access and routes of mixed peer policies");
        Check(controller.Compile(controller.Configuration).Peer(Convert.ToHexString(b))!.Broadcast,
            "explicit peer mode overrides either legacy global mode");
        using var reload = new VpnConfigurationStore(store.Path, new());
        Check(reload.Snapshot().Peers[0] is { Mode: "advanced", AutoConnect: true } && reload.Snapshot().Peers[1] is { Mode: "simple", AutoConnect: false },
            "mixed peer modes and startup choices survive saving and reopening");
        config = controller.Configuration; config.Peers[1].Mode = null;
        // Remove inactive fields so the old global-mode semantics can be exercised independently.
        config.Peers[1].VirtualAddress = ""; config.Peers[1].Routes = []; config.Peers[1].AllowedSources = [];
        Check(!controller.Compile(config).Peer(Convert.ToHexString(b))!.Broadcast,
            "legacy peers with no mode inherit their existing global advanced behavior");
        config.Peers[0].Mode = null;
        Check(!controller.Compile(config).Peer(Convert.ToHexString(a))!.AutoConnect,
            "legacy peers still respect the old global startup opt-out until explicitly configured");
        config = controller.Configuration; config.Peers[0].Enabled = false;
        controller.SaveConfiguration(config, config.Revision);
        Check(session is { Forwarding: false, Broadcast: true } && !session.InboundDestinations.Contains("192.168.1.0/24") && !session.Routes.Contains("192.168.50.0/24"),
            "removing the advanced peer revokes its adapter permissions while keeping automatic LAN available");
    }
    private static async Task ExerciseDeferredStartupAsync(string root)
    {
        using var node = new QP(appDataPath: Path.Combine(root, "node"));
        using var peer = new PeerInfo().SetCertificateHash(Enumerable.Repeat((byte)7, 32).ToArray());
        node.TrustPeer(peer.CertHash);
        string path = Path.Combine(root, "vpn.json");
        using (var store = new VpnConfigurationStore(path, new()))
        using (var unavailable = new VirtualLanHandler(() => throw new UnauthorizedAccessException("Test: administrator privileges unavailable")))
        {
            await using var controller = new VpnController(node, unavailable, store, CancellationToken.None);
            var desired = controller.Configuration;
            desired.Enabled = true; desired.AutoConnect = true;
            desired.Peers.Add(new() { CertHash = Convert.ToHexString(peer.CertHash), VirtualAddress = "10.0.0.253", AutoConnect = true, AutoAccept = true });
            controller.SaveConfiguration(desired, desired.Revision);
            Check(!unavailable.IsActive && controller.Configuration.Enabled && controller.ConfigurationError != null,
                "automatic VPN settings are saved even when adapter privileges are unavailable");
        }
        using (var restored = new VpnConfigurationStore(path, new()))
        using (var available = new VirtualLanHandler(() => new Adapter(_ => { })))
        {
            await using var restarted = new VpnController(node, available, restored, CancellationToken.None);
            await Wait(() => available.IsActive, "next startup activates the saved VPN when adapter privileges become available");
            Check(restarted.Configuration.Peers.Single() is { AutoConnect: true, AutoAccept: true } &&
                available.IncomingSessionPolicy(peer) == true, "saved peer reconnect and automatic acceptance survive restart");
            using var unrelated = new PeerInfo().SetCertificateHash(Enumerable.Repeat((byte)8, 32).ToArray());
            Check(available.IncomingSessionPolicy(unrelated) == false, "automatic acceptance remains limited to the configured identity");
        }
    }
    private static async Task ExerciseAsync(string root)
    {
        await using var a = await Node(Path.Combine(root, "a"));
        await using var b = await Node(Path.Combine(root, "b"));
        a.TrustPeer(b.CurrentPeer.CertHash); b.TrustPeer(a.CurrentPeer.CertHash);
        Session? aSession = null, bSession = null;
        using var aHandler = new VirtualLanHandler(() => new Adapter(s => aSession = s));
        using var bHandler = new VirtualLanHandler(() => new Adapter(s => bSession = s));
        a.RegisterProtocol(aHandler); b.RegisterProtocol(bHandler);
        using var aStore = new VpnConfigurationStore(Path.Combine(root, "a-vpn.json"), new() { Enabled = true, Peers = [new() {
            CertHash = Convert.ToHexString(b.CurrentPeer.CertHash), AutoConnect = true, AutoAccept = true }] });
        using var bStore = new VpnConfigurationStore(Path.Combine(root, "b-vpn.json"), new() { Enabled = true, Peers = [new() {
            CertHash = Convert.ToHexString(a.CurrentPeer.CertHash), AutoConnect = true, AutoAccept = true }] });
        using var stop = new CancellationTokenSource();
        await using var aController = new VpnController(a, aHandler, aStore, stop.Token);
        await using var bController = new VpnController(b, bHandler, bStore, stop.Token);
        long acceptedAt = 0;
        bool? advertisedBeforeReady = null;
        b.OnSessionStateChanged += change =>
        {
            if (change.PeerId != a.CurrentPeer.Id || change.ProtocolId != bHandler.ProtocolId || change.State != SessionState.Connected) return;
            advertisedBeforeReady = bHandler.GetActivePeerSummaries().Any();
            Volatile.Write(ref acceptedAt, Stopwatch.GetTimestamp());
        };
        int petitions = 0;
        b.Manager.HandshakeRequested += (_, _) => { Interlocked.Increment(ref petitions); return Task.FromResult(new HandshakeDecision(false, null, CancellationToken.None)); };
        await a.ProcessIncomingPacketAsync(b.GenerateHelloPayload(MessageType.Interrogation, true), new IPEndPoint(IPAddress.Loopback, b.LocalBoundPort), QP.TransportType.Wan);
        await b.ProcessIncomingPacketAsync(a.GenerateHelloPayload(MessageType.Interrogation, true), new IPEndPoint(IPAddress.Loopback, a.LocalBoundPort), QP.TransportType.Wan);
        await Wait(() => aHandler.HasActivePeer(b.CurrentPeer.Id) && bHandler.HasActivePeer(a.CurrentPeer.Id), "VPN opens automatically over real QUIC without a local petition");
        Check(advertisedBeforeReady == false, "accepted VPN is omitted from the active peers sent to the UI until ready");
        var readyPeer = (JsonElement)bHandler.GetActivePeerSummaries().Single();
        Check(readyPeer.GetProperty("status").GetString() == "Connected" &&
            readyPeer.GetProperty("peerId").GetString() == a.CurrentPeer.Id.ToString(), "ready VPN appears as connected in the UI status");
        byte[] packet = VpnPolicyTests.Packet(aHandler.LocalIp.ToString(), bHandler.LocalIp.ToString());
        aSession!.Capture(packet);
        await Wait(() => bSession!.Received.Any(p => p.SequenceEqual(packet)), "IPv4 crosses the automatic VPN as soon as both adapters are ready");
        LogFirstIpv4("Initial connection");
        Check(petitions == 0, "explicit VPN permission bypasses the petition without granting other applications");
        b.PeerStore!.AddOrUpdate([IPAddress.Loopback], [(ushort)a.LocalBoundPort], a.CurrentPeer.CertHash, save: true);
        b.PeerStore.SetAutomaticPermission(a.CurrentPeer.CertHash, "vpn", true);
        var consent = bController.Configuration; consent.Peers[0].AutoAccept = false;
        bController.SaveConfiguration(consent, consent.Revision);
        Check(!b.HasAutomaticProtocolPermission(a.CurrentPeer, bHandler.ProtocolId), "explicit VPN consent overrides an older saved automatic grant");
        consent = bController.Configuration; consent.Peers[0].AutoAccept = true; bController.SaveConfiguration(consent, consent.Revision);
        byte[] streamPacket = VpnPolicyTests.Packet(aHandler.LocalIp.ToString(), bHandler.LocalIp.ToString(), 1235);
        byte[] streamFrame = new byte[streamPacket.Length + 2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(streamFrame, (ushort)streamPacket.Length);
        streamPacket.CopyTo(streamFrame, 2);
        await aHandler.ActivePeers.Values.Single().Stream.WriteAsync(streamFrame);
        await aHandler.ActivePeers.Values.Single().Stream.FlushAsync();
        await Wait(() => bSession!.Received.Any(p => p.SequenceEqual(streamPacket)), "stream fallback uses the same IPv4 policy");
        var remote = bHandler.ActivePeers.Values.Single();
        int received = bSession!.Received.Count;
        bHandler.ProcessIncomingPacket(remote, VpnPolicyTests.Packet("10.203.99.99", bHandler.LocalIp.ToString()));
        Check(bSession.Received.Count == received && remote.DroppedPackets > 0, "authenticated peer cannot spoof an unauthorized source");
        bHandler.ProcessIncomingPacket(remote, VpnPolicyTests.Packet(aHandler.LocalIp.ToString(), "192.168.1.10"));
        Check(bSession.Received.Count == received, "simple LAN does not expose the physical network");
        aController.PausePeer(b.CurrentPeer.Id); aHandler.DisconnectPeer(b.CurrentPeer.Id);
        await Wait(() => !aHandler.HasActivePeer(b.CurrentPeer.Id) && !bHandler.HasActivePeer(a.CurrentPeer.Id), "manual disconnect closes both VPN sessions");
        await Task.Delay(4500);
        Check(!aHandler.HasActivePeer(b.CurrentPeer.Id), "paused VPN does not immediately reconnect");
        Check(aHandler.IncomingSessionPolicy(b.CurrentPeer) == false, "paused VPN rejects the other side's automatic attempts too");
        Volatile.Write(ref acceptedAt, 0);
        aController.ResumePeer(b.CurrentPeer.Id);
        await Wait(() => aHandler.HasActivePeer(b.CurrentPeer.Id) && bHandler.HasActivePeer(a.CurrentPeer.Id), "resuming restores the VPN automatically");
        byte[] resumedPacket = VpnPolicyTests.Packet(aHandler.LocalIp.ToString(), bHandler.LocalIp.ToString(), 1236);
        aSession!.Capture(resumedPacket);
        await Wait(() => bSession!.Received.Any(p => p.SequenceEqual(resumedPacket)), "the first IPv4 packet arrives after reconnecting");
        LogFirstIpv4("Reconnect");
        var advanced = bController.Configuration; advanced.Mode = "advanced";
        advanced.AutoAssign = false; advanced.LocalAddress = bHandler.LocalIp.ToString(); advanced.SubnetMask = "255.255.255.255";
        advanced.Peers[0].AccessibleDestinations = [];
        bController.SaveConfiguration(advanced, advanced.Revision);
        await Wait(() => aHandler.HasActivePeer(b.CurrentPeer.Id) && bHandler.HasActivePeer(a.CurrentPeer.Id), "advanced policy applies and reconnects after configuration change");
        Check(bSession!.Routes.Contains(aHandler.LocalIp + "/32"), "manual /32 interface installs a route to the peer's authorized virtual address");
        int beforeUnsolicited = bSession!.Received.Count;
        bHandler.ProcessIncomingPacket(bHandler.ActivePeers.Values.Single(), packet);
        Check(bSession.Received.Count == beforeUnsolicited, "advanced empty destination ACL blocks unsolicited local access");
        byte[] request = VpnPolicyTests.Packet(bHandler.LocalIp.ToString(), aHandler.LocalIp.ToString(), 2345, 8080);
        bSession.Capture(request);
        await Wait(() => aSession!.Received.Any(p => p.SequenceEqual(request)), "advanced peer can initiate a permitted outgoing flow");
        await Task.Delay(1200);
        byte[] response = VpnPolicyTests.Packet(aHandler.LocalIp.ToString(), bHandler.LocalIp.ToString(), 8080, 2345);
        aSession!.Capture(response);
        await Wait(() => bSession!.Received.Any(p => p.SequenceEqual(response)), "authenticated flow replies return without granting unsolicited local access");
        var failedSession = bSession; failedSession!.FailCapture = true;
        await Wait(() => !ReferenceEquals(bSession, failedSession) && bHandler.IsActive, "orchestrator recreates an adapter whose capture loop failed");
        await Wait(() => aHandler.HasActivePeer(b.CurrentPeer.Id) && bHandler.HasActivePeer(a.CurrentPeer.Id), "VPN reconnects after adapter recovery");
        var disabled = bController.Configuration; disabled.Enabled = false; bController.SaveConfiguration(disabled, disabled.Revision);
        await Wait(() => !bHandler.IsActive && !aHandler.HasActivePeer(b.CurrentPeer.Id), "disabling the receiver revokes the live VPN");
        Check(bHandler.IncomingSessionPolicy(a.CurrentPeer) == false, "disabled VPN cannot be resurrected by an incoming session");

        void LogFirstIpv4(string phase)
        {
            long accepted = Volatile.Read(ref acceptedAt);
            long receivedAt = Volatile.Read(ref bSession!.LastReceivedAt);
            Check(accepted > 0 && receivedAt >= accepted, "receiving adapter observes IPv4 after VPN acceptance");
            Console.WriteLine($"[VPN] {phase}, acceptance to first IPv4: {Stopwatch.GetElapsedTime(accepted, receivedAt).TotalMilliseconds:F1} ms.");
        }
    }
}
