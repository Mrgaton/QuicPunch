using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace QuicPunch.Vpn.Broker;

public interface IVpnRouteAdapter
{
    void SetRoutes(string[] prefixes);
    void SetTransportEndpoints(string[] addresses) { }
    void SetForwarding(bool enabled) { }
    void SetAccess(string[] destinations, bool broadcast) { }
}

/// <summary>Unprivileged packet transport to the administrator-installed adapter broker.</summary>
public sealed class BrokerVpnAdapter : IVpnAdapter, IVpnRouteAdapter
{
    private BrokerVpnSession? _session;
    private string[] _routes = [];
    private string[] _endpoints = [];
    private bool _forwarding;
    private string[] _destinations = [];
    private bool _broadcast;
    public string Name => VpnAdapterFactory.DefaultAdapterName;
    public bool IsActive => _session?.IsHealthy == true;
    public IPAddress? LocalIp { get; private set; }
    public string? SubnetMask { get; private set; }
    public int Mtu { get; private set; } = 1420;
    public static bool IsAvailable => OperatingSystem.IsWindows() ? WindowsBrokerService.IsRunning :
        OperatingSystem.IsLinux() && File.Exists(BrokerTransport.SocketPath(BrokerTransport.CurrentOwner));
    public void SetIpAddress(IPAddress ip, string subnetMask) { LocalIp = ip; SubnetMask = subnetMask; }
    public void SetMtu(int mtu) => Mtu = mtu;
    public void SetRoutes(string[] prefixes) => _routes = prefixes;
    public void SetTransportEndpoints(string[] addresses) => _endpoints = addresses;
    public void SetForwarding(bool enabled) => _forwarding = enabled;
    public void SetAccess(string[] destinations, bool broadcast) { _destinations = destinations; _broadcast = broadcast; }
    public IVpnSession StartSession(uint bufferCapacity = 0x400000 * 4)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stream = BrokerTransport.ConnectAsync(deadline.Token).GetAwaiter().GetResult();
        try
        {
            BrokerWire.WriteJsonAsync(stream, new BrokerRequest(LocalIp!.ToString(), SubnetMask!, Mtu, _routes, _endpoints, _forwarding, _destinations, _broadcast), deadline.Token).GetAwaiter().GetResult();
            var response = BrokerWire.ReadJsonAsync<BrokerResponse>(stream, deadline.Token).GetAwaiter().GetResult();
            if (!response.Success) throw new UnauthorizedAccessException(response.Error);
            return _session = new BrokerVpnSession(stream);
        }
        catch { stream.Dispose(); throw; }
    }
    public void Close() => Dispose();
    public void Dispose() { _session?.Dispose(); _session = null; }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

internal sealed class BrokerVpnSession : IVpnSession
{
    private readonly Stream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _receiveLock = new(), _sendLock = new();
    private readonly Queue<(byte[] Buffer, int Length)> _packets = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Task _reader;
    private Exception? _error;
    private int _disposed;
    public bool IsHealthy => Volatile.Read(ref _disposed) == 0 && !_reader.IsCompleted;
    internal BrokerVpnSession(Stream stream) { _stream = stream; _reader = Task.Run(ReadAsync); }
    private async Task ReadAsync()
    {
        byte[] header = new byte[2];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _stop.Token);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(header);
                if (length < 20) throw new InvalidDataException("Invalid broker packet length.");
                byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
                try { await _stream.ReadExactlyAsync(bytes.AsMemory(0, length), _stop.Token); }
                catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
                lock (_receiveLock)
                {
                    if (Volatile.Read(ref _disposed) != 0) { ArrayPool<byte>.Shared.Return(bytes); return; }
                    if (_packets.Count == 512) ArrayPool<byte>.Shared.Return(_packets.Dequeue().Buffer);
                    _packets.Enqueue((bytes, length));
                    _ready.Set();
                }
            }
        }
        catch (Exception ex) { _error = ex; }
        finally { _ready.Set(); }
    }
    public bool WaitForPacket(int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_error != null) throw new IOException("VPN broker disconnected.", _error);
        return _ready.Wait(timeoutMilliseconds, cancellationToken);
    }
    public int ReceivePacket(Span<byte> destination)
    {
        lock (_receiveLock)
        {
            if (!_packets.TryDequeue(out var packet)) { _ready.Reset(); return 0; }
            try { packet.Buffer.AsSpan(0, packet.Length).CopyTo(destination); return packet.Length; }
            finally { ArrayPool<byte>.Shared.Return(packet.Buffer); if (_packets.Count == 0) _ready.Reset(); }
        }
    }
    public void SendPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 20 or > 65535) throw new InvalidDataException("Invalid packet size.");
        lock (_sendLock)
        {
            byte[] frame = ArrayPool<byte>.Shared.Rent(packet.Length + 2);
            try
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)packet.Length);
                packet.CopyTo(frame.AsSpan(2));
                _stop.CancelAfter(TimeSpan.FromSeconds(5));
                _stream.WriteAsync(frame.AsMemory(0, packet.Length + 2), _stop.Token).AsTask().GetAwaiter().GetResult();
                _stop.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            finally { ArrayPool<byte>.Shared.Return(frame); }
        }
    }
    public void Close() => Dispose();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _stream.Dispose();
        try { _reader.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_receiveLock) { while (_packets.TryDequeue(out var p)) ArrayPool<byte>.Shared.Return(p.Buffer); }
        _ready.Set();
        if (_reader.IsCompleted) _ready.Dispose();
        _stop.Dispose();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
