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
/// Owned Windows/Linux/Android transport backed by the public MsQuic API. Pins peers by
/// SHA3-256 of its public key, the same identity desktop validates (PeerInfo.CertHash).
/// </summary>
internal sealed class MsQuicBridgeTransport : IQuicConnectionTransport
{
    private nint _context;
    private readonly IPEndPoint? _remote;
    private IPEndPoint? _currentRemote;
    private readonly string _targetHost;
    private X509Certificate2? _peerCertificate;
    private readonly ConcurrentDictionary<MsQuicBridgeStream, byte> _streams = new();
    private readonly object _nativeGate = new();
    private MsQuicBridgeDatagramChannel? _datagramChannel;
    private readonly CategorizedDatagramProtocol _datagrams = new();

    private MsQuicBridgeTransport(nint context, IPEndPoint? remote, string targetHost)
    {
        _context = context;
        _remote = remote;
        _targetHost = targetHost;
    }

    /// <param name="peerCertificateHash">SHA3-256 of the peer's public key (PeerInfo.CertHash).</param>
    public static MsQuicBridgeTransport Create(X509Certificate2 ownCertificate, byte[] peerCertificateHash,
        IPEndPoint? remote = null, string targetHost = QuicPunchConnection.DefaultDisguiseHost)
    {
        ArgumentNullException.ThrowIfNull(peerCertificateHash);
        if (peerCertificateHash.Length != 32)
            throw new ArgumentException("The peer certificate hash must be 32 bytes.", nameof(peerCertificateHash));
        byte[] pfx = ownCertificate.Export(X509ContentType.Pkcs12);
        nint context;
        try { context = MsQuicBridgeNative.Create(pfx, (uint)pfx.Length, peerCertificateHash, (uint)peerCertificateHash.Length, remote == null ? 1 : 0); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(pfx); }
        if (context == 0) throw new InvalidOperationException("MsQuic could not initialize its TLS credentials.");
        ulong[] policy = Volatile.Read(ref MsQuicTuner.ConfigurationPolicy);
        if (MsQuicBridgeNative.Configure(context, policy, policy.Length) == 0)
        {
            MsQuicBridgeNative.Close(context);
            throw new InvalidOperationException("Native QUIC configuration rejected.");
        }
        return new MsQuicBridgeTransport(context, remote, targetHost);
    }

    public void Listen(ushort port)
    {
        int status;
        lock (_nativeGate) status = MsQuicBridgeNative.Listen(Context, port);
        if (status != 0) throw new IOException($"MsQuic listener failed: 0x{status:X8}.");
    }

    public void Connect(IPEndPoint remote, ushort localPort)
    {
        int status;
        // Normalize mapped IPv4 before selecting the native socket family.
        IPAddress address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        lock (_nativeGate) status = MsQuicBridgeNative.Connect(Context, address.ToString(), (ushort)remote.Port, localPort, _targetHost);
        if (status != 0) throw new IOException($"MsQuic connection start failed: 0x{status:X8}.");
    }

    public async Task WaitConnectedAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int status;
            lock (_nativeGate)
            {
                status = MsQuicBridgeNative.WaitConnected(Context, 0);
                if (status == 0 && _datagramChannel == null)
                {
                    var channel = new MsQuicBridgeDatagramChannel(Context);
                    channel.OnDatagramReceived += OnRawDatagramReceived;
                    channel.OnPeerAddressChanged += address => Volatile.Write(ref _currentRemote, address);
                    _datagramChannel = channel;
                }
            }
            if (status == 0)
            {
                var tls = new QuicConnectionTelemetry { NegotiatedTlsGroup = NegotiatedTlsGroup };
                QuicPunchLog.Info($"[QUIC TLS] Group: {tls.NegotiatedTlsGroupName}; cipher: {NegotiatedCipherSuite}");
                return;
            }
            if (status != -3) throw new IOException($"MsQuic TLS handshake failed: 0x{status:X8}.");
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    public EndPoint? LocalEndPoint
    {
        get { lock (_nativeGate) return _context == 0 ? null : MsQuicBridgeNative.TryGetAddress(_context, remote: false); }
    }
    public EndPoint? RemoteEndPoint
    {
        get { lock (_nativeGate) return (_context == 0 ? null : MsQuicBridgeNative.TryGetAddress(_context, remote: true)) ?? Volatile.Read(ref _currentRemote) ?? _remote; }
    }
    public string TargetHostName => _targetHost;
    public bool IsClosed { get { lock (_nativeGate) return _context == 0 || MsQuicBridgeNative.IsStopped(_context) != 0; } }
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
            lock (_nativeGate) der = _context == 0 ? null : MsQuicBridgeNative.TryGetPeerCertificate(_context);
            if (der == null) return null;
            var certificate = X509CertificateLoader.LoadCertificate(der);
            return Interlocked.CompareExchange(ref _peerCertificate, certificate, null) ?? certificate;
        }
    }
    internal uint NegotiatedTlsGroup
    {
        get { lock (_nativeGate) return _context == 0 ? 0 : MsQuicBridgeNative.GetTlsGroup(_context); }
    }
    private bool TryReadTls(out string alpn, out ushort cipher)
    {
        byte[] bytes = new byte[32];
        int result;
        lock (_nativeGate)
        {
            if (_context == 0) { cipher = 0; alpn = string.Empty; return false; }
            result = MsQuicBridgeNative.GetTls(_context, bytes, bytes.Length, out cipher);
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
        lock (_nativeGate) result = _context == 0 ? 0 : MsQuicBridgeNative.GetStats(_context, stats, stats.Length);
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
            XdpRequested = MsQuicTuner.IsXdpRequested,
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
    internal unsafe bool TryGetNative<T>(uint parameter, out T value) where T : unmanaged
    {
        T data = default; uint length = (uint)sizeof(T);
        lock (_nativeGate)
        {
            bool ok = _context != 0 && MsQuicBridgeNative.GetParam(_context, parameter, ref length, (nint)(&data)) != 0;
            value = data;
            return ok && length <= sizeof(T);
        }
    }
    internal unsafe bool TrySetNative<T>(uint parameter, T value) where T : unmanaged
    {
        lock (_nativeGate) return _context != 0 && MsQuicBridgeNative.SetParam(_context, parameter, (uint)sizeof(T), (nint)(&value)) != 0;
    }
    internal bool Tune(int option, ulong a, ulong b = 0, ulong c = 0, ulong d = 0)
    {
        lock (_nativeGate) return _context != 0 && MsQuicBridgeNative.Tune(_context, option, a, b, c, d) != 0;
    }
    public bool TrySetDscp(QuicDscpPriority priority) => (byte)priority <= 63 && TrySetNative(MsQuicTuner.QuicParamConnSendDscp, (byte)priority);
    public bool TrySetCongestionControl(QuicCongestionAlgorithm algorithm) => TryGetTelemetry(out var t) && t.CongestionAlgorithm == algorithm;
    private void OnRawDatagramReceived(byte[] data)
    {
        // The multiplexer consumes the raw channel. Only decode application envelopes
        // here when the direct native datagram API actually has a receiver.
        if (!_datagrams.HasReceivers) return;
        var peer = new PeerInfo(RemoteCertificate is X509Certificate2 certificate ? certificate : null, Array.Empty<byte>())
        {
            ActiveEndPoint = RemoteEndPoint as IPEndPoint
        };
        _datagrams.Receive(data, peer);
    }

    public bool SendDatagram(ReadOnlySpan<byte> datagram) => DatagramChannel?.Send(datagram) ?? false;

    public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload,
        Datagrams.QuicDatagramFlags flags = Datagrams.QuicDatagramFlags.None, uint sequenceNumber = 0) =>
        _datagrams.SendDatagram(DatagramChannel, category, payload, flags, sequenceNumber);

    public bool SendFrame(byte category, ReadOnlySpan<byte> frameData, bool isKeyFrame = false, int maxFragmentSize = 1200) =>
        _datagrams.SendFrame(DatagramChannel, category, frameData, isKeyFrame, maxFragmentSize);

    public void RegisterDatagramCategory(byte category, Action<PeerInfo, Datagrams.QuicDatagramMessage> handler) =>
        _datagrams.RegisterCategory(category, handler);

    public event Action<PeerInfo, Datagrams.QuicDatagramMessage>? OnCategorizedDatagramReceived
    {
        add => _datagrams.OnDatagramReceived += value;
        remove => _datagrams.OnDatagramReceived -= value;
    }

    public event Action<PeerInfo, byte, ReadOnlyMemory<byte>, bool>? OnFrameReceived
    {
        add => _datagrams.OnFrameReceived += value;
        remove => _datagrams.OnFrameReceived -= value;
    }

    public async ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MsQuicBridgeStream wrapper;
        lock (_nativeGate)
        {
            nint stream = MsQuicBridgeNative.OpenStream(Context, type == QuicStreamType.Bidirectional ? 1 : 0);
            if (stream == 0) throw new IOException("MsQuic could not open a stream.");
            wrapper = new MsQuicBridgeStream(this, stream, type, inbound: false);
            _streams.TryAdd(wrapper, 0);
        }
        try { await wrapper.WaitStartedAsync(cancellationToken).ConfigureAwait(false); return wrapper; }
        catch { wrapper.Abort(QuicAbortDirection.Both, 0); throw; }
    }

    public async ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_nativeGate)
            {
                nint stream = MsQuicBridgeNative.AcceptStream(Context, 0);
                if (stream != 0)
                {
                    var type = MsQuicBridgeNative.StreamBidi(stream) != 0 ? QuicStreamType.Bidirectional : QuicStreamType.Unidirectional;
                    var wrapper = new MsQuicBridgeStream(this, stream, type, inbound: true);
                    _streams.TryAdd(wrapper, 0);
                    return wrapper;
                }
                if (MsQuicBridgeNative.IsStopped(Context) != 0) throw new IOException("MsQuic connection closed.");
            }
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask CloseAsync(long errorCode, CancellationToken cancellationToken = default) => DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        nint context;
        MsQuicBridgeStream[] streams;
        lock (_nativeGate)
        {
            context = _context;
            if (context == 0) return;
            _context = 0;
            streams = _streams.Keys.ToArray();
        }
        foreach (MsQuicBridgeStream stream in streams) stream.Abort(QuicAbortDirection.Both, 0);
        if (_datagramChannel != null)
        {
            _datagramChannel.OnDatagramReceived -= OnRawDatagramReceived;
            await _datagramChannel.StopAsync().ConfigureAwait(false);
        }
        MsQuicBridgeNative.Close(context);
        _datagrams.Clear();
    }

    internal void Forget(MsQuicBridgeStream stream) => _streams.TryRemove(stream, out _);
    private nint Context => Volatile.Read(ref _context) is var value && value != 0 ? value : throw new ObjectDisposedException(nameof(MsQuicBridgeTransport));
}

internal sealed class MsQuicBridgeStream : Stream, IQuicStreamTransport
{
    internal unsafe bool TryGetStatistics(out QuicStreamStatistics stats)
    {
        QuicStreamStatistics value = default; uint length = (uint)sizeof(QuicStreamStatistics);
        lock (_nativeGate)
        {
            bool ok = _stream != 0 && MsQuicBridgeNative.StreamGetStats(_stream, ref length, (nint)(&value)) != 0;
            stats = value; return ok;
        }
    }
    internal async Task WaitStartedAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int status;
            lock (_nativeGate) status = MsQuicBridgeNative.StreamStartStatus(Handle);
            if (status == 0) return;
            if (status != -3) throw new IOException("Native stream start failed or was blocked by peer flow control.");
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }
    private readonly MsQuicBridgeTransport _owner;
    private nint _stream;
    private readonly TaskCompletionSource _readsClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writesClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly QuicStreamType _type;
    private readonly bool _inbound;
    private readonly object _nativeGate = new();
    private ushort _priority16 = 0x7fff;
    private int _writeCompletionStarted;

    internal MsQuicBridgeStream(MsQuicBridgeTransport owner, nint stream, QuicStreamType type, bool inbound)
    {
        _owner = owner; _stream = stream; _type = type; _inbound = inbound;
    }

    public long Id { get { lock (_nativeGate) return (long)MsQuicBridgeNative.StreamId(Handle); } }
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
            lock (_nativeGate) return _stream == 0 ? _priority16 : MsQuicBridgeNative.StreamGetPriority(_stream);
        }
        set
        {
            lock (_nativeGate)
            {
                _priority16 = value;
                if (_stream != 0) MsQuicBridgeNative.StreamSetPriority(_stream, value);
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
        lock (_nativeGate) MsQuicBridgeNative.StreamComplete(Handle);
        if (Interlocked.Exchange(ref _writeCompletionStarted, 1) == 0) _ = ObserveWritesClosedAsync();
    }

    private async Task ObserveWritesClosedAsync()
    {
        while (!_writesClosed.Task.IsCompleted)
        {
            int status;
            lock (_nativeGate) status = _stream == 0 ? -1 : MsQuicBridgeNative.StreamWritesClosed(_stream);
            if (status > 0) { _writesClosed.TrySetResult(); return; }
            if (status < 0) { _writesClosed.TrySetCanceled(); return; }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    public void Abort(QuicAbortDirection abortDirection, long errorCode)
    {
        // This bridge closes the stream as a unit. Mark the native abort first so
        // disposal cannot spend 1.5 seconds trying to gracefully drain stale media.
        lock (_nativeGate) { if (_stream != 0) MsQuicBridgeNative.StreamAbort(_stream, unchecked((ulong)errorCode)); }
        Dispose();
    }
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
            lock (_nativeGate) count = MsQuicBridgeNative.StreamRead(Handle, temp, temp.Length, 0);
            if (count == -3)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (count < 0) throw new IOException($"MsQuic stream read failed: {count}.");
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
            lock (_nativeGate) written = MsQuicBridgeNative.StreamWrite(Handle, data, data.Length);
            if (written == -3)
            {
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (written != data.Length) throw new IOException($"MsQuic stream write failed: 0x{written:X8}.");
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
            if (handle != 0) MsQuicBridgeNative.StreamClose(handle);
        }
        _readsClosed.TrySetResult(); _writesClosed.TrySetCanceled();
        _owner.Forget(this);
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    private nint Handle => Volatile.Read(ref _stream) is var value && value != 0 ? value : throw new ObjectDisposedException(nameof(MsQuicBridgeStream));
}

internal static partial class MsQuicBridgeNative
{
    private const string Library = "qp_transport";
    [LibraryImport(Library, EntryPoint = "qp_initialize")] internal static partial int Initialize(int requestXdp);
    [LibraryImport(Library, EntryPoint = "qp_stream_get_stats")] internal static partial int StreamGetStats(nint stream, ref uint length, nint output);
    [LibraryImport(Library, EntryPoint = "qp_configure")] internal static partial int Configure(nint context, ulong[] values, int count);
    [LibraryImport(Library, EntryPoint = "qp_get_param")] internal static partial int GetParam(nint context, uint parameter, ref uint length, nint value);
    [LibraryImport(Library, EntryPoint = "qp_set_param")] internal static partial int SetParam(nint context, uint parameter, uint length, nint value);
    [LibraryImport(Library, EntryPoint = "qp_global_get_param")] internal static partial int GlobalGetParam(uint parameter, ref uint length, nint value);
    [LibraryImport(Library, EntryPoint = "qp_tune")] internal static partial int Tune(nint context, int option, ulong a, ulong b, ulong c, ulong d);
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
    [LibraryImport(Library, EntryPoint = "qp_stream_start_status")] internal static partial int StreamStartStatus(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_write")] internal static partial int StreamWrite(nint stream, byte[] data, int length);
    [LibraryImport(Library, EntryPoint = "qp_stream_complete")] internal static partial void StreamComplete(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_writes_closed")] internal static partial int StreamWritesClosed(nint stream);
    [LibraryImport(Library, EntryPoint = "qp_stream_abort")] internal static partial void StreamAbort(nint stream, ulong error);
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

internal sealed class MsQuicBridgeDatagramChannel : IQuicDatagramChannel
{
    private readonly nint _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private readonly Channel<(byte[]? Data, IPEndPoint? Address, long Created)> _dispatch =
        Channel.CreateBounded<(byte[]?, IPEndPoint?, long)>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private readonly object _nativeGate = new();
    private IPEndPoint? _lastRemoteAddress;
    public MsQuicBridgeDatagramChannel(nint context)
    {
        _context = context;
        _pump = Task.Factory.StartNew(Pump, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = Task.Run(DispatchAsync);
    }
    public bool IsSendEnabled { get { lock (_nativeGate) return !_stop.IsCancellationRequested && MsQuicBridgeNative.DatagramState(_context) > 0; } }
    public bool IsReceiveEnabled => !_stop.IsCancellationRequested;
    public ushort MaxSendDatagramLength { get { lock (_nativeGate) return _stop.IsCancellationRequested ? (ushort)0 : (ushort)Math.Clamp(MsQuicBridgeNative.DatagramState(_context), 0, ushort.MaxValue); } }
    public Channel<byte[]> IncomingDatagrams { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
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
            MsQuicBridgeNative.SendDatagram(_context, bytes, bytes.Length) == bytes.Length;
    }
    private void Pump()
    {
        byte[] buffer = new byte[65535];
        while (!_stop.IsCancellationRequested)
        {
            int length = MsQuicBridgeNative.ReceiveDatagram(_context, buffer, buffer.Length, 250);
            IPEndPoint? remote = MsQuicBridgeNative.TryGetAddress(_context, remote: true);
            if (remote != null && !_stop.IsCancellationRequested &&
                (_lastRemoteAddress == null || !_lastRemoteAddress.Equals(remote)))
            {
                _lastRemoteAddress = remote;
                _dispatch.Writer.TryWrite((null, remote, Environment.TickCount64));
            }
            if (length == -2) break;
            if (length <= 0) continue;
            byte[] data = buffer.AsSpan(0, length).ToArray();
            IncomingDatagrams.Writer.TryWrite(data);
            _dispatch.Writer.TryWrite((data, null, Environment.TickCount64));
        }
        IncomingDatagrams.Writer.TryComplete();
        _dispatch.Writer.TryComplete();
    }
    private async Task DispatchAsync()
    {
        try
        {
            await foreach (var item in _dispatch.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (_stop.IsCancellationRequested) break;
                try
                {
                    if (item.Address != null) OnPeerAddressChanged?.Invoke(item.Address);
                    else if (Environment.TickCount64 - item.Created <= 250 && item.Data != null)
                        OnDatagramReceived?.Invoke(item.Data);
                }
                catch (Exception ex) { QuicPunchLog.Info($"[QUIC datagram handler] {ex.GetType().Name}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async Task StopAsync()
    {
        _stop.Cancel();
        _dispatch.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        lock (_nativeGate) { }
    }
    public void Dispose() => _stop.Cancel();
}
