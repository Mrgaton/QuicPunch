using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using QuicPunch.Datagrams;
using QuicPunch.Helpers;

namespace QuicPunch;

/// <summary>
/// Android ARM64 transport backed by MsQuic. The native TLS callback pins the peer by
/// SHA3-256 of its public key, the same identity desktop validates (PeerInfo.CertHash).
/// </summary>
internal sealed class AndroidMsQuicTransport : IQuicConnectionTransport
{
    private nint _context;
    private readonly IPEndPoint? _remote;
    private IPEndPoint? _currentRemote;
    private readonly string _targetHost;
    private X509Certificate2? _peerCertificate;
    private readonly ConcurrentDictionary<AndroidMsQuicStream, byte> _streams = new();
    private readonly object _nativeGate = new();
    private AndroidMsQuicDatagramChannel? _datagramChannel;
    private readonly DatagramFrameReassembler _reassembler = new();
    private readonly ConcurrentDictionary<byte, Action<PeerInfo, QuicDatagramMessage>> _categoryHandlers = new();
    private int _nextSequenceNumber, _nextFrameId;

    private AndroidMsQuicTransport(nint context, IPEndPoint? remote, string targetHost)
    {
        _context = context;
        _remote = remote;
        _targetHost = targetHost;
    }

    /// <param name="peerCertificateHash">SHA3-256 of the peer's public key (PeerInfo.CertHash).</param>
    public static AndroidMsQuicTransport Create(X509Certificate2 ownCertificate, byte[] peerCertificateHash,
        IPEndPoint? remote = null, string targetHost = QuicPunchConnection.DefaultDisguiseHost)
    {
        if (!OperatingSystem.IsAndroid()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(peerCertificateHash);
        if (peerCertificateHash.Length != 32)
            throw new ArgumentException("The peer certificate hash must be 32 bytes.", nameof(peerCertificateHash));
        byte[] pfx = ownCertificate.Export(X509ContentType.Pkcs12);
        nint context = AndroidMsQuicNative.Create(pfx, (uint)pfx.Length, peerCertificateHash, (uint)peerCertificateHash.Length, remote == null ? 1 : 0);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(pfx);
        if (context == 0) throw new InvalidOperationException("Android MsQuic could not initialize its TLS credentials.");
        return new AndroidMsQuicTransport(context, remote, targetHost);
    }

    public void Listen(ushort port)
    {
        int status;
        lock (_nativeGate) status = AndroidMsQuicNative.Listen(Context, port);
        if (status != 0) throw new IOException($"Android MsQuic listener failed: 0x{status:X8}.");
    }

    public void Connect(IPEndPoint remote, ushort localPort)
    {
        int status;
        // The hole-punch socket is IPv4; never hand MsQuic an IPv4-mapped IPv6 literal.
        IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        lock (_nativeGate) status = AndroidMsQuicNative.Connect(Context, address.ToString(), (ushort)remote.Port, localPort, _targetHost);
        if (status != 0) throw new IOException($"Android MsQuic connection start failed: 0x{status:X8}.");
    }

    public async Task WaitConnectedAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int status;
            lock (_nativeGate) status = AndroidMsQuicNative.WaitConnected(Context, 0);
            if (status == 0)
            {
                if (_datagramChannel == null)
                {
                    var channel = new AndroidMsQuicDatagramChannel(Context);
                    channel.OnDatagramReceived += OnRawDatagramReceived;
                    channel.OnPeerAddressChanged += address => Volatile.Write(ref _currentRemote, address);
                    _datagramChannel = channel;
                }
                var tls = new QuicConnectionTelemetry { NegotiatedTlsGroup = NegotiatedTlsGroup };
                QuicPunchLog.Info($"[ANDROID QUIC TLS] Group: {tls.NegotiatedTlsGroupName}; cipher: {NegotiatedCipherSuite}");
                return;
            }
            if (status != -3) throw new IOException($"Android MsQuic TLS handshake failed: 0x{status:X8}.");
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    public EndPoint? LocalEndPoint
    {
        get { lock (_nativeGate) return _context == 0 ? null : AndroidMsQuicNative.TryGetAddress(_context, remote: false); }
    }
    public EndPoint? RemoteEndPoint
    {
        get { lock (_nativeGate) return (_context == 0 ? null : AndroidMsQuicNative.TryGetAddress(_context, remote: true)) ?? Volatile.Read(ref _currentRemote) ?? _remote; }
    }
    public string TargetHostName => _targetHost;
    public bool IsClosed { get { lock (_nativeGate) return _context == 0 || AndroidMsQuicNative.IsStopped(_context) != 0; } }
    public SslApplicationProtocol NegotiatedApplicationProtocol =>
        TryReadTls(out string alpn, out _) && !string.IsNullOrEmpty(alpn) ? new(alpn) : default;
    public System.Security.Authentication.SslProtocols SslProtocol => System.Security.Authentication.SslProtocols.Tls13;
    public System.Net.Security.TlsCipherSuite NegotiatedCipherSuite =>
        TryReadTls(out _, out ushort cipher) ? (System.Net.Security.TlsCipherSuite)cipher : default;
    /// <summary>The certificate the peer actually presented and that passed pinning.</summary>
    public X509Certificate? RemoteCertificate
    {
        get
        {
            if (Volatile.Read(ref _peerCertificate) is { } cached) return cached;
            byte[]? der;
            lock (_nativeGate) der = _context == 0 ? null : AndroidMsQuicNative.TryGetPeerCertificate(_context);
            if (der == null) return null;
            var certificate = X509CertificateLoader.LoadCertificate(der);
            return Interlocked.CompareExchange(ref _peerCertificate, certificate, null) ?? certificate;
        }
    }
    internal uint NegotiatedTlsGroup
    {
        get { lock (_nativeGate) return _context == 0 ? 0 : AndroidMsQuicNative.GetTlsGroup(_context); }
    }
    private bool TryReadTls(out string alpn, out ushort cipher)
    {
        byte[] bytes = new byte[32];
        int result;
        lock (_nativeGate)
        {
            if (_context == 0) { cipher = 0; alpn = string.Empty; return false; }
            result = AndroidMsQuicNative.GetTls(_context, bytes, bytes.Length, out cipher);
        }
        if (result == 0) { alpn = string.Empty; return false; }
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        alpn = Encoding.ASCII.GetString(bytes, 0, length);
        return true;
    }
    public bool TryGetTelemetry(out QuicConnectionTelemetry telemetry)
    {
        ulong[] stats = new ulong[21];
        int result;
        lock (_nativeGate) result = _context == 0 ? 0 : AndroidMsQuicNative.GetStats(_context, stats, stats.Length);
        if (result == 0) { telemetry = null!; return false; }
        telemetry = new QuicConnectionTelemetry
        {
            RttMs = stats[0] / 1000.0,
            MinRttMs = stats[1] / 1000.0,
            MaxRttMs = stats[2] / 1000.0,
            RttVarianceMs = stats[3] / 1000.0,
            PathMtu = (ushort)stats[4],
            SendTotalPackets = stats[5],
            SendSuspectedLostPackets = stats[6],
            SendSpuriousLostPackets = stats[7],
            SendTotalBytes = stats[8],
            RecvTotalPackets = stats[9],
            RecvTotalBytes = stats[10],
            RecvDroppedPackets = stats[11],
            RecvDecryptionFailures = stats[12],
            SendCongestionWindow = (uint)stats[13],
            CongestionAlgorithm = stats[20] <= 1 ? (QuicCongestionAlgorithm)stats[20] : null,
            SendCongestionCount = (uint)stats[14],
            RecvValidAckFrames = stats[15],
            RecvReorderedPackets = stats[16],
            RecvDuplicatePackets = stats[17],
            SendTotalStreamBytes = stats[18],
            RecvTotalStreamBytes = stats[19],
            SslProtocol = "Tls13",
            NegotiatedTlsGroup = NegotiatedTlsGroup,
            CipherSuite = NegotiatedCipherSuite.ToString(),
            Alpn = TryReadTls(out string alpn, out _) ? alpn : null,
            NativeLocalEndPoint = LocalEndPoint as IPEndPoint,
            NativeRemoteEndPoint = RemoteEndPoint as IPEndPoint
        };
        return true;
    }
    public IQuicDatagramChannel? DatagramChannel => _datagramChannel;
    public bool SendDatagram(ReadOnlySpan<byte> datagram) => _datagramChannel?.Send(datagram) ?? false;

    public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload,
        QuicDatagramFlags flags = QuicDatagramFlags.None, uint sequenceNumber = 0)
    {
        if (sequenceNumber == 0) sequenceNumber = (uint)Interlocked.Increment(ref _nextSequenceNumber);
        int capacity = _datagramChannel?.MaxSendDatagramLength ?? 0;
        if (payload.Length + QuicDatagramEnvelope.UnfragmentedHeaderSize > capacity) return false;
        byte[] bytes = new byte[QuicDatagramEnvelope.UnfragmentedHeaderSize + payload.Length];
        QuicDatagramEnvelope.WriteEnvelope(bytes, category, flags, sequenceNumber, payload);
        return SendDatagram(bytes);
    }

    public bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200)
    {
        int capacity = (_datagramChannel?.MaxSendDatagramLength ?? 0) - QuicDatagramEnvelope.FragmentedHeaderSize;
        maxFragmentSize = Math.Min(maxFragmentSize, capacity);
        if (maxFragmentSize <= 0) return false;
        if (frameData.Length <= maxFragmentSize)
            return SendCategorizedDatagram(category, frameData, isKeyFrame ? QuicDatagramFlags.IsKeyFrame : QuicDatagramFlags.None);
        int total = (frameData.Length + maxFragmentSize - 1) / maxFragmentSize;
        if (total > 1024) return false;
        ushort id = (ushort)Interlocked.Increment(ref _nextFrameId);
        bool success = true;
        for (int i = 0; i < total; ++i)
        {
            ReadOnlySpan<byte> slice = frameData.Slice(i * maxFragmentSize, Math.Min(maxFragmentSize, frameData.Length - i * maxFragmentSize));
            byte[] bytes = new byte[QuicDatagramEnvelope.FragmentedHeaderSize + slice.Length];
            var flags = QuicDatagramFlags.IsFragmented;
            if (isKeyFrame) flags |= QuicDatagramFlags.IsKeyFrame;
            if (i == total - 1) flags |= QuicDatagramFlags.IsLastFragment;
            QuicDatagramEnvelope.WriteFragmentEnvelope(bytes, category, flags,
                (uint)Interlocked.Increment(ref _nextSequenceNumber), id, (ushort)i, (ushort)total, slice);
            if (!SendDatagram(bytes)) success = false;
        }
        return success;
    }

    public void RegisterDatagramCategory(byte category, Action<PeerInfo, QuicDatagramMessage> handler) =>
        _categoryHandlers[category] = handler;
    public event Action<PeerInfo, QuicDatagramMessage>? OnCategorizedDatagramReceived;
    public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived;

    private void OnRawDatagramReceived(byte[] data)
    {
        if (!QuicDatagramEnvelope.TryParse(data, out var envelope)) return;
        var peer = new PeerInfo(RemoteCertificate as X509Certificate2, Array.Empty<byte>())
        {
            ActiveEndPoint = Volatile.Read(ref _currentRemote) ?? _remote
        };
        if (envelope.IsFragmented)
        {
            if (_reassembler.TryProcessFragment(envelope, out byte category, out var frame, out bool isKey))
                OnFrameReceived?.Invoke(peer, category, frame, isKey);
            return;
        }
        var message = new QuicDatagramMessage(peer, envelope.Category, envelope.Flags,
            envelope.SequenceNumber, envelope.Payload.ToArray(), envelope.IsFramed);
        if (_categoryHandlers.TryGetValue(envelope.Category, out var handler)) handler(peer, message);
        OnCategorizedDatagramReceived?.Invoke(peer, message);
    }

    public async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint stream;
        lock (_nativeGate) stream = AndroidMsQuicNative.OpenStream(Context, type == QuicStreamType.Bidirectional ? 1 : 0);
        if (stream == 0) throw new IOException("Android MsQuic could not open a stream.");
        var wrapper = new AndroidMsQuicStream(this, stream, type, inbound: false);
        _streams.TryAdd(wrapper, 0);
        await Task.CompletedTask;
        return wrapper;
    }

    public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint stream;
            lock (_nativeGate) stream = AndroidMsQuicNative.AcceptStream(Context, 0);
            if (stream != 0)
            {
                var type = AndroidMsQuicNative.StreamBidi(stream) != 0 ? QuicStreamType.Bidirectional : QuicStreamType.Unidirectional;
                var wrapper = new AndroidMsQuicStream(this, stream, type, inbound: true);
                _streams.TryAdd(wrapper, 0);
                return wrapper;
            }
            lock (_nativeGate)
                if (AndroidMsQuicNative.IsStopped(Context) != 0) throw new IOException("Android MsQuic connection closed.");
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask CloseAsync(long errorCode, CancellationToken cancellationToken = default) => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (AndroidMsQuicStream stream in _streams.Keys) stream.Dispose();
        if (_datagramChannel != null)
        {
            _datagramChannel.OnDatagramReceived -= OnRawDatagramReceived;
            await _datagramChannel.StopAsync().ConfigureAwait(false);
        }
        nint context = Interlocked.Exchange(ref _context, 0);
        if (context != 0)
            lock (_nativeGate) AndroidMsQuicNative.Close(context);
        _reassembler.Clear();
    }

    internal void Forget(AndroidMsQuicStream stream) => _streams.TryRemove(stream, out _);
    private nint Context => Volatile.Read(ref _context) is var value && value != 0 ? value : throw new ObjectDisposedException(nameof(AndroidMsQuicTransport));
}

internal sealed class AndroidMsQuicStream : Stream, IQuicStreamTransport
{
    private readonly AndroidMsQuicTransport _owner;
    private nint _stream;
    private readonly TaskCompletionSource _readsClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writesClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly QuicStreamType _type;
    private readonly bool _inbound;
    private readonly object _nativeGate = new();
    private ushort _priority16 = 0x7fff;

    internal AndroidMsQuicStream(AndroidMsQuicTransport owner, nint stream, QuicStreamType type, bool inbound)
    {
        _owner = owner; _stream = stream; _type = type; _inbound = inbound;
    }

    public long Id { get { lock (_nativeGate) return (long)AndroidMsQuicNative.StreamId(Handle); } }
    public QuicStreamType Type => _type;
    public Stream Stream => this;
    public Task ReadsClosed => _readsClosed.Task;
    public Task WritesClosed => _writesClosed.Task;
    public byte Priority
    {
        get => (byte)(Priority16 >> 8);
        set => Priority16 = (ushort)((value << 8) | value);
    }
    public ushort Priority16
    {
        get
        {
            lock (_nativeGate) return _stream == 0 ? _priority16 : AndroidMsQuicNative.StreamGetPriority(_stream);
        }
        set
        {
            lock (_nativeGate)
            {
                _priority16 = value;
                if (_stream != 0) AndroidMsQuicNative.StreamSetPriority(_stream, value);
            }
        }
    }
    public override bool CanRead => _stream != 0 && (_type == QuicStreamType.Bidirectional || _inbound);
    public override bool CanWrite => _stream != 0 && (_type == QuicStreamType.Bidirectional || !_inbound);
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void CompleteWrites()
    {
        lock (_nativeGate) AndroidMsQuicNative.StreamComplete(Handle);
        _writesClosed.TrySetResult();
    }

    public void Abort(QuicAbortDirection abortDirection, long errorCode) => Dispose();
    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_nativeGate) { _ = Handle; }
        // StreamWrite already copied the caller's bytes into the bounded native
        // send queue. As with System.Net.Quic.QuicStream, there is no additional
        // managed buffer to flush. Draining SEND_COMPLETE here serializes every
        // application frame through the 5 ms poll timer and starves QUIC.
        // CompleteWrites / protocol acknowledgments provide ordered completion.
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        byte[] temp = new byte[buffer.Length];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count;
            lock (_nativeGate) count = AndroidMsQuicNative.StreamRead(Handle, temp, temp.Length, 0);
            if (count == -3)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (count < 0) throw new IOException($"Android MsQuic stream read failed: {count}.");
            if (count == 0) _readsClosed.TrySetResult();
            else temp.AsMemory(0, count).CopyTo(buffer);
            return count;
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data = buffer.Slice(offset, Math.Min(64 * 1024, buffer.Length - offset)).ToArray();
            int written;
            lock (_nativeGate) written = AndroidMsQuicNative.StreamWrite(Handle, data, data.Length);
            if (written == -3)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (written != data.Length) throw new IOException($"Android MsQuic stream write failed: 0x{written:X8}.");
            offset += written;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        lock (_nativeGate)
        {
            nint handle = Interlocked.Exchange(ref _stream, 0);
            if (handle != 0) AndroidMsQuicNative.StreamClose(handle);
        }
        _readsClosed.TrySetResult(); _writesClosed.TrySetResult();
        _owner.Forget(this);
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    private nint Handle => Volatile.Read(ref _stream) is var value && value != 0 ? value : throw new ObjectDisposedException(nameof(AndroidMsQuicStream));
}

internal static partial class AndroidMsQuicNative
{
    private const string Library = "qp_transport";
    [LibraryImport(Library, EntryPoint = "qp_create")] internal static partial nint Create(byte[] pfx, uint pfxLength, byte[] expected, uint expectedLength, int server);
    [LibraryImport(Library, EntryPoint = "qp_listen")] internal static partial int Listen(nint context, ushort port);
    [LibraryImport(Library, EntryPoint = "qp_connect", StringMarshalling = StringMarshalling.Utf8)] internal static partial int Connect(nint context, string host, ushort port, ushort localPort, string? serverName);
    [LibraryImport(Library, EntryPoint = "qp_get_peer_certificate")] private static partial int GetPeerCertificate(nint context, byte[]? output, int capacity);
    internal static byte[]? TryGetPeerCertificate(nint context)
    {
        int length = GetPeerCertificate(context, null, 0);
        if (length == 0) return null;
        byte[] der = new byte[Math.Abs(length)];
        return GetPeerCertificate(context, der, der.Length) == der.Length ? der : null;
    }
    [LibraryImport(Library, EntryPoint = "qp_wait_connected")] internal static partial int WaitConnected(nint context, int timeoutMs);
    [LibraryImport(Library, EntryPoint = "qp_open_stream")] internal static partial nint OpenStream(nint context, int bidi);
    [LibraryImport(Library, EntryPoint = "qp_accept_stream")] internal static partial nint AcceptStream(nint context, int timeoutMs);
    [LibraryImport(Library, EntryPoint = "qp_stream_read")] internal static partial int StreamRead(nint stream, byte[] data, int capacity, int timeoutMs);
    [LibraryImport(Library, EntryPoint = "qp_stream_write")] internal static partial int StreamWrite(nint stream, byte[] data, int length);
    [LibraryImport(Library, EntryPoint = "qp_stream_complete")] internal static partial void StreamComplete(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_flush")] internal static partial int StreamFlush(nint stream, int timeoutMs);
    [LibraryImport(Library, EntryPoint = "qp_stream_close")] internal static partial void StreamClose(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_id")] internal static partial ulong StreamId(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_bidi")] internal static partial int StreamBidi(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_set_priority")] internal static partial int StreamSetPriority(nint stream, ushort priority);
    [LibraryImport(Library, EntryPoint = "qp_stream_get_priority")] internal static partial ushort StreamGetPriority(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_is_stopped")] internal static partial int IsStopped(nint context);
    [LibraryImport(Library, EntryPoint = "qp_close")] internal static partial void Close(nint context);
    [LibraryImport(Library, EntryPoint = "qp_send_datagram")] internal static partial int SendDatagram(nint context, byte[] data, int length);
    [LibraryImport(Library, EntryPoint = "qp_receive_datagram")] internal static partial int ReceiveDatagram(nint context, byte[] data, int capacity, int timeoutMs);
    [LibraryImport(Library, EntryPoint = "qp_datagram_state")] internal static partial int DatagramState(nint context);
    [LibraryImport(Library, EntryPoint = "qp_get_stats")] internal static partial int GetStats(nint context, [Out] ulong[] output, int capacity);
    [LibraryImport(Library, EntryPoint = "qp_get_tls")] internal static partial int GetTls(nint context, [Out] byte[] alpn, int capacity, out ushort cipher);
    [LibraryImport(Library, EntryPoint = "qp_get_tls_group")] internal static partial uint GetTlsGroup(nint context);
    [LibraryImport(Library, EntryPoint = "qp_get_address")] private static partial int GetAddress(nint context, int remote, byte[] output, int capacity, out ushort port);
    internal static IPEndPoint? TryGetAddress(nint context, bool remote)
    {
        byte[] output = new byte[64];
        if (GetAddress(context, remote ? 1 : 0, output, output.Length, out ushort port) == 0) return null;
        int length = Array.IndexOf(output, (byte)0);
        if (length < 0) length = output.Length;
        return IPAddress.TryParse(Encoding.ASCII.GetString(output, 0, length), out IPAddress? address)
            ? new IPEndPoint(address, port) : null;
    }
}

internal sealed class AndroidMsQuicDatagramChannel : IQuicDatagramChannel
{
    private readonly nint _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private readonly object _nativeGate = new();
    private IPEndPoint? _lastRemoteAddress;
    public AndroidMsQuicDatagramChannel(nint context)
    {
        _context = context;
        _pump = Task.Factory.StartNew(Pump, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public bool IsSendEnabled { get { lock (_nativeGate) return !_stop.IsCancellationRequested && AndroidMsQuicNative.DatagramState(_context) > 0; } }
    public bool IsReceiveEnabled => !_stop.IsCancellationRequested;
    public ushort MaxSendDatagramLength { get { lock (_nativeGate) return _stop.IsCancellationRequested ? (ushort)0 : (ushort)Math.Clamp(AndroidMsQuicNative.DatagramState(_context), 0, ushort.MaxValue); } }
    public Channel<byte[]> IncomingDatagrams { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true
    });
    public event Action<byte[]>? OnDatagramReceived;
    public event Action<IPEndPoint>? OnPeerAddressChanged;
    public bool Send(ReadOnlySpan<byte> datagram)
    {
        if (_stop.IsCancellationRequested || datagram.Length == 0 || datagram.Length > MaxSendDatagramLength) return false;
        byte[] bytes = datagram.ToArray();
        lock (_nativeGate) return !_stop.IsCancellationRequested &&
            AndroidMsQuicNative.SendDatagram(_context, bytes, bytes.Length) == bytes.Length;
    }
    private void Pump()
    {
        byte[] buffer = new byte[65535];
        while (!_stop.IsCancellationRequested)
        {
            int length = AndroidMsQuicNative.ReceiveDatagram(_context, buffer, buffer.Length, 250);
            IPEndPoint? remote = AndroidMsQuicNative.TryGetAddress(_context, remote: true);
            if (remote != null && !_stop.IsCancellationRequested &&
                (_lastRemoteAddress == null || !_lastRemoteAddress.Equals(remote)))
            {
                _lastRemoteAddress = remote;
                OnPeerAddressChanged?.Invoke(remote);
            }
            if (length == -2) break;
            if (length <= 0) continue;
            byte[] data = buffer.AsSpan(0, length).ToArray();
            IncomingDatagrams.Writer.TryWrite(data);
            OnDatagramReceived?.Invoke(data);
        }
        IncomingDatagrams.Writer.TryComplete();
    }
    public async Task StopAsync()
    {
        _stop.Cancel();
        await _pump.ConfigureAwait(false);
        lock (_nativeGate) { }
    }
    public void Dispose() => _stop.Cancel();
}
