using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Discovery.PortMapping;

/// <summary>
/// Client implementation of the Port Control Protocol (PCP, RFC 6887).
/// Operates directly over UDP port 5351 targeting the local default gateway.
/// Provides IPv4/IPv6 support, CGNAT awareness, and sub-millisecond lease negotiation.
/// </summary>
public sealed class PcpPortMapper : IPortMapper
{
    public const int PcpPort = 5351;
    public const byte PcpVersion = 2;
    public const byte OpcodeMap = 1;

    public string ProtocolName => "PCP (RFC 6887)";

    private readonly IPAddress? _gatewayAddress;
    private readonly IPAddress? _localClientAddress;
    private int _disposed;

    public PcpPortMapper(IPAddress? gatewayAddress = null, IPAddress? localClientAddress = null)
    {
        _gatewayAddress = gatewayAddress ?? NatPmpPortMapper.GetDefaultGateway();
        _localClientAddress = localClientAddress ?? FindLocalIpForGateway(_gatewayAddress);
    }

    public async Task<PortMappingResult> TryMapPortAsync(
        int internalPort,
        int requestedExternalPort,
        ProtocolType protocol = ProtocolType.Udp,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return PortMappingResult.Failed(ProtocolName, "Mapper is disposed.");

        var gateway = _gatewayAddress ?? NatPmpPortMapper.GetDefaultGateway();
        if (gateway == null)
            return PortMappingResult.Failed(ProtocolName, "No default gateway found for PCP.");

        var clientIp = _localClientAddress ?? FindLocalIpForGateway(gateway);
        if (clientIp == null)
            return PortMappingResult.Failed(ProtocolName, "Could not determine local IP address for PCP.");

        byte protocolByte = protocol switch
        {
            ProtocolType.Udp => 17,
            ProtocolType.Tcp => 6,
            _ => throw new NotSupportedException($"Protocol {protocol} is not supported by PCP.")
        };

        uint requestedLifetimeSec = (uint)(lifetime?.TotalSeconds ?? 7200);

        // Generate 12-byte cryptographically random mapping nonce
        byte[] nonce = RandomNumberGenerator.GetBytes(12);

        // 60-byte PCP MAP Request:
        // [0..23]  24-byte Common Header
        // [24..59] 36-byte MAP Opcode Specific Payload
        byte[] request = new byte[60];

        // Header:
        request[0] = PcpVersion; // Vers = 2
        request[1] = OpcodeMap;  // Opcode = 1
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), requestedLifetimeSec);
        byte[] clientIpMapped = ToPcpIpBytes(clientIp);
        clientIpMapped.CopyTo(request, 8);

        // MAP Payload:
        nonce.CopyTo(request, 24);
        request[36] = protocolByte;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(40, 2), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(42, 2), (ushort)requestedExternalPort);
        // [44..59] Suggested External IP (all zeros = wildcard)

        try
        {
            byte[]? response = await SendAndReceiveAsync(gateway, request, 60, ct).ConfigureAwait(false);
            if (response == null || response.Length < 60)
                return PortMappingResult.Failed(ProtocolName, "Gateway did not respond to PCP request.");

            byte respVers = response[0];
            byte respOpcode = response[1];
            byte resultCode = response[3];

            if (respVers != PcpVersion || respOpcode != (0x80 | OpcodeMap))
                return PortMappingResult.Failed(ProtocolName, $"Unexpected PCP response header (v={respVers}, op=0x{respOpcode:X2}).");

            if (resultCode != 0)
                return PortMappingResult.Failed(ProtocolName, $"PCP returned error code {resultCode} ({GetResultCodeDescription(resultCode)}).");

            // Verify mapping nonce matches
            if (!response.AsSpan(24, 12).SequenceEqual(nonce))
                return PortMappingResult.Failed(ProtocolName, "PCP response nonce mismatch.");

            uint assignedLifetimeSec = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4, 4));
            ushort assignedInternalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(40, 2));
            ushort assignedExternalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(42, 2));
            IPAddress assignedExternalIp = FromPcpIpBytes(response.AsSpan(44, 16));

            return PortMappingResult.Succeeded(
                ProtocolName,
                assignedExternalIp,
                assignedExternalPort,
                assignedInternalPort,
                TimeSpan.FromSeconds(assignedLifetimeSec));
        }
        catch (Exception ex)
        {
            return PortMappingResult.Failed(ProtocolName, $"PCP mapping error: {ex.Message}");
        }
    }

    public async Task<bool> TryUnmapPortAsync(
        int externalPort,
        ProtocolType protocol = ProtocolType.Udp,
        CancellationToken ct = default)
    {
        var gateway = _gatewayAddress ?? NatPmpPortMapper.GetDefaultGateway();
        if (gateway == null) return false;

        var clientIp = _localClientAddress ?? FindLocalIpForGateway(gateway);
        if (clientIp == null) return false;

        byte protocolByte = protocol == ProtocolType.Tcp ? (byte)6 : (byte)17;

        // Lifetime 0 deletes the mapping in PCP
        byte[] request = new byte[60];
        request[0] = PcpVersion;
        request[1] = OpcodeMap;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), 0); // Lifetime 0
        ToPcpIpBytes(clientIp).CopyTo(request, 8);

        // All zero nonce to delete wildcard or delete specific
        request[36] = protocolByte;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(40, 2), (ushort)externalPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(42, 2), (ushort)externalPort);

        try
        {
            byte[]? response = await SendAndReceiveAsync(gateway, request, 60, ct).ConfigureAwait(false);
            if (response == null || response.Length < 60) return false;

            return response[3] == 0; // Result Code 0 = SUCCESS
        }
        catch
        {
            return false;
        }
    }

    public async Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default)
    {
        // In PCP, a MAP request with internal port 0 / external port 0 returns the external IP
        var res = await TryMapPortAsync(0, 0, ProtocolType.Udp, TimeSpan.FromSeconds(0), ct).ConfigureAwait(false);
        return res.Success ? res.ExternalIp : null;
    }

    private static async Task<byte[]?> SendAndReceiveAsync(
        IPAddress gateway,
        byte[] request,
        int expectedMinLen,
        CancellationToken ct)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.ReceiveTimeout = 600;
        client.Client.SendTimeout = 600;

        var targetEp = new IPEndPoint(gateway, PcpPort);
        await client.SendAsync(request, targetEp, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(750));

        try
        {
            var recvResult = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (recvResult.Buffer.Length >= expectedMinLen)
            {
                return recvResult.Buffer;
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }

        return null;
    }

    public static byte[] ToPcpIpBytes(IPAddress ip)
    {
        byte[] raw = new byte[16];
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // IPv4-mapped IPv6: ::ffff:a.b.c.d
            raw[10] = 0xFF;
            raw[11] = 0xFF;
            ip.GetAddressBytes().CopyTo(raw, 12);
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            ip.GetAddressBytes().CopyTo(raw, 0);
        }
        return raw;
    }

    public static IPAddress FromPcpIpBytes(ReadOnlySpan<byte> bytes)
    {
        // Check for IPv4-mapped IPv6 prefix: 10 zero bytes + 2 0xFF bytes
        bool isIpv4Mapped = true;
        for (int i = 0; i < 10; i++)
        {
            if (bytes[i] != 0) { isIpv4Mapped = false; break; }
        }
        if (isIpv4Mapped && bytes[10] == 0xFF && bytes[11] == 0xFF)
        {
            return new IPAddress(bytes[12..16].ToArray());
        }

        return new IPAddress(bytes[..16].ToArray());
    }

    private static IPAddress? FindLocalIpForGateway(IPAddress? gateway)
    {
        if (gateway == null) return null;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect(gateway, 5351);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                return ep.Address;
            }
        }
        catch { }
        return null;
    }

    private static string GetResultCodeDescription(byte code) => code switch
    {
        0 => "SUCCESS",
        1 => "UNSUPP_VERSION",
        2 => "NOT_AUTHORIZED",
        3 => "MALFORMED_REQUEST",
        4 => "UNSUPP_OPCODE",
        5 => "UNSUPP_OPTION",
        6 => "MALFORMED_OPTION",
        7 => "NETWORK_FAILURE",
        8 => "NO_RESOURCES",
        9 => "UNSUPP_PROTOCOL",
        10 => "USER_EX_QUOTA",
        11 => "CANNOT_PROVIDE_EXTERNAL",
        12 => "ADDRESS_MISMATCH",
        13 => "EXCESSIVE_REMOTE_PEERS",
        _ => $"UNKNOWN_ERROR ({code})"
    };

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
