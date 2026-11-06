using System.Buffers;
using System.Buffers.Binary;
using QuicPunch.Vpn.Routing;

namespace QuicPunch.Vpn.Broker;

public static class VpnBrokerHost
{
    public static async Task RunAsync(string path, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var revision = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path)) {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            watcher.Changed += (_, _) => revision.Cancel(); watcher.Renamed += (_, _) => revision.Cancel();
            watcher.Deleted += (_, _) => revision.Cancel(); watcher.EnableRaisingEvents = true;
            var config = VpnBrokerConfiguration.Load(path);
            try { await BrokerTransport.ServeAsync(config, stream => ServeSessionAsync(path, stream, revision.Token), revision.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && revision.IsCancellationRequested) { }
            if (!ct.IsCancellationRequested) await Task.Delay(250, ct);
        }
    }
    private static async Task ServeSessionAsync(string path, Stream stream, CancellationToken ct)
    {
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var request = await BrokerWire.ReadJsonAsync<BrokerRequest>(stream, handshake.Token);
            // Reload the administrator-owned file for every session. Clients cannot select files, commands or adapters.
            var config = VpnBrokerConfiguration.Load(path);
            config.ValidateRequest(request);
            var packetPolicy = new BrokerPacketPolicy(request);
            using var adapter = VpnAdapterFactory.Create(config.AdapterName);
            using var routes = new VpnRouteManager(adapter.Name, handshake.Token);
            routes.ProtectTransportEndpoints(request.BypassAddresses ?? []);
            adapter.SetIpAddress(System.Net.IPAddress.Parse(request.Address), request.Mask);
            adapter.SetMtu(request.Mtu);
            using var forwarding = new VpnForwardingManager(adapter.Name, request.Forwarding);
            routes.Install(request.Routes);
            using var session = adapter.StartSession();
            await BrokerWire.WriteJsonAsync(stream, new BrokerResponse(true, null), handshake.Token);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sending = Task.Run(async () => {
                byte[] bytes = ArrayPool<byte>.Shared.Rent(65535);
                byte[] header = new byte[2];
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (!session.WaitForPacket(100, stop.Token))
                        {
                            if (!session.IsHealthy) throw new IOException("VPN capture session failed.");
                            continue;
                        }
                        int length = session.ReceivePacket(bytes);
                        if (length <= 0) continue;
                        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)length);
                        await stream.WriteAsync(header, stop.Token);
                        await stream.WriteAsync(bytes.AsMemory(0, length), stop.Token);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(bytes); }
            }, stop.Token);
            byte[] rented = ArrayPool<byte>.Shared.Rent(65535);
            try
            {
                byte[] header = new byte[2];
                while (!ct.IsCancellationRequested)
                {
                    // Stop reading if the capture loop fails; no abandoned client can hold the adapter indefinitely.
                    var read = stream.ReadExactlyAsync(header, stop.Token).AsTask();
                    if (await Task.WhenAny(read, sending) == sending) { await sending; break; }
                    await read;
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
                    if (length < 20) throw new InvalidDataException("Invalid packet length.");
                    await stream.ReadExactlyAsync(rented.AsMemory(0, length), stop.Token);
                    if (!packetPolicy.Accept(rented.AsSpan(0, length))) continue;
                    session.SendPacket(rented.AsSpan(0, length));
                }
            }
            finally
            {
                stop.Cancel();
                stream.Dispose();
                try { await sending; } catch { }
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception ex)
        {
            // Disconnect and shutdown can race: EOF during stop is still a normal cancellation.
            ct.ThrowIfCancellationRequested();
            try { using var response = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await BrokerWire.WriteJsonAsync(stream, new BrokerResponse(false, ex.Message), response.Token); } catch { }
        }
    }
    internal static bool ValidatePacket(ReadOnlySpan<byte> bytes, BrokerRequest request)
        => new BrokerPacketPolicy(request).Accept(bytes);
}

/// <summary>Compiled once per broker session; parsing and allocations stay outside the packet path.</summary>
internal sealed class BrokerPacketPolicy
{
    private readonly uint _local, _broadcast;
    private readonly bool _allowBroadcast;
    private readonly bool _hasDirectedBroadcast;
    private readonly (uint Network, uint Mask)[] _destinations;
    internal BrokerPacketPolicy(BrokerRequest request)
    {
        _local = Address(request.Address);
        uint mask = Address(request.Mask);
        _broadcast = (_local & mask) | ~mask;
        _hasDirectedBroadcast = System.Numerics.BitOperations.PopCount(mask) < 31;
        _allowBroadcast = request.Broadcast;
        _destinations = (request.InboundDestinations ?? []).Select(prefix => {
            string[] parts = prefix.Split('/'); int length = int.Parse(parts[1]);
            uint prefixMask = length == 0 ? 0 : uint.MaxValue << (32 - length);
            return (Address(parts[0]) & prefixMask, prefixMask);
        }).ToArray();
    }
    private static uint Address(string value) => BinaryPrimitives.ReadUInt32BigEndian(System.Net.IPAddress.Parse(value).GetAddressBytes());
    internal bool Accept(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || bytes[0] >> 4 != 4) return false;
        int header = (bytes[0] & 15) * 4;
        if (header < 20 || header > bytes.Length || BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]) != bytes.Length || bytes[8] == 0) return false;
        for (int position = 20; position < header;)
        {
            byte option = bytes[position];
            if (option == 0) break;
            if (option == 1) { position++; continue; }
            if (option is 131 or 137 || position + 1 >= header || bytes[position + 1] < 2 || position + bytes[position + 1] > header) return false;
            position += bytes[position + 1];
        }
        uint sum = 0; for (int i = 0; i < header; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i, 2));
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        if (sum != 65535) return false;
        uint destination = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        if (destination == uint.MaxValue || destination is >= 0xE0000000 and <= 0xEFFFFFFF || (_hasDirectedBroadcast && destination == _broadcast)) return _allowBroadcast;
        if (destination == _local) return true;
        foreach (var prefix in _destinations)
        {
            if ((destination & prefix.Mask) == prefix.Network) return true;
        }
        return false;
    }
}
