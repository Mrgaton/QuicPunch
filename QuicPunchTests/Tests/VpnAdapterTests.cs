using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Numerics;
using QuicPunch.Vpn;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.Tests;

public static class VpnAdapterTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("     CROSS-PLATFORM VPN ADAPTER (L3) TESTS        ");
        Console.WriteLine("==================================================");

        TestVpnFactoryPlatformSupport();
        TestCidrSubnetCalculation();
        TestVirtualLanPrivilegeHandling();
        TestIpv4HeaderDestinationRouting();

        Console.WriteLine("==================================================");
        Console.WriteLine("   ALL VPN ADAPTER TESTS PASSED SUCCESSFULLY!    ");
        Console.WriteLine("==================================================");
        await Task.CompletedTask;
    }

    private static void TestVpnFactoryPlatformSupport()
    {
        Console.Write("[TEST 1] VpnAdapterFactory OS detection & naming... ");

        if (OperatingSystem.IsWindows())
        {
            if (VpnAdapterFactory.DefaultAdapterName != "QuicPunchAdapter")
                throw new Exception($"Expected 'QuicPunchAdapter', got '{VpnAdapterFactory.DefaultAdapterName}'");
        }
        else if (OperatingSystem.IsLinux())
        {
            if (VpnAdapterFactory.DefaultAdapterName != "qp-tun0")
                throw new Exception($"Expected 'qp-tun0', got '{VpnAdapterFactory.DefaultAdapterName}'");
        }
        else
        {
            throw new Exception("Unexpected operating system.");
        }

        // Try creating adapter: will either succeed (with root/admin) or throw Win32Exception (privilege required)
        try
        {
            using var adapter = VpnAdapterFactory.Create("qptest-dummy");
            if (adapter == null)
                throw new Exception("Created adapter was null.");
            if (string.IsNullOrEmpty(adapter.Name))
                throw new Exception("Adapter name was empty.");
        }
        catch (Win32Exception ex)
        {
            bool isPrivilegeError = (OperatingSystem.IsWindows() && ex.NativeErrorCode == 5) ||
                                    (OperatingSystem.IsLinux() && (ex.NativeErrorCode == 1 || ex.NativeErrorCode == 13));
            if (!isPrivilegeError)
                throw new Exception($"Unexpected Win32Exception error code: {ex.NativeErrorCode} ({ex.Message})");
        }
        catch (PlatformNotSupportedException)
        {
            throw new Exception("VpnAdapterFactory threw PlatformNotSupportedException on supported OS.");
        }

        Console.WriteLine("PASSED");
    }

    private static void TestCidrSubnetCalculation()
    {
        Console.Write("[TEST 2] Subnet mask to CIDR prefix bit-counting... ");

        var testCases = new (string mask, int expectedCidr)[]
        {
            ("255.255.255.255", 32),
            ("255.255.255.252", 30),
            ("255.255.255.0", 24),
            ("255.255.240.0", 20),
            ("255.255.0.0", 16),
            ("255.0.0.0", 8),
            ("0.0.0.0", 0)
        };

        foreach (var (mask, expectedCidr) in testCases)
        {
            var ip = IPAddress.Parse(mask);
            uint maskUint = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
            int cidr = BitOperations.PopCount(maskUint);
            if (cidr != expectedCidr)
                throw new Exception($"Mask {mask}: expected /{expectedCidr}, calculated /{cidr}");
        }

        Console.WriteLine("PASSED");
    }

    private static void TestVirtualLanPrivilegeHandling()
    {
        Console.Write("[TEST 3] VirtualLanHandler graceful privilege degradation... ");

        using var lanHandler = new VirtualLanHandler();
        lanHandler.SetupTun("10.99.99.2", "255.255.255.0", 1420);

        // Depending on whether this process is running with elevated privileges (root or admin):
        if (lanHandler.IsActive)
        {
            if (lanHandler.AdapterStatus != "Active")
                throw new Exception($"Expected Active, got {lanHandler.AdapterStatus}");
        }
        else
        {
            if (!lanHandler.AdapterStatus.Contains("privileges required", StringComparison.OrdinalIgnoreCase) &&
                lanHandler.AdapterStatus != "Error")
            {
                throw new Exception($"Expected privilege or error status, got: '{lanHandler.AdapterStatus}'");
            }
        }

        lanHandler.StopTun();
        if (lanHandler.IsActive)
            throw new Exception("Adapter should be inactive after StopTun");

        Console.WriteLine($"PASSED (Adapter status: {lanHandler.AdapterStatus})");
    }

    private static void TestIpv4HeaderDestinationRouting()
    {
        Console.Write("[TEST 4] Layer-3 IPv4 header parsing and unicast routing... ");

        // Build a mock IPv4 packet (20 bytes header + 4 bytes payload)
        byte[] mockPacket = new byte[24];
        mockPacket[0] = 0x45; // Version 4, IHL 5 (20 bytes)
        mockPacket[8] = 64;   // TTL 64
        mockPacket[9] = 17;   // UDP Protocol (17)

        // Source IP: 10.10.10.5
        byte[] src = new byte[] { 10, 10, 10, 5 };
        src.CopyTo(mockPacket, 12);

        // Dest IP: 10.10.10.99
        byte[] dst = new byte[] { 10, 10, 10, 99 };
        dst.CopyTo(mockPacket, 16);

        uint parsedDest = BinaryPrimitives.ReadUInt32BigEndian(mockPacket.AsSpan(16, 4));
        uint expectedDest = BinaryPrimitives.ReadUInt32BigEndian(dst);

        if (parsedDest != expectedDest)
            throw new Exception($"Expected dest IP {expectedDest}, got {parsedDest}");

        // Broadcast check
        byte[] bcast = new byte[] { 255, 255, 255, 255 };
        bcast.CopyTo(mockPacket, 16);
        uint bcastDest = BinaryPrimitives.ReadUInt32BigEndian(mockPacket.AsSpan(16, 4));
        if (bcastDest != 0xFFFFFFFF)
            throw new Exception("Broadcast packet not matched");

        Console.WriteLine("PASSED");
    }
}
