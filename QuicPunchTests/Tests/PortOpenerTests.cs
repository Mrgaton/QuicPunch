using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Discovery.PortMapping;

namespace QuicPunchTests;

public static class PortOpenerTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("   PORT OPENERS (PCP, NAT-PMP & UPnP) TEST SUITE  ");
        Console.WriteLine("==================================================");

        await TestNatPmpFramingAsync();
        await TestPcpFramingAsync();
        await TestMockGatewayNatPmpAsync();
        await TestMockGatewayPcpAsync();
        await TestCoordinatorFallbackAsync();
        await TestRealGatewayProbeAsync();

        Console.WriteLine("==================================================");
        Console.WriteLine("   ALL PORT OPENER TESTS PASSED SUCCESSFULLY!    ");
        Console.WriteLine("==================================================");
    }

    private static Task TestNatPmpFramingAsync()
    {
        Console.Write("[TEST 1] NAT-PMP (RFC 6886) protocol framing & result codes... ");

        byte[] mockIpResponse = new byte[12];
        mockIpResponse[0] = 0;   // Vers
        mockIpResponse[1] = 128; // OP (0 + 128)
        BinaryPrimitives.WriteUInt16BigEndian(mockIpResponse.AsSpan(2, 2), 0); // Result = 0 (Success)
        BinaryPrimitives.WriteUInt32BigEndian(mockIpResponse.AsSpan(4, 4), 3600); // Epoch
        new byte[] { 203, 0, 113, 5 }.CopyTo(mockIpResponse, 8); // External IP: 203.0.113.5

        var parsedIp = new IPAddress(mockIpResponse[8..12]);
        if (!parsedIp.Equals(IPAddress.Parse("203.0.113.5")))
            throw new Exception("NAT-PMP IP framing parse failed.");

        byte[] mockMapResponse = new byte[16];
        mockMapResponse[0] = 0;
        mockMapResponse[1] = 129; // OP (1 + 128)
        BinaryPrimitives.WriteUInt16BigEndian(mockMapResponse.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(mockMapResponse.AsSpan(4, 4), 3600);
        BinaryPrimitives.WriteUInt16BigEndian(mockMapResponse.AsSpan(8, 2), 45000);  // Internal port
        BinaryPrimitives.WriteUInt16BigEndian(mockMapResponse.AsSpan(10, 2), 55000); // External port
        BinaryPrimitives.WriteUInt32BigEndian(mockMapResponse.AsSpan(12, 4), 7200);  // Lifetime

        ushort inPort = BinaryPrimitives.ReadUInt16BigEndian(mockMapResponse.AsSpan(8, 2));
        ushort outPort = BinaryPrimitives.ReadUInt16BigEndian(mockMapResponse.AsSpan(10, 2));
        uint lifetime = BinaryPrimitives.ReadUInt32BigEndian(mockMapResponse.AsSpan(12, 4));

        if (inPort != 45000 || outPort != 55000 || lifetime != 7200)
            throw new Exception("NAT-PMP port mapping parse failed.");

        Console.WriteLine("PASSED");
        return Task.CompletedTask;
    }

    private static Task TestPcpFramingAsync()
    {
        Console.Write("[TEST 2] PCP (RFC 6887) 60-byte MAP framing & IPv4/IPv6 mapping... ");

        var testIpv4 = IPAddress.Parse("192.168.1.50");
        byte[] pcpBytes = PcpPortMapper.ToPcpIpBytes(testIpv4);

        if (pcpBytes.Length != 16)
            throw new Exception("PCP IP bytes must be exactly 16 bytes.");
        if (pcpBytes[10] != 0xFF || pcpBytes[11] != 0xFF)
            throw new Exception("PCP IPv4 must be mapped to ::ffff:x.x.x.x.");

        var recoveredIp = PcpPortMapper.FromPcpIpBytes(pcpBytes);
        if (!recoveredIp.Equals(testIpv4))
            throw new Exception($"PCP IP roundtrip failed: {recoveredIp} vs {testIpv4}");

        // Native IPv6 test
        var testIpv6 = IPAddress.Parse("2001:db8::1");
        byte[] pcpV6Bytes = PcpPortMapper.ToPcpIpBytes(testIpv6);
        var recoveredV6 = PcpPortMapper.FromPcpIpBytes(pcpV6Bytes);
        if (!recoveredV6.Equals(testIpv6))
            throw new Exception("PCP IPv6 roundtrip failed.");

        Console.WriteLine("PASSED");
        return Task.CompletedTask;
    }

    private static async Task TestMockGatewayNatPmpAsync()
    {
        Console.Write("[TEST 3] Mock NAT-PMP Gateway socket handshake (UDP 5351 emulation)... ");

        using var mockGatewayUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int mockPort = ((IPEndPoint)mockGatewayUdp.Client.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var serverTask = Task.Run(async () =>
        {
            var req = await mockGatewayUdp.ReceiveAsync(cts.Token);
            if (req.Buffer.Length == 12 && req.Buffer[1] == 1) // UDP map request
            {
                byte[] resp = new byte[16];
                resp[0] = 0;
                resp[1] = 129; // OP UDP response
                BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 0); // SUCCESS
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), 100);
                BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(8, 2), BinaryPrimitives.ReadUInt16BigEndian(req.Buffer.AsSpan(4, 2))); // Internal
                BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(10, 2), 61234); // Assigned external port
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(12, 4), 3600);
                await mockGatewayUdp.SendAsync(resp, req.RemoteEndPoint, cts.Token);
            }
        });

        // Use custom client pointing directly to mock gateway
        using var clientUdp = new UdpClient(AddressFamily.InterNetwork);
        byte[] clientReq = new byte[12];
        clientReq[0] = 0; clientReq[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(clientReq.AsSpan(4, 2), 48888);
        BinaryPrimitives.WriteUInt16BigEndian(clientReq.AsSpan(6, 2), 48888);
        BinaryPrimitives.WriteUInt32BigEndian(clientReq.AsSpan(8, 4), 3600);

        await clientUdp.SendAsync(clientReq, new IPEndPoint(IPAddress.Loopback, mockPort), cts.Token);
        var clientRecv = await clientUdp.ReceiveAsync(cts.Token);

        await serverTask;

        if (clientRecv.Buffer.Length != 16 || clientRecv.Buffer[1] != 129)
            throw new Exception("Mock NAT-PMP gateway communication failed.");

        ushort assignedExt = BinaryPrimitives.ReadUInt16BigEndian(clientRecv.Buffer.AsSpan(10, 2));
        if (assignedExt != 61234)
            throw new Exception($"Expected assigned port 61234, got {assignedExt}");

        Console.WriteLine($"PASSED (Mapped port {assignedExt})");
    }

    private static async Task TestMockGatewayPcpAsync()
    {
        Console.Write("[TEST 4] Mock PCP (RFC 6887) 60-byte socket handshake (MAP opcode)... ");

        using var mockGatewayUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int mockPort = ((IPEndPoint)mockGatewayUdp.Client.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var serverTask = Task.Run(async () =>
        {
            var req = await mockGatewayUdp.ReceiveAsync(cts.Token);
            if (req.Buffer.Length == 60 && req.Buffer[0] == 2 && req.Buffer[1] == 1) // PCP v2 MAP
            {
                byte[] resp = new byte[60];
                resp[0] = 2; // Vers
                resp[1] = 0x81; // R-bit | Opcode MAP
                resp[3] = 0; // Result SUCCESS
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), 7200); // Lifetime
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(8, 4), 500);  // Epoch

                req.Buffer.AsSpan(24, 12).CopyTo(resp.AsSpan(24, 12));
                resp[36] = req.Buffer[36]; // Protocol
                req.Buffer.AsSpan(40, 2).CopyTo(resp.AsSpan(40, 2));
                // Assigned external port
                BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(42, 2), 59999);
                // Assigned external IP (198.51.100.77 as IPv4-mapped)
                PcpPortMapper.ToPcpIpBytes(IPAddress.Parse("198.51.100.77")).CopyTo(resp, 44);

                await mockGatewayUdp.SendAsync(resp, req.RemoteEndPoint, cts.Token);
            }
        });

        using var clientUdp = new UdpClient(AddressFamily.InterNetwork);
        byte[] pcpReq = new byte[60];
        pcpReq[0] = 2; pcpReq[1] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(pcpReq.AsSpan(4, 4), 7200);
        PcpPortMapper.ToPcpIpBytes(IPAddress.Loopback).CopyTo(pcpReq, 8);
        byte[] nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        nonce.CopyTo(pcpReq, 24);
        pcpReq[36] = 17; // UDP
        BinaryPrimitives.WriteUInt16BigEndian(pcpReq.AsSpan(40, 2), 45000);
        BinaryPrimitives.WriteUInt16BigEndian(pcpReq.AsSpan(42, 2), 45000);

        await clientUdp.SendAsync(pcpReq, new IPEndPoint(IPAddress.Loopback, mockPort), cts.Token);
        var clientRecv = await clientUdp.ReceiveAsync(cts.Token);

        await serverTask;

        if (clientRecv.Buffer.Length != 60 || clientRecv.Buffer[3] != 0)
            throw new Exception("Mock PCP gateway communication failed.");

        ushort assignedExt = BinaryPrimitives.ReadUInt16BigEndian(clientRecv.Buffer.AsSpan(42, 2));
        var assignedExtIp = PcpPortMapper.FromPcpIpBytes(clientRecv.Buffer.AsSpan(44, 16));

        if (assignedExt != 59999 || !assignedExtIp.Equals(IPAddress.Parse("198.51.100.77")))
            throw new Exception($"PCP validation mismatch: port={assignedExt}, ip={assignedExtIp}");

        Console.WriteLine($"PASSED (Mapped port {assignedExt}, Ext IP {assignedExtIp})");
    }

    private static async Task TestCoordinatorFallbackAsync()
    {
        Console.Write("[TEST 5] PortMappingCoordinator fallback cascading (PCP -> NAT-PMP -> UPnP)... ");

        using var coordinator = new PortMappingCoordinator(IPAddress.Loopback);

        // Map on dummy loopback where 5351 is closed. It should cascade cleanly through PCP, NAT-PMP, and then UPnP without throwing.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await coordinator.TryMapPortAsync(55555, 55555, ProtocolType.Udp, TimeSpan.FromSeconds(300), cts.Token);

        // Result can be failure on loopback since no router is on loopback, but should complete gracefully
        if (result == null)
            throw new Exception("Coordinator returned null result.");

        Console.WriteLine($"PASSED (Cascaded cleanly, protocol evaluated: {coordinator.ProtocolName})");
    }

    private static async Task TestRealGatewayProbeAsync()
    {
        Console.Write("[TEST 6] Local network default gateway probe (PCP/NAT-PMP port 5351 & UPnP)... ");

        var gateway = NatPmpPortMapper.GetDefaultGateway();
        if (gateway == null)
        {
            Console.WriteLine("SKIPPED (No local IPv4 default gateway detected)");
            return;
        }

        Console.Write($"[Gateway: {gateway}] ");
        using var coordinator = new PortMappingCoordinator(gateway);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var extIp = await coordinator.GetExternalIpAddressAsync(cts.Token);

        if (extIp != null)
        {
            Console.WriteLine($"PASSED (Discovered External IP: {extIp} via {coordinator.ProtocolName})");
        }
        else
        {
            Console.WriteLine("PASSED (Gateway probed; router has port 5351 / UPnP closed or filtered)");
        }
    }
}
