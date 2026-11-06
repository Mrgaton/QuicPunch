using System.Buffers.Binary;
using System.IO.Pipes;
using QuicPunch.Vpn.Broker;
using QuicPunch.Vpn.Routing;

namespace QuicPunchTests.Tests;

internal static class VpnBrokerTests
{
    public static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            var physical = VpnRouteManager.InspectWindowsRoute(System.Net.IPAddress.Loopback);
            if (physical.InterfaceIndex == 0 || string.IsNullOrEmpty(physical.InterfaceName) || !System.Net.IPAddress.TryParse(physical.Gateway, out _))
                throw new Exception("Native physical route inspection returned an invalid route.");
            Console.WriteLine("[PASS] physical IPv4 route is inspected through the native API without modifying the routing table");
        }
        string name = "quicnet-vpn-test-" + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token); await accept;
        using var session = new BrokerVpnSession(client);
        byte[] outgoing = VpnPolicyTests.Packet("10.203.0.2", "10.203.0.1");
        var sending = Task.Run(() => session.SendPacket(outgoing), deadline.Token);
        byte[] header = new byte[2]; await server.ReadExactlyAsync(header, deadline.Token);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        byte[] received = new byte[length]; await server.ReadExactlyAsync(received, deadline.Token);
        await sending;
        if (!outgoing.SequenceEqual(received)) throw new Exception("Broker corrupts outbound IPv4.");
        Console.WriteLine("[PASS] unprivileged broker client frames outbound packets correctly");
        byte[] capture = VpnPolicyTests.Packet("10.203.0.1", "10.203.0.2");
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)capture.Length);
        // Exercise partial pipe reads, not just complete frames.
        await server.WriteAsync(header.AsMemory(0, 1), deadline.Token);
        await server.WriteAsync(header.AsMemory(1, 1), deadline.Token);
        await server.WriteAsync(capture.AsMemory(0, 7), deadline.Token);
        await server.WriteAsync(capture.AsMemory(7), deadline.Token);
        while (!session.WaitForPacket(10, deadline.Token)) { }
        byte[] buffer = new byte[65535]; int actual = session.ReceivePacket(buffer);
        if (!capture.AsSpan().SequenceEqual(buffer.AsSpan(0, actual))) throw new Exception("Broker corrupts partial packet reads.");
        Console.WriteLine("[PASS] partial broker frames are reassembled and delivered to the capture loop");
        server.Dispose();
        while (session.IsHealthy) await Task.Delay(10, deadline.Token);
        try { session.WaitForPacket(10); throw new Exception("Disconnected broker must fail visibly."); }
        catch (IOException) { Console.WriteLine("[PASS] broker disconnect is visible and recoverable instead of silently stalling"); }
        session.Dispose(); session.Dispose();
        Console.WriteLine("[PASS] broker disposal is idempotent");
    }
}
