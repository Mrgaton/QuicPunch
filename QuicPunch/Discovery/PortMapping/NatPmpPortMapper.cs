using System;
using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Discovery.PortMapping;

/// <summary>
/// Client implementation of the NAT Port Mapping Protocol (NAT-PMP, RFC 6886).
/// Operates directly over UDP port 5351 targeting the local default gateway.
/// </summary>
public sealed class NatPmpPortMapper : IPortMapper
{
    public const int NatPmpPort = 5351;
    public string ProtocolName => "NAT-PMP (RFC 6886)";

    private readonly IPAddress? _gatewayAddress;
    private int _disposed;

    public NatPmpPortMapper(IPAddress? gatewayAddress = null)
    {
        _gatewayAddress = gatewayAddress ?? GetDefaultGateway();
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

        var gateway = _gatewayAddress ?? GetDefaultGateway();
        if (gateway == null)
            return PortMappingResult.Failed(ProtocolName, "No IPv4 default gateway found.");

        byte opcode = protocol switch
        {
            ProtocolType.Udp => 1,
            ProtocolType.Tcp => 2,
            _ => throw new NotSupportedException($"Protocol {protocol} is not supported by NAT-PMP.")
        };

        uint requestedLifetimeSec = (uint)(lifetime?.TotalSeconds ?? 7200);

        // Build 12-byte request:
        // [0] Vers (0)
        // [1] OP (1=UDP, 2=TCP)
        // [2-3] Reserved (0)
        // [4-5] Internal Port (big-endian)
        // [6-7] Suggested External Port (big-endian)
        // [8-11] Requested Lifetime in seconds (big-endian)
        byte[] request = new byte[12];
        request[0] = 0;
        request[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)internalPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), (ushort)requestedExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8, 4), requestedLifetimeSec);

        try
        {
            byte[]? response = await SendAndReceiveAsync(gateway, request, 16, ct).ConfigureAwait(false);
            if (response == null || response.Length < 16)
                return PortMappingResult.Failed(ProtocolName, "Gateway did not respond to NAT-PMP request.");

            byte respVers = response[0];
            byte respOp = response[1];
            ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));

            if (respVers != 0 || respOp != (opcode + 128))
                return PortMappingResult.Failed(ProtocolName, $"Unexpected NAT-PMP response header (v={respVers}, op={respOp}).");

            if (resultCode != 0)
                return PortMappingResult.Failed(ProtocolName, $"NAT-PMP returned error code {resultCode} ({GetResultCodeDescription(resultCode)}).");

            ushort mappedInternalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8, 2));
            ushort mappedExternalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10, 2));
            uint assignedLifetimeSec = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(12, 4));

            var externalIp = await GetExternalIpAddressAsync(ct).ConfigureAwait(false);

            return PortMappingResult.Succeeded(
                ProtocolName,
                externalIp,
                mappedExternalPort,
                mappedInternalPort,
                TimeSpan.FromSeconds(assignedLifetimeSec));
        }
        catch (Exception ex)
        {
            return PortMappingResult.Failed(ProtocolName, $"NAT-PMP mapping error: {ex.Message}");
        }
    }

    public async Task<bool> TryUnmapPortAsync(
        int externalPort,
        ProtocolType protocol = ProtocolType.Udp,
        CancellationToken ct = default)
    {
        var gateway = _gatewayAddress ?? GetDefaultGateway();
        if (gateway == null) return false;

        byte opcode = protocol == ProtocolType.Tcp ? (byte)2 : (byte)1;

        // Lifetime 0 deletes the mapping
        byte[] request = new byte[12];
        request[0] = 0;
        request[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)externalPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8, 4), 0);

        try
        {
            byte[]? response = await SendAndReceiveAsync(gateway, request, 16, ct).ConfigureAwait(false);
            if (response == null || response.Length < 16) return false;

            ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
            return resultCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default)
    {
        var gateway = _gatewayAddress ?? GetDefaultGateway();
        if (gateway == null) return null;

        // Opcode 0: Request external IP address (2 bytes: [0x00, 0x00])
        byte[] request = [0x00, 0x00];

        try
        {
            byte[]? response = await SendAndReceiveAsync(gateway, request, 12, ct).ConfigureAwait(false);
            if (response == null || response.Length < 12) return null;

            byte respOp = response[1];
            ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));

            if (respOp == 128 && resultCode == 0)
            {
                byte[] ipBytes = response[8..12];
                return new IPAddress(ipBytes);
            }
        }
        catch { }

        return null;
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

        var targetEp = new IPEndPoint(gateway, NatPmpPort);
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

    public static IPAddress? GetDefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(ip));
        }
        catch
        {
            return null;
        }
    }

    private static string GetResultCodeDescription(ushort code) => code switch
    {
        0 => "Success",
        1 => "Unsupported Version",
        2 => "Not Authorized / Refused",
        3 => "Network Failure",
        4 => "Out of Resources",
        5 => "Unsupported Opcode",
        _ => $"Unknown ({code})"
    };

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
