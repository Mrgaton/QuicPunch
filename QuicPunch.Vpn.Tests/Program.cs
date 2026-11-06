using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using QuicPunch.Vpn;
using QuicPunch.Vpn.Broker;
using QuicPunch.Vpn.Linux;
using QuicPunch.Vpn.Routing;

namespace QuicPunch.Vpn.Tests;

internal static class Program
{
    [DllImport("libc", SetLastError = true)]
    private static extern int socketpair(int domain, int type, int protocol, [Out] int[] fds);

    private static async Task Main(string[] args)
    {
        int clientArgument = Array.IndexOf(args, "--broker-client");
        if (clientArgument >= 0) { await BrokerClient(args[clientArgument + 1]); return; }
        ValidateConfiguration();
        if (!OperatingSystem.IsLinux()) { Console.WriteLine("[SKIP] Linux kernel tests require Linux."); return; }
        await SessionTests();
        if (!args.Contains("--integration"))
        {
            Console.WriteLine("[SKIP] Real TUN/broker tests require --integration inside an isolated network/mount namespace (see scripts/test-vpn-linux.sh).");
            return;
        }
        Check(Environment.GetEnvironmentVariable("QUICNET_VPN_TEST_ISOLATED") == "1", "Use scripts/test-vpn-linux.sh for isolated integration tests.");
        Check(LinuxVpnAdapter.HasNetworkAdministration, "Integration requires CAP_NET_ADMIN.");
        await AdapterTests();
        if (!args.Contains("--kernel-only")) await BrokerTests();
        Console.WriteLine("[PASS] Linux VPN integration completed.");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static void ValidateConfiguration()
    {
        foreach (string name in new[] { "", "abcdefghijklmnop", "a b", "a/b", "a:b", "tun%d", "ñ", ".", "..", "a\0" })
            Reject<ArgumentException>(() => LinuxVpnAdapter.ValidateName(name));
        LinuxVpnAdapter.ValidateName("qp-tun0");
        foreach (var (mask, expected) in new[] { ("0.0.0.0", 0), ("255.255.255.0", 24), ("255.255.255.254", 31), ("255.255.255.255", 32) })
            Check(LinuxVpnAdapter.PrefixLength(IPAddress.Parse("10.77.0.1"), mask) == expected, "Incorrect prefix length.");
        foreach (string mask in new[] { "255.0.255.0", "0.0.0.1", "::", "garbage" })
            Reject<ArgumentException>(() => LinuxVpnAdapter.PrefixLength(IPAddress.Parse("10.77.0.1"), mask));
        Reject<ArgumentException>(() => LinuxVpnAdapter.PrefixLength(IPAddress.IPv6Loopback, "255.255.255.0"));
        Console.WriteLine("[PASS] Interface names, IPv4 addresses and contiguous masks validated before OS operations.");
    }

    private static (LinuxVpnSession Session, Socket Peer) Pair()
    {
        int[] fds = new int[2];
        if (socketpair(1, 2 | LinuxTunApi.O_NONBLOCK | LinuxTunApi.O_CLOEXEC, 0, fds) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (new LinuxVpnSession(fds[0]), new Socket(new SafeSocketHandle((nint)fds[1], ownsHandle: true)));
    }
    private static async Task SessionTests()
    {
        var (session, peer) = Pair();
        using (session) using (peer)
        {
            byte[] bytes = new byte[65535];
            Check(session.ReceivePacket(bytes) == 0 && !session.WaitForPacket(0), "Empty queue must not block.");
            Reject<ArgumentOutOfRangeException>(() => session.WaitForPacket(-2));
            Reject<ArgumentException>(() => session.SendPacket(new byte[65536]));
            byte[] packet = UdpPacket("10.77.0.2", "10.77.0.1", 12345, 12346, [1, 2, 3]);
            peer.Send(packet);
            Check(session.WaitForPacket(1000), "Packet readiness lost.");
            int size = session.ReceivePacket(bytes);
            Check(packet.AsSpan().SequenceEqual(bytes.AsSpan(0, size)), "Read corrupts packet.");
            session.SendPacket(packet);
            Check(peer.Receive(bytes) == packet.Length && packet.AsSpan().SequenceEqual(bytes.AsSpan(0, packet.Length)), "Write corrupts packet.");
            long allocated = 0;
            for (int i = 0; i < 1000; i++)
            {
                peer.Send(packet);
                long before = GC.GetAllocatedBytesForCurrentThread();
                session.ReceivePacket(bytes);
                session.SendPacket(packet);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                peer.Receive(bytes);
            }
            Check(allocated == 0, $"Packet I/O allocated {allocated} managed bytes.");
            Console.WriteLine("[PASS] 1000 read/write pairs allocate zero managed bytes in LinuxVpnSession.");
            using var cancel = new CancellationTokenSource(80);
            var elapsed = Stopwatch.StartNew();
            Check(!session.WaitForPacket(-1, cancel.Token) && elapsed.ElapsedMilliseconds < 1000, "Cancellation must interrupt poll.");
            // A full nonblocking receive queue drops packets without failing a healthy VPN.
            for (int i = 0; i < 1000; i++) session.SendPacket(packet);
            Check(session.IsHealthy, "Backpressure must not tear down a session.");
            while (peer.Available > 0) peer.Receive(bytes);
            Console.WriteLine("[PASS] Nonblocking packet I/O, cancellation and saturated queues.");
        }
        var (waiting, idlePeer) = Pair();
        using (waiting) using (idlePeer)
        {
            using var started = new ManualResetEventSlim();
            var poll = Task.Run(() => { started.Set(); Reject<ObjectDisposedException>(() => waiting.WaitForPacket(-1)); });
            started.Wait();
            await Task.Delay(30);
            waiting.Dispose();
            await poll.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!waiting.IsHealthy, "Disposed session still healthy.");
        }
        // A pipe EOF produces POLLHUP; unlike a timeout it must stop/recover the session.
        var pipe = new int[2];
        Check(Pipe(pipe, LinuxTunApi.O_NONBLOCK | LinuxTunApi.O_CLOEXEC) == 0, "pipe2 failed.");
        using var broken = new LinuxVpnSession(pipe[0]);
        LinuxTunApi.Close(pipe[1]);
        Reject<IOException>(() => broken.WaitForPacket(100));
        Check(!broken.IsHealthy, "POLLHUP must mark the session unhealthy.");
        Console.WriteLine("[PASS] Concurrent disposal and fatal capture failures are observable.");
    }
    [DllImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    private static extern int Pipe([Out] int[] fds, int flags);

    private static async Task AdapterTests()
    {
        const string name = "qptest-tun";
        using (var adapter = new LinuxVpnAdapter(name))
        {
            Reject<Win32Exception>(() => { using var duplicate = new LinuxVpnAdapter(name); });
            adapter.SetIpAddress(IPAddress.Parse("10.77.0.1"), "255.255.255.0");
            adapter.SetMtu(1420);
            using var session = adapter.StartSession();
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            udp.Bind(new IPEndPoint(IPAddress.Parse("10.77.0.1"), 0));
            await Exchange(session, udp, "10.77.0.1", "10.77.0.2");
            udp.SendTo(new byte[] { 7, 8, 9 }, new IPEndPoint(IPAddress.Parse("10.77.0.255"), 12345));
            Receive(session, "10.77.0.255");
            using var json = JsonDocument.Parse(VpnCommand.Run("ip", "-j", "address", "show", "dev", name));
            Check(json.RootElement[0].GetProperty("mtu").GetInt32() == 1420, "MTU not applied.");
            Check(json.RootElement[0].GetProperty("addr_info")[0].GetProperty("broadcast").GetString() == "10.77.0.255", "Directed broadcast not configured.");
            adapter.SetIpAddress(IPAddress.Parse("10.78.0.1"), "255.255.255.255");
            using var changed = JsonDocument.Parse(VpnCommand.Run("ip", "-j", "-4", "address", "show", "dev", name));
            Check(changed.RootElement[0].GetProperty("addr_info").GetArrayLength() == 1, "Address change left the old connected subnet installed.");
            using (var routes = new VpnRouteManager(name))
            {
                routes.Install(["198.51.100.0/24"]);
                using var route = JsonDocument.Parse(VpnCommand.Run("ip", "-j", "route", "get", "198.51.100.1"));
                Check(route.RootElement[0].GetProperty("dev").GetString() == name, "Route not installed.");
            }
            Check(VpnCommand.Run("ip", "route", "show", "exact", "198.51.100.0/24").Trim().Length == 0, "Owned route not removed.");
        }
        Check(!Directory.Exists("/sys/class/net/" + name), "TUN was not removed after disposal.");
        using (var repeated = new LinuxVpnAdapter(name)) { }
        Console.WriteLine("[PASS] Real TUN UDP capture/injection, broadcast, MTU, /32 readdressing, routes and cleanup.");
        VpnCommand.Run("ip", "link", "add", "qptest-phy", "type", "dummy");
        try
        {
            VpnCommand.Run("ip", "address", "add", "192.0.2.1/24", "dev", "qptest-phy");
            VpnCommand.Run("ip", "link", "set", "qptest-phy", "up");
            VpnCommand.Run("ip", "route", "add", "default", "via", "192.0.2.254", "dev", "qptest-phy");
            using var adapter = new LinuxVpnAdapter(name);
            adapter.SetIpAddress(IPAddress.Parse("10.77.0.1"), "255.255.255.0"); adapter.SetMtu(1420);
            using (var routes = new VpnRouteManager(name))
            {
                routes.ProtectTransportEndpoints(["203.0.113.5"]);
                routes.Install(["0.0.0.0/0"]);
                using var physical = JsonDocument.Parse(VpnCommand.Run("ip", "-j", "route", "get", "203.0.113.5"));
                Check(physical.RootElement[0].GetProperty("dev").GetString() == "qptest-phy", "QUIC endpoint entered its own VPN.");
                using var tunneled = JsonDocument.Parse(VpnCommand.Run("ip", "-j", "route", "get", "203.0.113.6"));
                Check(tunneled.RootElement[0].GetProperty("dev").GetString() == name, "Default VPN route not installed.");
            }
            Check(VpnCommand.Run("ip", "route", "show", "exact", "203.0.113.5/32").Trim().Length == 0, "Bypass route leaked.");
            Check(VpnCommand.Run("ip", "route", "show", "default").Contains("qptest-phy"), "Physical route was removed.");
        }
        finally { VpnCommand.Run("ip", "link", "del", "qptest-phy"); }
        Console.WriteLine("[PASS] Default VPN routing preserves QUIC endpoint bypasses and physical routes.");
        string originalForwarding = File.ReadAllText("/proc/sys/net/ipv4/ip_forward").Trim();
        using (var forwarding = new VpnForwardingManager(name, true))
            Check(File.ReadAllText("/proc/sys/net/ipv4/ip_forward").Trim() == "1", "Forwarding not enabled.");
        Check(File.ReadAllText("/proc/sys/net/ipv4/ip_forward").Trim() == originalForwarding, "Forwarding state not restored.");
        Console.WriteLine("[PASS] Explicit IPv4 forwarding is enabled and its previous state restored.");
    }

    private static async Task Exchange(IVpnSession session, Socket udp, string local, string remote)
    {
        byte[] payload = [19, 31, 77, 99];
        udp.SendTo(payload, new IPEndPoint(IPAddress.Parse(remote), 12345));
        Receive(session, remote);
        int port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        session.SendPacket(UdpPacket(remote, local, 12345, port, payload));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] response = new byte[100];
        int length = await udp.ReceiveAsync(response, SocketFlags.None, deadline.Token);
        Check(payload.AsSpan().SequenceEqual(response.AsSpan(0, length)), "Injected UDP packet did not reach the OS socket.");
    }
    private static void Receive(IVpnSession session, string destination)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        byte[] packet = new byte[65535];
        while (!deadline.IsCancellationRequested)
        {
            if (!session.WaitForPacket(100, deadline.Token)) continue;
            int size = session.ReceivePacket(packet);
            if (size >= 28 && packet[0] >> 4 == 4 && new IPAddress(packet.AsSpan(16, 4)).ToString() == destination) return;
        }
        throw new Exception("TUN did not capture IPv4 to " + destination);
    }
    private static byte[] UdpPacket(string source, string destination, int sourcePort, int destinationPort, byte[] payload)
    {
        byte[] packet = new byte[28 + payload.Length];
        packet[0] = 0x45; packet[8] = 64; packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        uint sum = 0;
        for (int i = 0; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(i));
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10), (ushort)~sum);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), (ushort)(8 + payload.Length));
        payload.CopyTo(packet, 28);
        return packet;
    }

    private static async Task BrokerTests()
    {
        // The test script mounts a private /run so neither this socket nor any service can affect the host.
        string policy = Path.Combine(Path.GetTempPath(), "quicnet-vpn-policy-" + Guid.NewGuid().ToString("N") + ".json");
        // nobody's UID is distribution policy, not a Linux ABI constant.
        string owner = VpnCommand.Run("id", "-u", "nobody").Trim();
        Check(uint.TryParse(owner, out uint uid) && uid != 0, "Tests require an unprivileged nobody account.");
        await File.WriteAllTextAsync(policy, JsonSerializer.Serialize(new VpnBrokerConfiguration { Owner = owner }, BrokerWire.Json));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = VpnBrokerHost.RunAsync(policy, stop.Token);
        try
        {
            while (!File.Exists(BrokerTransport.SocketPath(owner))) { await Task.Delay(20, stop.Token); if (host.IsCompleted) await host; }
            // root has filesystem access, but SO_PEERCRED must reject its UID and keep accepting.
            using (var unauthorized = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await unauthorized.ConnectAsync(new UnixDomainSocketEndPoint(BrokerTransport.SocketPath(owner)), stop.Token);
                byte[] closed = new byte[1];
                Check(await unauthorized.ReceiveAsync(closed, SocketFlags.None, stop.Token) == 0, "Wrong UID accepted by broker.");
            }
            var info = new ProcessStartInfo("runuser") { UseShellExecute = false };
            foreach (string argument in new[] { "-u", "nobody", "--", Environment.ProcessPath!, typeof(Program).Assembly.Location, "--broker-client", owner }) info.ArgumentList.Add(argument);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var client = Process.Start(info) ?? throw new Exception("Cannot launch unprivileged client.");
                await client.WaitForExitAsync(stop.Token);
                Check(client.ExitCode == 0, "Unprivileged broker client failed.");
                while (Directory.Exists("/sys/class/net/qp-tun0")) await Task.Delay(20, stop.Token);
            }
        }
        finally
        {
            stop.Cancel();
            try { await host; } catch (OperationCanceledException) { }
            File.Delete(policy);
        }
        Check(!File.Exists(BrokerTransport.SocketPath(owner)), "Broker socket not removed.");
        Console.WriteLine("[PASS] Root broker authenticates UID, survives a rejected client, serves unprivileged TUN packets and cleans up.");
    }
    private static async Task BrokerClient(string owner)
    {
        Check(BrokerTransport.CurrentOwner == owner && !LinuxVpnAdapter.HasNetworkAdministration, "Broker client must be unprivileged.");
        Check(BrokerVpnAdapter.IsAvailable, "Unprivileged application cannot discover the broker socket.");
        using var adapter = new BrokerVpnAdapter();
        adapter.SetIpAddress(IPAddress.Parse("10.0.0.1"), "255.255.255.0");
        adapter.SetMtu(1420); adapter.SetAccess(["10.0.0.1/32"], true);
        using var session = adapter.StartSession();
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 0));
        await Exchange(session, udp, "10.0.0.1", "10.0.0.2");
        Console.WriteLine("[PASS] Unprivileged application exchanges real packets through the root broker.");
    }
}
