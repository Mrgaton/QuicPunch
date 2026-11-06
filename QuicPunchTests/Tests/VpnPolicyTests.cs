using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using QuicNet.Vpn;
using QuicPunch.Vpn.Broker;

namespace QuicPunchTests.Tests;

internal static class VpnPolicyTests
{
    private const string A = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string B = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("[PASS] " + label); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidDataException) { Check(true, label); return; }
        throw new Exception(label);
    }
    public static Task RunAsync()
    {
        for (int i = 0; i < 1000; i++)
        {
            byte[] identity = System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(i));
            var ip = QuicNet.Protocols.VirtualLanHandler.ComputeAutomaticIp(identity);
            byte[] bytes = ip.GetAddressBytes();
            if (bytes[0] != 10 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] is < 2 or > 251 ||
                !ip.Equals(QuicNet.Protocols.VirtualLanHandler.ComputeAutomaticIp(identity)))
                throw new Exception("Automatic LAN address must be stable and within 10.0.0.2–251.");
        }
        var automatic = new VpnConfiguration { SubnetMask = "255.255.0.0" };
        Reject(() => VpnConfigurationStore.Validate(new() { Peers = [new() { CertHash = A, Mode = "unknown" }] }), "unknown peer mode rejected");
        VpnConfigurationStore.Validate(automatic);
        Check(automatic.SubnetMask == "255.255.255.0", "automatic LAN uses one /24 even when loading the previous wider mask");
        uint local = Ipv4Prefix.Address("10.203.0.1");
        var gateway = new VpnPeerPolicy(new() { CertHash = A, VirtualAddress = "10.203.0.2",
            AllowedSources = ["0.0.0.0/0"], Routes = ["0.0.0.0/0"], AccessibleDestinations = ["10.203.0.1/32"] }, "10.203.0.2", local, false);
        var subnet = new VpnPeerPolicy(new() { CertHash = B, VirtualAddress = "10.203.0.3", AllowedSources = ["192.168.50.0/24"],
            Routes = ["192.168.50.0/24"], AccessibleDestinations = ["192.168.1.10/32"] }, "10.203.0.3", local, false);
        var policy = new VpnPolicy(local, [gateway, subnet]);
        Check(policy.Route(Ipv4Prefix.Address("192.168.50.99")) == B, "most-specific subnet wins over default route, even without an active session");
        Check(policy.Route(Ipv4Prefix.Address("8.8.8.8")) == A, "default gateway selection");
        Check(!policy.AcceptSource(A, subnet.Address) && !policy.AcceptSource(A, Ipv4Prefix.Address("192.168.50.99")), "gateway cannot spoof another peer or its subnet");
        Check(!policy.AcceptSource(A, local) && !policy.AcceptSource(A, Ipv4Prefix.Address("127.0.0.1")), "local and loopback source impersonation blocked");
        Check(policy.AcceptSource(B, Ipv4Prefix.Address("192.168.50.9")) && !policy.AcceptSource(B, Ipv4Prefix.Address("192.168.51.9")), "remote network source authorization");
        Check(policy.AcceptDestination(B, Ipv4Prefix.Address("192.168.1.10"), false) && !policy.AcceptDestination(B, local, false), "destination ACL independent of routes");
        Check(!policy.AcceptDestination(B, uint.MaxValue, true), "advanced broadcast disabled unless explicitly authorized");
        var paused = new VpnPeerPolicy(new() { CertHash = B, Paused = true, VirtualAddress = "10.203.0.3",
            Routes = ["192.168.50.0/24"], AllowedSources = ["192.168.50.0/24"], AccessibleDestinations = ["10.203.0.1/32"] }, "10.203.0.3", local, false);
        var pausedPolicy = new VpnPolicy(local, [gateway, paused]);
        Check(!pausedPolicy.AcceptSource(B, paused.Address) && !pausedPolicy.AcceptDestination(B, local, false), "pausing revokes traffic in both directions");
        Check(pausedPolicy.Route(Ipv4Prefix.Address("192.168.50.99")) == B, "paused subnet cannot fall through to a default gateway");
        var simple = new VpnPeerPolicy(new() { CertHash = B, VirtualAddress = "10.203.0.3" }, "10.203.0.3", local, true);
        Check(simple.Broadcast && !simple.Forwarding && simple.Destinations.Single().Contains(local), "simple LAN has local access and discovery without forwarding");
        Reject(() => Ipv4Prefix.Parse("10.0.0.1/33"), "invalid prefix rejected");
        Reject(() => Ipv4Prefix.MaskLength("255.0.255.0"), "non-contiguous mask rejected");
        Reject(() => Ipv4Prefix.Address("127.1"), "abbreviated address rejected");
        Check(Ipv4Prefix.Parse("192.168.50.99/24").ToString() == "192.168.50.0/24", "CIDR canonicalization");
        byte[] packet = Packet("10.203.0.1", "10.203.0.2", 45000, 8080);
        Check(Ipv4Packet.TryRead(packet, out var outgoing), "valid IPv4 packet accepted");
        var replies = new VpnReplyTracker(1);
        replies.Record(A, packet, outgoing);
        byte[] reply = Packet("10.203.0.2", "10.203.0.1", 8080, 45000);
        Ipv4Packet.TryRead(reply, out var incoming);
        Check(replies.IsReply(A, reply, incoming) && !replies.IsReply(B, reply, incoming), "return traffic bound to identity and flow");
        byte[] unsolicited = Packet("10.203.0.2", "10.203.0.1", 8081, 45000);
        Ipv4Packet.TryRead(unsolicited, out var other);
        Check(!replies.IsReply(A, unsolicited, other), "unsolicited traffic cannot use a return-path permission");
        byte[] forged = (byte[])reply.Clone(); forged[12] ^= 1;
        Check(!Ipv4Packet.TryRead(forged, out _), "invalid header checksum rejected");
        Check(!Ipv4Packet.TryRead(reply.AsSpan(0, reply.Length - 1), out _), "truncated IPv4 rejected");
        byte[] routed = new byte[packet.Length + 8];
        packet.AsSpan(0, 20).CopyTo(routed); packet.AsSpan(20).CopyTo(routed.AsSpan(28));
        routed[0] = 0x47; new byte[] { 131, 7, 4, 192, 168, 1, 1, 0 }.CopyTo(routed, 20);
        BinaryPrimitives.WriteUInt16BigEndian(routed.AsSpan(2), (ushort)routed.Length);
        routed[10] = routed[11] = 0;
        uint routedSum = 0; for (int i = 0; i < 28; i += 2) routedSum += BinaryPrimitives.ReadUInt16BigEndian(routed.AsSpan(i, 2));
        while (routedSum >> 16 != 0) routedSum = (routedSum & 65535) + (routedSum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(routed.AsSpan(10), (ushort)~routedSum);
        Check(!Ipv4Packet.TryRead(routed, out _) && !VpnBrokerHost.ValidatePacket(routed, new("10.203.0.2", "255.255.0.0", 1420, [])),
            "IPv4 source routing cannot bypass the destination ACL at either boundary");
        replies.Clear(); Check(!replies.IsReply(A, reply, incoming), "policy revocation removes return paths");
        TestStore();
        var admin = new VpnBrokerConfiguration();
        admin.ValidateRequest(new("10.0.0.1", "255.255.255.0", 1420, ["10.0.0.2/32"]));
        Reject(() => admin.ValidateRequest(new("192.168.1.1", "255.255.255.0", 1420, [])), "broker rejects unauthorized interface addresses");
        Reject(() => admin.ValidateRequest(new("10.0.0.1", "255.255.255.0", 1420, ["0.0.0.0/0"])), "broker rejects routes beyond administrator policy");
        Reject(() => admin.ValidateRequest(new("10.0.0.1", "255.255.255.0", 1420, ["10.0.0.0/8"])), "broker checks whole prefixes, not just starting IP");
        Reject(() => admin.ValidateRequest(new("10.0.0.1", "255.255.255.0", 1420, [], Forwarding: true)), "forwarding needs administrator authorization");
        Reject(() => admin.ValidateRequest(new("10.0.0.1", "255.255.255.0", 1420, [], InboundDestinations: ["192.168.1.0/24"])), "administrator destination limit cannot be expanded by the desktop");
        Reject(() => admin.ValidateRequest(new("10.0.0.1", "255.255.255.0", 1420, [], BypassAddresses: ["192.168.1.1"])), "transport exemptions cannot modify unrelated routes");
        Check(VpnBrokerHost.ValidatePacket(Packet("10.203.0.2", "10.203.0.1"), new("10.203.0.1", "255.255.0.0", 1420, [])), "broker accepts valid local IPv4");
        Check(!VpnBrokerHost.ValidatePacket(Packet("10.203.0.2", "192.168.1.1"), new("10.203.0.1", "255.255.0.0", 1420, [])), "broker also filters packets at the privilege boundary");
        Check(VpnBrokerHost.ValidatePacket(Packet("10.203.0.2", "10.203.0.1"), new("10.203.0.1", "255.255.255.255", 1420, [], Broadcast: false)), "manual /32 address is unicast, not a directed broadcast");
        Check(VpnBrokerHost.ValidatePacket(Packet("10.203.0.2", "10.203.0.1"), new("10.203.0.1", "255.255.255.254", 1420, [], Broadcast: false)), "point-to-point /31 has no directed broadcast");
        return Task.CompletedTask;
    }

    private static void TestStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "quicnet-vpn-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new VpnConfigurationStore(Path.Combine(root, "vpn.json"), new());
            var config = store.Snapshot(); config.Mode = "advanced";
            store.Save(config, config.Revision);
            Reject(() => store.Save(config, config.Revision), "stale UI cannot overwrite a newer revision");
            File.WriteAllText(store.Path, "{broken json}");
            store.Reload(); Check(store.Snapshot().Mode == "advanced" && store.LastError != null, "invalid external edit retains the last valid configuration");
            var edited = store.Snapshot(); edited.AutoConnect = false;
            File.WriteAllText(store.Path, JsonSerializer.Serialize(edited, VpnConfigurationStore.JsonOptions));
            store.Reload(); Check(!store.Snapshot().AutoConnect && store.LastError == null, "valid external edit reloads");
            var snapshot = store.Snapshot(); snapshot.Mode = "simple";
            Check(store.Snapshot().Mode == "advanced", "callers cannot mutate the live configuration through a snapshot");
            var duplicate = new VpnConfiguration { Peers = [new() { CertHash = A, Routes = ["192.168.0.0/24"] }, new() { CertHash = B, Routes = ["192.168.0.7/24"] }] };
            Reject(() => VpnConfigurationStore.Validate(duplicate), "ambiguous canonical routes rejected before saving");
            var oversized = store.Snapshot();
            oversized.Peers = Enumerable.Range(0, 1024).Select(i => new VpnPeerConfiguration { CertHash = i.ToString("X64") }).ToList();
            Reject(() => store.Save(oversized, oversized.Revision), "oversized configuration is rejected before writing a file the watcher cannot reload");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    internal static byte[] Packet(string source, string destination, ushort sourcePort = 1234, ushort destinationPort = 8080)
    {
        byte[] bytes = new byte[28]; bytes[0] = 0x45; bytes[8] = 64; bytes[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)bytes.Length);
        IPAddress.Parse(source).GetAddressBytes().CopyTo(bytes, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(bytes, 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 8);
        uint sum = 0; for (int i = 0; i < 20; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i, 2));
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), (ushort)~sum);
        return bytes;
    }
}
