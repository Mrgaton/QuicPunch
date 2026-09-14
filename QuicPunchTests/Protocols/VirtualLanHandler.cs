using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;
using QuicPunch.Vpn;

namespace QuicPunchTests.Protocols;

internal sealed class VirtualLanHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public ushort PreferredPort => 0;
    public string ProtocolName => "LAN Bridge";
    public ushort StreamPriority => (ushort)QuicPunch.Helpers.QuicStreamPriority.High;
    public ZstandardCompressionOptions? CompressionOptions => null;

    private IPAddress _localIp = IPAddress.Parse("10.10.10.2");
    private uint _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse("10.10.10.2").GetAddressBytes());
    public const int DefaultDatagramMtu = 1420; // Maximum safe MTU for RFC 9221 QUIC datagrams over standard 1500-byte links without IP fragmentation
    private string _subnetMask = "255.255.255.0";
    private int _mtu = DefaultDatagramMtu;

    public string AdapterName { get; } = OperatingSystem.IsWindows() ? "QuicPunchAdapter" : "qp-tun0";
    public string AdapterStatus { get; private set; } = "Stopped";
    public string? LastError { get; private set; }
    public IPAddress LocalIp => _localIp;
    public string SubnetMask => _subnetMask;
    public int Mtu => _mtu;
    public bool AutoAssignEnabled { get; private set; } = true;
    public bool IsActive => _session != null;
    public string AddressMode => AutoAssignEnabled ? "Automatic" : "Manual";

    public event Action? StateChanged;

    public long TotalRxPackets;
    public long TotalTxPackets;
    public long TotalRxBytes;
    public long TotalTxBytes;
    public ConcurrentDictionary<uint, PeerLanSession> ActivePeers { get; } = new();

    private IVpnAdapter? _adapter;
    private IVpnSession? _session;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    public static IPAddress ComputeAutomaticIp(byte[] identityHash, byte[]? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(identityHash);
        if (identityHash.Length == 0)
            throw new ArgumentException("Identity hash cannot be empty.", nameof(identityHash));

        byte[] hostDigest = SHA256.HashData(identityHash);
        byte host = (byte)(2 + (hostDigest[0] % 251)); // Range 2..252 (avoiding network .0, gateway .1, broadcast .255)

        // Default to clean, memorable 10.10.10.x subnet; if pool/group specified, map into 10.10.<subnet>.x
        byte subnet = 10;
        if (groupId is { Length: > 0 })
            subnet = (byte)(10 + (SHA256.HashData(groupId)[0] % 240));

        return new IPAddress(new byte[] { 10, 10, subnet, host });
    }

    public void ConfigureAutoAssignment(byte[] identityHash, byte[]? groupId = null)
    {
        AutoAssignEnabled = true;
        SetAddressFields(ComputeAutomaticIp(identityHash, groupId), "255.255.255.0");
    }

    public bool ConfigureManualAddress(string ip, string subnetMask)
    {
        if (!TryParseIpv4(ip, out var parsed) || !TryParseIpv4(subnetMask, out _))
            return false;

        AutoAssignEnabled = false;
        SetAddressFields(parsed, subnetMask);
        return true;
    }

    public bool SetMtu(int mtu)
    {
        if (mtu < 1200 || mtu > 9000)
            return false;
        _mtu = mtu;
        if (mtu > 1500)
        {
            QuicPunch.Helpers.MsQuicTuner.EnsurePathMtuDiscovery(1280, (ushort)Math.Min(mtu, 9000));
        }
        if (_adapter != null)
        {
            try { _adapter.SetMtu(_mtu); }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }
        return true;
    }

    public void SetupTun(string? initialIp = null, string? subnetMask = null, int? mtu = null)
    {
        if (!string.IsNullOrWhiteSpace(initialIp) && TryParseIpv4(initialIp, out var parsed))
            SetAddressFields(parsed, string.IsNullOrWhiteSpace(subnetMask) ? _subnetMask : subnetMask!);
        else if (!string.IsNullOrWhiteSpace(subnetMask))
            _subnetMask = subnetMask!;
        if (mtu.HasValue && !SetMtu(mtu.Value))
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU must be between 1200 and 9000.");

        StopTun();
        try
        {
            Console.WriteLine($"[LAN] Starting VPN adapter ({AdapterName}) at {_localIp}/{_subnetMask}, MTU {_mtu}...");
            _adapter = VpnAdapterFactory.Create(AdapterName, "QuicPunchTunnel");
            _adapter.SetIpAddress(_localIp, _subnetMask);
            _adapter.SetMtu(_mtu);
            _session = _adapter.StartSession();
            AdapterStatus = "Active";
            LastError = null;

            _captureCts = new CancellationTokenSource();
            IVpnSession captureSession = _session;
            _captureTask = Task.Run(() => CaptureVpnAndSendToPeers(captureSession, _captureCts.Token));
        }
        catch (Win32Exception ex)
        {
            LastError = ex.Message;
            bool isPrivilege = (OperatingSystem.IsWindows() && ex.NativeErrorCode == 5) ||
                               (OperatingSystem.IsLinux() && (ex.NativeErrorCode == 1 || ex.NativeErrorCode == 13));
            AdapterStatus = isPrivilege ? "Administrator / root privileges required" : "Error";
            CleanupAdapter();
            Console.WriteLine($"[LAN] VPN initialization failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AdapterStatus = "Error";
            CleanupAdapter();
            Console.WriteLine($"[LAN] VPN initialization failed: {ex.Message}");
        }
        finally
        {
            StateChanged?.Invoke();
        }
    }

    public void StopTun()
    {
        try { _captureCts?.Cancel(); } catch { }
        try { _captureTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _captureTask = null;
        try { _captureCts?.Dispose(); } catch { }
        _captureCts = null;

        foreach (var session in ActivePeers.Values)
            session.Dispose();
        ActivePeers.Clear();

        CleanupAdapter();
        if (AdapterStatus != "Error" && !AdapterStatus.Contains("privileges", StringComparison.OrdinalIgnoreCase))
            AdapterStatus = "Stopped";

        StateChanged?.Invoke();
    }

    private void CleanupAdapter()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        try { _adapter?.Dispose(); } catch { }
        _adapter = null;
    }

    public bool SetVirtualIp(string newIpStr, string subnetMask = "255.255.255.0")
    {
        if (!ConfigureManualAddress(newIpStr, subnetMask))
            return false;
        if (_adapter == null)
            return true;

        try
        {
            _adapter.SetIpAddress(_localIp, _subnetMask);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
    {
        Console.WriteLine($"[LAN] Connection with {peer.Name} was denied or failed.");
        return Task.CompletedTask;
    }

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.ApplyOptimalTuning();
        QuicPunch.Helpers.MsQuicTuner.TrySetStreamPriority(stream, QuicPunch.Helpers.QuicStreamPriority.High);
        uint remoteIpUint = 0;
        PeerLanSession? peerSession = null;
        Action<byte[]>? datagramHandler = null;
        try
        {
            byte[] localIpBytes = _localIp.GetAddressBytes();
            await stream.WriteAsync(localIpBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            byte[] remoteIpBytes = new byte[4];
            await stream.ReadExactlyAsync(remoteIpBytes, ct).ConfigureAwait(false);
            remoteIpUint = BinaryPrimitives.ReadUInt32BigEndian(remoteIpBytes);
            var remoteIp = new IPAddress(remoteIpBytes);

            if (remoteIpUint == _localIpUint)
                throw new InvalidDataException($"LAN address collision: both nodes use {_localIp}.");
            if (ActivePeers.TryGetValue(remoteIpUint, out var collision) && collision.Peer.Id != peer.Id)
                throw new InvalidDataException($"LAN address collision: {remoteIp} is already assigned to {collision.Peer.Name}.");

            if (ActivePeers.TryRemove(remoteIpUint, out var previous))
                previous.Dispose();

            peerSession = new PeerLanSession(remoteIpUint, remoteIp, peer, stream, connection);
            ActivePeers[remoteIpUint] = peerSession;
            Console.WriteLine($"[LAN] {peer.Name} joined as {remoteIp}.");
            StateChanged?.Invoke();

            datagramHandler = dgram =>
            {
                if (dgram == null || dgram.Length < 20) return;
                if ((dgram[0] >> 4) != 4) return;

                ReadOnlySpan<byte> packetSpan = dgram;
                uint sourceIp = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(12, 4));
                if (sourceIp != remoteIpUint) return;

                Interlocked.Increment(ref TotalRxPackets);
                Interlocked.Add(ref TotalRxBytes, dgram.Length);
                Interlocked.Increment(ref peerSession.RxPackets);
                Interlocked.Add(ref peerSession.RxBytes, dgram.Length);

                uint destIp = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(16, 4));
                bool broadcast = IsBroadcastOrMulticast(destIp);
                IVpnSession? localSession = _session;
                if ((destIp == _localIpUint || broadcast) && localSession != null)
                {
                    try { localSession.SendPacket(packetSpan); } catch { }
                }

                if (broadcast)
                    RelayBroadcastToOtherPeers(packetSpan, remoteIpUint);
                else if (destIp != _localIpUint && ActivePeers.TryGetValue(destIp, out var target))
                {
                    using var packet = PooledPacket.Create(packetSpan);
                    SendPacketToPeerStream(target, packet);
                }
            };

            if (connection.DatagramChannel != null)
            {
                connection.DatagramChannel.OnDatagramReceived += datagramHandler;
            }

            byte[] lenBuffer = new byte[2];
            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(lenBuffer, ct).ConfigureAwait(false);
                    ushort len = BinaryPrimitives.ReadUInt16LittleEndian(lenBuffer);
                    if (len == 0)
                        continue;

                    await stream.ReadExactlyAsync(rented.AsMemory(0, len), ct).ConfigureAwait(false);
                    if (len < 20)
                        continue;

                    Interlocked.Increment(ref TotalRxPackets);
                    Interlocked.Add(ref TotalRxBytes, len);
                    Interlocked.Increment(ref peerSession.RxPackets);
                    Interlocked.Add(ref peerSession.RxBytes, len);

                    if ((rented[0] >> 4) != 4)
                        continue;

                    ReadOnlySpan<byte> packetSpan = rented.AsSpan(0, len);
                    uint sourceIp = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(12, 4));
                    if (sourceIp != remoteIpUint)
                        continue;

                    uint destIp = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(16, 4));
                    bool broadcast = IsBroadcastOrMulticast(destIp);
                    IVpnSession? localSession = _session;
                    if ((destIp == _localIpUint || broadcast) && localSession != null)
                    {
                        try { localSession.SendPacket(packetSpan); } catch { }
                    }

                    if (broadcast)
                        RelayBroadcastToOtherPeers(packetSpan, remoteIpUint);
                    else if (destIp != _localIpUint && ActivePeers.TryGetValue(destIp, out var target))
                    {
                        using var packet = PooledPacket.Create(packetSpan);
                        SendPacketToPeerStream(target, packet);
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[LAN] Tunnel error with {peer.Name}: {ex.Message}");
        }
        finally
        {
            if (connection.DatagramChannel != null && datagramHandler != null)
            {
                try { connection.DatagramChannel.OnDatagramReceived -= datagramHandler; } catch { }
            }
            if (peerSession != null)
            {
                ActivePeers.TryRemove(new KeyValuePair<uint, PeerLanSession>(remoteIpUint, peerSession));
                peerSession.Dispose();
                StateChanged?.Invoke();
            }
            Console.WriteLine($"[LAN] Tunnel with {peer.Name} closed.");
        }
    }

    private void CaptureVpnAndSendToPeers(IVpnSession session, CancellationToken ct)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!session.WaitForPacket(100, ct))
                        continue;

                    while (!ct.IsCancellationRequested)
                    {
                        int packetSize = session.ReceivePacket(rented);
                        if (packetSize <= 0)
                            break;

                        if (packetSize < 20 || (rented[0] >> 4) != 4)
                            continue;

                        ReadOnlySpan<byte> packetSpan = rented.AsSpan(0, packetSize);
                        uint destIp = BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(16, 4));

                        if (IsBroadcastOrMulticast(destIp))
                        {
                            using var packet = PooledPacket.Create(packetSpan);
                            foreach (var peerSession in ActivePeers.Values)
                            {
                                SendPacketToPeerStream(peerSession, packet);
                            }
                        }
                        else if (ActivePeers.TryGetValue(destIp, out var target))
                        {
                            using var packet = PooledPacket.Create(packetSpan);
                            SendPacketToPeerStream(target, packet);
                        }
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Console.WriteLine($"[LAN] VPN capture error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void SendPacketToPeerStream(PeerLanSession session, PooledPacket packet)
    {
        if (session.TrySendDatagram(packet))
        {
            Interlocked.Increment(ref TotalTxPackets);
            Interlocked.Add(ref TotalTxBytes, packet.Length);
            return;
        }

        packet.AddRef();
        session.EnqueuePacket(packet);
        Interlocked.Increment(ref TotalTxPackets);
        Interlocked.Add(ref TotalTxBytes, packet.Length);
    }

    private void RelayBroadcastToOtherPeers(ReadOnlySpan<byte> packetSpan, uint sourceIpUint)
    {
        using var packet = PooledPacket.Create(packetSpan);
        foreach (var peerSession in ActivePeers.Values)
        {
            if (peerSession.RemoteIpUint != sourceIpUint)
                SendPacketToPeerStream(peerSession, packet);
        }
    }

    private bool IsBroadcastOrMulticast(uint destIp)
    {
        if (destIp == 0xFFFFFFFF) return true;
        if (destIp >= 0xE0000000 && destIp <= 0xEFFFFFFF) return true;

        if (!TryParseIpv4(_subnetMask, out var maskIp))
            return false;
        uint mask = BinaryPrimitives.ReadUInt32BigEndian(maskIp.GetAddressBytes());
        uint directedBroadcast = (_localIpUint & mask) | ~mask;
        return destIp == directedBroadcast;
    }

    private void SetAddressFields(IPAddress ip, string subnetMask)
    {
        _localIp = ip;
        _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        _subnetMask = subnetMask;
    }

    private static bool TryParseIpv4(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address = parsed;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    public static void SetAdapterIP(string adapterName, string ipAddress, string subnetMask)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ip set address name=\"{adapterName}\" static {ipAddress} {subnetMask}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            if (process is { ExitCode: not 0 })
                throw new InvalidOperationException($"netsh failed while assigning {ipAddress} (exit {process.ExitCode}).");
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!IPAddress.TryParse(subnetMask, out var maskIp))
                throw new ArgumentException("Invalid subnet mask", nameof(subnetMask));
            uint maskUint = BinaryPrimitives.ReadUInt32BigEndian(maskIp.GetAddressBytes());
            int cidr = System.Numerics.BitOperations.PopCount(maskUint);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ip",
                Arguments = $"addr replace {ipAddress}/{cidr} dev {adapterName}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            if (process is { ExitCode: not 0 })
                throw new InvalidOperationException($"ip addr replace failed while assigning {ipAddress}/{cidr} (exit {process.ExitCode}).");
        }
    }

    public static void SetAdapterMTU(string adapterName, int mtu)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ipv4 set subinterface \"{adapterName}\" mtu={mtu} store=persistent",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            if (process is { ExitCode: not 0 })
                throw new InvalidOperationException($"netsh failed while setting MTU {mtu} (exit {process.ExitCode}).");
        }
        else if (OperatingSystem.IsLinux())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ip",
                Arguments = $"link set dev {adapterName} mtu {mtu} up",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            if (process is { ExitCode: not 0 })
                throw new InvalidOperationException($"ip link set failed while setting MTU {mtu} (exit {process.ExitCode}).");
        }
    }

    public void Dispose() => StopTun();
}

/// <summary>
/// High-performance zero-allocation packet container backed by ArrayPool&lt;byte&gt;.Shared
/// with atomic reference counting. Instances are recycled via an internal pool to eliminate
/// all heap allocations during steady-state packet forwarding.
/// </summary>
public sealed class PooledPacket : IDisposable
{
    private static readonly ConcurrentQueue<PooledPacket> _instancePool = new();
    private const int MaxPooledInstances = 2048;

    private byte[]? _buffer;
    private int _length;
    private int _refCount;

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledPacket));
    public int Length => _length;
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);
    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    private PooledPacket() { }

    public static PooledPacket Create(ReadOnlySpan<byte> source)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(rented);

        if (!_instancePool.TryDequeue(out var packet))
        {
            packet = new PooledPacket();
        }

        packet._buffer = rented;
        packet._length = source.Length;
        packet._refCount = 1;
        return packet;
    }

    public static unsafe PooledPacket Create(byte* source, int length)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        new ReadOnlySpan<byte>(source, length).CopyTo(rented);

        if (!_instancePool.TryDequeue(out var packet))
        {
            packet = new PooledPacket();
        }

        packet._buffer = rented;
        packet._length = length;
        packet._refCount = 1;
        return packet;
    }

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            byte[]? buf = Interlocked.Exchange(ref _buffer, null);
            if (buf != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
            _length = 0;
            if (_instancePool.Count < MaxPooledInstances)
            {
                _instancePool.Enqueue(this);
            }
        }
    }
}

/// <summary>
/// Bounded queue for LAN sessions that discards oldest packets under congestion
/// and immediately recycles discarded packets to ArrayPool without memory leaks.
/// </summary>
public sealed class PooledPacketQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<PooledPacket> _queue;
    private readonly int _capacity;
    private readonly SemaphoreSlim _signal = new(0);
    private bool _disposed;

    public PooledPacketQueue(int capacity = 512)
    {
        _capacity = capacity;
        _queue = new Queue<PooledPacket>(capacity);
    }

    public int Count
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    public bool Enqueue(PooledPacket packet, ref long droppedCounter)
    {
        PooledPacket? dropped = null;
        bool releaseSignal = false;

        lock (_lock)
        {
            if (_disposed)
            {
                packet.Dispose();
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                dropped = _queue.Dequeue();
                Interlocked.Increment(ref droppedCounter);
            }
            else
            {
                releaseSignal = true;
            }

            _queue.Enqueue(packet);
        }

        dropped?.Dispose();
        if (releaseSignal)
        {
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }
        return true;
    }

    public ValueTask<PooledPacket?> DequeueAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.FromResult<PooledPacket?>(null);

            if (_queue.Count > 0)
            {
                _signal.Wait(0);
                return ValueTask.FromResult<PooledPacket?>(_queue.Dequeue());
            }
        }

        return DequeueSlowAsync(ct);
    }

    private async ValueTask<PooledPacket?> DequeueSlowAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_disposed || _queue.Count == 0)
                return null;

            return _queue.Dequeue();
        }
    }

    public void Dispose()
    {
        List<PooledPacket> remaining;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            remaining = new List<PooledPacket>(_queue);
            _queue.Clear();
        }

        foreach (var p in remaining)
        {
            p.Dispose();
        }

        try { _signal.Release(int.MaxValue / 2); } catch { }
        _signal.Dispose();
    }
}

public sealed class PeerLanSession : IDisposable
{
    public uint RemoteIpUint { get; }
    public IPAddress RemoteIp { get; }
    public PeerInfo Peer { get; }
    public Stream Stream { get; }
    public QuicConnection Connection { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public long RxPackets;
    public long TxPackets;
    public long RxBytes;
    public long TxBytes;
    public long DroppedPackets;

    private readonly PooledPacketQueue _outQueue = new(512);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendTask;

    public PeerLanSession(uint remoteIpUint, IPAddress remoteIp, PeerInfo peer, Stream stream, QuicConnection connection)
    {
        RemoteIpUint = remoteIpUint;
        RemoteIp = remoteIp;
        Peer = peer;
        Stream = stream;
        Connection = connection;
        _sendTask = Task.Run(SendLoopAsync);
    }

    public bool TrySendDatagram(PooledPacket packet)
    {
        if (Connection.DatagramChannel != null && Connection.DatagramChannel.IsSendEnabled)
        {
            int maxLen = Connection.DatagramChannel.MaxSendDatagramLength;
            if (packet.Length <= maxLen)
            {
                if (Connection.SendDatagram(packet.Span))
                {
                    Interlocked.Increment(ref TxPackets);
                    Interlocked.Add(ref TxBytes, packet.Length);
                    return true;
                }
            }
        }
        return false;
    }

    public void EnqueuePacket(PooledPacket packet)
    {
        _outQueue.Enqueue(packet, ref DroppedPackets);
    }

    private async Task SendLoopAsync()
    {
        byte[] sendBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65535 + 2);
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                PooledPacket? packet = await _outQueue.DequeueAsync(_cts.Token).ConfigureAwait(false);
                if (packet == null)
                    break;

                try
                {
                    int packetLen = packet.Length;
                    if (packetLen == 0 || packetLen > ushort.MaxValue)
                        continue;

                    BinaryPrimitives.WriteUInt16LittleEndian(sendBuffer.AsSpan(0, 2), (ushort)packetLen);
                    packet.Span.CopyTo(sendBuffer.AsSpan(2));

                    int totalLen = packetLen + 2;
                    await Stream.WriteAsync(sendBuffer.AsMemory(0, totalLen), _cts.Token).ConfigureAwait(false);

                    // Smart flush: flush only when the queue is currently drained, allowing QUIC to coalesce back-to-back packets
                    if (_outQueue.Count == 0)
                    {
                        await Stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref TxPackets);
                    Interlocked.Add(ref TxBytes, packetLen);
                }
                finally
                {
                    packet.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(sendBuffer);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _outQueue.Dispose();
        try { Stream.Close(); } catch { }
        try { _sendTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        _cts.Dispose();
    }
}

