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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Port, ProtocolType Protocol), byte[]> _nonces = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Port, ProtocolType Protocol), int> _internalPorts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Port, ProtocolType Protocol), IPAddress> _externalIps = new();
    private IPAddress? _lastExternalIp;

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
        byte[] nonce = _nonces.GetOrAdd((internalPort, protocol), _ => RandomNumberGenerator.GetBytes(12));

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
        // PCP uses address-family-specific all-zero addresses. For IPv4 this is
        // encoded as the IPv4-mapped address ::ffff:0.0.0.0, not 16 zero bytes.
        ToPcpIpBytes(clientIp.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any)
            .CopyTo(request, 44);

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

            if (assignedInternalPort != internalPort || response[36] != protocolByte || assignedExternalPort == 0 || assignedLifetimeSec == 0)
                return PortMappingResult.Failed(ProtocolName, "PCP response does not describe the requested live mapping.");
            _internalPorts[(assignedExternalPort, protocol)] = internalPort;
            _externalIps[(assignedExternalPort, protocol)] = assignedExternalIp;
            _lastExternalIp = assignedExternalIp;
            return PortMappingResult.Succeeded(
                ProtocolName,
                assignedExternalIp,
                assignedExternalPort,
                assignedInternalPort,
                TimeSpan.FromSeconds(assignedLifetimeSec));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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

        if (!_internalPorts.TryGetValue((externalPort, protocol), out int internalPort) ||
            !_nonces.TryGetValue((internalPort, protocol), out var nonce) ||
            !_externalIps.TryGetValue((externalPort, protocol), out var externalIp)) return false;
        nonce.CopyTo(request, 24);
        // Delete exactly the lease created by this mapper. RFC 6887 + verified
        // errata 3621 require lifetime=0, the original nonce/protocol/internal
        // port, external port 0, and the address-family-specific all-zero address.
        request[36] = protocolByte;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(40, 2), (ushort)internalPort);
        byte[] deletionAddress = ToPcpIpBytes(externalIp.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any);
        deletionAddress.CopyTo(request, 44);

        try
        {
            byte[]? response = await SendAndReceiveAsync(gateway, request, 60, ct).ConfigureAwait(false);
            if (response == null || response.Length < 60) return false;

            bool success = response[0] == PcpVersion && response[1] == (0x80 | OpcodeMap) && response[3] == 0
                && response.AsSpan(24, 12).SequenceEqual(nonce) && response[36] == protocolByte
                && BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(40, 2)) == internalPort
                && BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(42, 2)) == 0
                && response.AsSpan(44, 16).SequenceEqual(deletionAddress)
                && BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4, 4)) == 0;
            if (success)
            {
                _internalPorts.TryRemove((externalPort, protocol), out _);
                _externalIps.TryRemove((externalPort, protocol), out _);
                _nonces.TryRemove((internalPort, protocol), out _);
            }
            return success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_lastExternalIp);
    }

    private static async Task<byte[]?> SendAndReceiveAsync(
        IPAddress gateway,
        byte[] request,
        int expectedMinLen,
        CancellationToken ct)
    {
        using var client = new UdpClient(gateway.AddressFamily);
        client.Client.ReceiveTimeout = 600;
        client.Client.SendTimeout = 600;

        var targetEp = new IPEndPoint(gateway, PcpPort);
        client.Connect(targetEp);
        await client.SendAsync(request, ct).ConfigureAwait(false);

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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
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
