using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;

namespace QuicPunch.Helpers;

/// <summary>
/// Low-level tuning utility for Microsoft MsQuic native engine.
/// Allows querying and dynamically switching congestion control algorithms (e.g. CUBIC to BBR),
/// stream priorities (QUIC_PARAM_STREAM_PRIORITY), Path MTU Discovery, and DSCP QoS tagging
/// on live System.Net.Quic connections via internal native SetParam calls.
/// </summary>
public static class MsQuicTuner
{
    private unsafe delegate int SetParamDelegate(IntPtr handle, uint param, uint bufferLength, IntPtr buffer);
    private unsafe delegate int GetParamDelegate(IntPtr handle, uint param, uint* bufferLength, IntPtr buffer);

    private static readonly FieldInfo? HandleField;
    private static readonly FieldInfo? StreamHandleField;
    private static readonly FieldInfo? StreamPriorityField;
    private static readonly SetParamDelegate? SetParam;
    private static readonly GetParamDelegate? GetParam;
    private static readonly bool Initialized;

    public const uint QuicParamConfigSettings = 0x03000000;
    public const uint QuicParamConnSettings = 0x05000004;
    public const uint QuicParamConnLocalAddress = 0x05000001;
    public const uint QuicParamConnRemoteAddress = 0x05000002;
    public const uint QuicParamConnStreamSchedulingScheme = 0x0500000C;
    public const uint QuicParamConnStatisticsV2 = 0x05000016;
    public const uint QuicParamConnSendDscp = 0x05000019;
    public const uint QuicParamStreamPriority = 0x08000003;
    public const uint QuicParamConnResumptionTicket = 0x05000010;

    public const int QuicSettingsSize = 144;
    public const int CongestionAlgorithmOffset = 92;
    public const ulong CongestionAlgorithmIsSetBit = 1UL << 17; // Bit 17 in IsSetFlags

    static MsQuicTuner()
    {
        try
        {
            if (!global::System.Net.Quic.QuicConnection.IsSupported)
            {
                Initialized = false;
                return;
            }

            var quicConnType = typeof(System.Net.Quic.QuicConnection);
            HandleField = quicConnType.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance);

            var quicStreamType = quicConnType.Assembly.GetType("System.Net.Quic.QuicStream");
            if (quicStreamType != null)
            {
                StreamHandleField = quicStreamType.GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance);
                StreamPriorityField = quicStreamType.GetField("_priority", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            var apiType = quicConnType.Assembly.GetType("System.Net.Quic.MsQuicApi");
            if (apiType == null) return;

            var apiProp = apiType.GetProperty("Api", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var api = apiProp?.GetValue(null);
            if (api == null) return;

            var apiTableField = apiType.GetField("<ApiTable>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            if (apiTableField == null) return;

            unsafe
            {
                IntPtr apiTablePtr = (IntPtr)Pointer.Unbox(apiTableField.GetValue(api)!);
                // In QUIC_API_TABLE: index 3 = SetParam, index 4 = GetParam
                IntPtr setParamPtr = Marshal.ReadIntPtr(apiTablePtr, 3 * IntPtr.Size);
                IntPtr getParamPtr = Marshal.ReadIntPtr(apiTablePtr, 4 * IntPtr.Size);

                SetParam = Marshal.GetDelegateForFunctionPointer<SetParamDelegate>(setParamPtr);
                GetParam = Marshal.GetDelegateForFunctionPointer<GetParamDelegate>(getParamPtr);
            }
            Initialized = true;

            try { EnsurePathMtuDiscovery(); } catch { }
        }
        catch
        {
            Initialized = false;
        }
    }

    /// <summary>
    /// Checks whether native MsQuic reflection tuning is supported on this platform and runtime.
    /// </summary>
    public static bool IsSupported => Initialized && SetParam != null && GetParam != null;

    /// <summary>
    /// Dynamically changes the congestion control algorithm of an active QUIC connection (e.g. to BBR).
    /// </summary>
    public static bool TrySetCongestionControl(System.Net.Quic.QuicConnection connection, QuicCongestionAlgorithm algorithm)
    {
        if (!Initialized || SetParam == null || HandleField == null)
            return false;

        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;

            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero)
                return false;

            Span<byte> settings = stackalloc byte[QuicSettingsSize];
            settings.Clear();

            // Set bit 17 in IsSetFlags (Anonymous1 at offset 0)
            BinaryPrimitives.WriteUInt64LittleEndian(settings[..8], CongestionAlgorithmIsSetBit);
            // Set CongestionControlAlgorithm enum value at offset 92
            BinaryPrimitives.WriteUInt16LittleEndian(settings.Slice(CongestionAlgorithmOffset, 2), (ushort)algorithm);

            unsafe
            {
                fixed (byte* p = settings)
                {
                    int status = SetParam(connHandle, QuicParamConnSettings, QuicSettingsSize, (IntPtr)p);
                    return status == 0;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Dynamically changes the congestion control algorithm of an active QUIC connection (e.g. to BBR).
    /// </summary>
    public static bool TrySetCongestionControl(global::QuicPunch.QuicConnection connection, QuicCongestionAlgorithm algorithm)
    {
        var native = connection?.NativeConnection;
        return native != null && TrySetCongestionControl(native, algorithm);
    }

    /// <summary>
    /// Queries the active congestion control algorithm of a live QUIC connection.
    /// </summary>
    public static bool TryGetCongestionControl(global::QuicPunch.QuicConnection connection, out QuicCongestionAlgorithm algorithm)
    {
        algorithm = QuicCongestionAlgorithm.Cubic;
        var native = connection?.NativeConnection;
        return native != null && TryGetCongestionControl(native, out algorithm);
    }

    /// <summary>
    /// Queries the active congestion control algorithm of a live QUIC connection.
    /// </summary>
    public static bool TryGetCongestionControl(System.Net.Quic.QuicConnection connection, out QuicCongestionAlgorithm algorithm)
    {
        algorithm = QuicCongestionAlgorithm.Cubic;
        if (!Initialized || GetParam == null || HandleField == null)
            return false;

        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;

            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero)
                return false;

            Span<byte> settings = stackalloc byte[QuicSettingsSize];
            settings.Clear();

            unsafe
            {
                fixed (byte* p = settings)
                {
                    uint len = QuicSettingsSize;
                    int status = GetParam(connHandle, QuicParamConnSettings, &len, (IntPtr)p);
                    if (status == 0)
                    {
                        ushort cc = BinaryPrimitives.ReadUInt16LittleEndian(settings.Slice(CongestionAlgorithmOffset, 2));
                        algorithm = (QuicCongestionAlgorithm)cc;
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the DSCP (Differentiated Services Code Point) QoS value on the underlying UDP socket for traffic prioritization.
    /// Voice (46 = Expedited Forwarding), Video (34 = AF41), or Custom.
    /// </summary>
    public static bool TrySetDscp(System.Net.Quic.QuicConnection connection, QuicDscpPriority priority)
    {
        return TrySetDscp(connection, (byte)priority);
    }

    /// <summary>
    /// Sets the DSCP byte on the underlying UDP socket for traffic prioritization.
    /// </summary>
    public static bool TrySetDscp(System.Net.Quic.QuicConnection connection, byte dscpValue)
    {
        if (!Initialized || SetParam == null || HandleField == null) return false;
        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;
            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            byte dscp = dscpValue;
            unsafe
            {
                int status = SetParam(connHandle, QuicParamConnSendDscp, 1, (IntPtr)(&dscp));
                return status == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the stream scheduling scheme on an active native QUIC connection (FIFO or RoundRobin).
    /// RoundRobin ensures fair multiplexing across streams without head-of-line blocking.
    /// </summary>
    public static bool TrySetStreamSchedulingScheme(System.Net.Quic.QuicConnection connection, QuicStreamSchedulingScheme scheme)
    {
        if (!Initialized || SetParam == null || HandleField == null) return false;
        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;
            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            uint val = (uint)scheme;
            unsafe
            {
                int status = SetParam(connHandle, QuicParamConnStreamSchedulingScheme, sizeof(uint), (IntPtr)(&val));
                return status == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sets the stream scheduling scheme on an active QuicPunch QUIC connection (FIFO or RoundRobin).
    /// </summary>
    public static bool TrySetStreamSchedulingScheme(global::QuicPunch.QuicConnection connection, QuicStreamSchedulingScheme scheme)
    {
        var native = connection?.NativeConnection;
        return native != null && TrySetStreamSchedulingScheme(native, scheme);
    }

    /// <summary>
    /// Queries the stream scheduling scheme of an active native QUIC connection.
    /// </summary>
    public static bool TryGetStreamSchedulingScheme(System.Net.Quic.QuicConnection connection, out QuicStreamSchedulingScheme scheme)
    {
        scheme = QuicStreamSchedulingScheme.Fifo;
        if (!Initialized || GetParam == null || HandleField == null) return false;
        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;
            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            uint val = 0;
            unsafe
            {
                uint len = sizeof(uint);
                int status = GetParam(connHandle, QuicParamConnStreamSchedulingScheme, &len, (IntPtr)(&val));
                if (status == 0)
                {
                    scheme = (QuicStreamSchedulingScheme)val;
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queries the stream scheduling scheme of an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TryGetStreamSchedulingScheme(global::QuicPunch.QuicConnection connection, out QuicStreamSchedulingScheme scheme)
    {
        scheme = QuicStreamSchedulingScheme.Fifo;
        var native = connection?.NativeConnection;
        return native != null && TryGetStreamSchedulingScheme(native, out scheme);
    }

    /// <summary>
    /// Queries real-time performance telemetry, protocol metrics, and network addresses directly from the native MsQuic engine.
    /// </summary>
    /// <param name="connection">The active <see cref="System.Net.Quic.QuicConnection"/> instance.</param>
    /// <param name="telemetry">When this method returns, contains the queried <see cref="QuicConnectionTelemetry"/>, or <c>null</c> if the query failed.</param>
    /// <returns><c>true</c> if the telemetry was successfully retrieved; otherwise, <c>false</c>.</returns>
    public static bool TryGetTelemetry(System.Net.Quic.QuicConnection connection, out QuicConnectionTelemetry telemetry)
    {
        telemetry = null!;
        if (!Initialized || GetParam == null || HandleField == null) return false;
        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;
            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            unsafe
            {
                QuicStatisticsV2 stats = default;
                uint len = (uint)sizeof(QuicStatisticsV2);
                int status = GetParam(connHandle, QuicParamConnStatisticsV2, &len, (IntPtr)(&stats));
                if (status == 0)
                {
                    double handshakeDurationMs = 0.0;
                    if (stats.TimingHandshakeFlightEnd > stats.TimingStart && stats.TimingStart > 0)
                    {
                        handshakeDurationMs = (stats.TimingHandshakeFlightEnd - stats.TimingStart) / 1000.0;
                    }

                    QuicCongestionAlgorithm? congestionAlgorithm = null;
                    if (TryGetCongestionControl(connection, out var cc))
                    {
                        congestionAlgorithm = cc;
                    }

                    TryGetLocalAddress(connection, out var localEp);
                    TryGetRemoteAddress(connection, out var remoteEp);

                    telemetry = new QuicConnectionTelemetry
                    {
                        RttMs = stats.Rtt / 1000.0,
                        MinRttMs = stats.MinRtt / 1000.0,
                        MaxRttMs = stats.MaxRtt / 1000.0,
                        RttVarianceMs = stats.RttVariance / 1000.0,
                        PathMtu = stats.SendPathMtu,
                        SendTotalPackets = stats.SendTotalPackets,
                        SendRetransmittablePackets = stats.SendRetransmittablePackets,
                        SendSuspectedLostPackets = stats.SendSuspectedLostPackets,
                        SendSpuriousLostPackets = stats.SendSpuriousLostPackets,
                        SendTotalBytes = stats.SendTotalBytes,
                        SendTotalStreamBytes = stats.SendTotalStreamBytes,
                        SendCongestionCount = stats.SendCongestionCount,
                        SendPersistentCongestionCount = stats.SendPersistentCongestionCount,
                        SendEcnCongestionCount = stats.SendEcnCongestionCount,
                        SendCongestionWindow = stats.SendCongestionWindow,
                        RecvTotalPackets = stats.RecvTotalPackets,
                        RecvReorderedPackets = stats.RecvReorderedPackets,
                        RecvDroppedPackets = stats.RecvDroppedPackets,
                        RecvDuplicatePackets = stats.RecvDuplicatePackets,
                        RecvTotalBytes = stats.RecvTotalBytes,
                        RecvTotalStreamBytes = stats.RecvTotalStreamBytes,
                        RecvDecryptionFailures = stats.RecvDecryptionFailures,
                        RecvValidAckFrames = stats.RecvValidAckFrames,
                        KeyUpdateCount = stats.KeyUpdateCount,
                        DestCidUpdateCount = stats.DestCidUpdateCount,
                        HandshakeHopLimitTtl = stats.HandshakeHopLimitTTL,
                        HandshakeDurationMs = handshakeDurationMs,
                        HandshakeClientFlight1Bytes = stats.HandshakeClientFlight1Bytes,
                        HandshakeServerFlight1Bytes = stats.HandshakeServerFlight1Bytes,
                        HandshakeClientFlight2Bytes = stats.HandshakeClientFlight2Bytes,
                        CongestionAlgorithm = congestionAlgorithm,
                        NativeLocalEndPoint = localEp,
                        NativeRemoteEndPoint = remoteEp,
                        TimestampUtc = DateTime.UtcNow
                    };
                    return true;
                }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queries the native local endpoint (IP and port) assigned by MsQuic to the connection.
    /// </summary>
    public static bool TryGetLocalAddress(System.Net.Quic.QuicConnection connection, out System.Net.IPEndPoint? endpoint)
    {
        return TryGetAddress(connection, QuicParamConnLocalAddress, out endpoint);
    }

    /// <summary>
    /// Queries the native remote endpoint (IP and port) assigned by MsQuic to the connection.
    /// </summary>
    public static bool TryGetRemoteAddress(System.Net.Quic.QuicConnection connection, out System.Net.IPEndPoint? endpoint)
    {
        return TryGetAddress(connection, QuicParamConnRemoteAddress, out endpoint);
    }

    private static bool TryGetAddress(System.Net.Quic.QuicConnection connection, uint paramId, out System.Net.IPEndPoint? endpoint)
    {
        endpoint = null;
        if (!Initialized || GetParam == null || HandleField == null) return false;
        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;
            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            Span<byte> buf = stackalloc byte[128];
            unsafe
            {
                fixed (byte* p = buf)
                {
                    uint len = 128;
                    int status = GetParam(connHandle, paramId, &len, (IntPtr)p);
                    if (status == 0)
                    {
                        endpoint = ParseQuicAddr(buf[..(int)len]);
                        return endpoint != null;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Address Family constants across operating systems
    private const short AfInet = 2;          // IPv4 (AF_INET is 2 on all platforms)
    private const short AfInet6Linux = 10;   // IPv6 on Linux/POSIX (AF_INET6 = 10)
    private const short AfInet6Windows = 23; // IPv6 on Windows Winsock (AF_INET6 = 23)

    public static System.Net.IPEndPoint? ParseQuicAddr(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 4) return null;
        short family = BinaryPrimitives.ReadInt16LittleEndian(buffer[..2]);
        ushort port = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));

        if (family == AfInet && buffer.Length >= 8)
        {
            var ip = new System.Net.IPAddress(buffer.Slice(4, 4));
            return new System.Net.IPEndPoint(ip, port);
        }
        else if ((family == AfInet6Windows || family == AfInet6Linux) && buffer.Length >= 24)
        {
            var ip = new System.Net.IPAddress(buffer.Slice(8, 16));
            return new System.Net.IPEndPoint(ip, port);
        }
        return null;
    }

    #region Stream Priority Tuning (QUIC_PARAM_STREAM_PRIORITY)

    private static IntPtr GetStreamNativeHandle(System.IO.Stream stream, out object? rawQuicStream)
    {
        rawQuicStream = null;
        if (stream == null) return IntPtr.Zero;

        object? current = stream;
        int depth = 0;
        while (current != null && depth++ < 10)
        {
            var type = current.GetType();
            if (type.FullName == "System.Net.Quic.QuicStream")
            {
                rawQuicStream = current;
                if (StreamHandleField?.GetValue(current) is SafeHandle sh && !sh.IsInvalid)
                    return sh.DangerousGetHandle();
                return IntPtr.Zero;
            }

            var innerProp = type.GetProperty("InnerStream", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         ?? type.GetProperty("Stream", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                         ?? type.GetProperty("BaseStream", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (innerProp != null)
            {
                var next = innerProp.GetValue(current);
                if (next != null && !ReferenceEquals(next, current))
                {
                    current = next;
                    continue;
                }
            }

            var innerField = type.GetField("_stream", BindingFlags.NonPublic | BindingFlags.Instance)
                          ?? type.GetField("_innerStream", BindingFlags.NonPublic | BindingFlags.Instance)
                          ?? type.GetField("_transport", BindingFlags.NonPublic | BindingFlags.Instance);
            if (innerField != null)
            {
                var next = innerField.GetValue(current);
                if (next != null && !ReferenceEquals(next, current))
                {
                    current = next;
                    continue;
                }
            }

            break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Configures the relative priority of a QUIC stream using MsQuic's native QUIC_PARAM_STREAM_PRIORITY.
    /// Range is 0x0000 (lowest) to 0xFFFF (highest, default is 0x7FFF).
    /// Higher priority streams are scheduled ahead of lower priority streams at the transport layer.
    /// </summary>
    public static bool TrySetStreamPriority(System.IO.Stream stream, ushort priority)
    {
        if (!Initialized || SetParam == null) return false;
        try
        {
            IntPtr streamHandle = GetStreamNativeHandle(stream, out var rawQuicStream);
            if (streamHandle == IntPtr.Zero) return false;

            unsafe
            {
                ushort val = priority;
                int status = SetParam(streamHandle, QuicParamStreamPriority, sizeof(ushort), (IntPtr)(&val));
                if (status == 0)
                {
                    if (rawQuicStream != null && StreamPriorityField != null)
                    {
                        try { StreamPriorityField.SetValue(rawQuicStream, (byte)(priority >> 8)); } catch { }
                    }
                    return true;
                }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Configures the relative priority of a QUIC stream using high-level priority levels.
    /// </summary>
    public static bool TrySetStreamPriority(System.IO.Stream stream, QuicStreamPriority priority) =>
        TrySetStreamPriority(stream, (ushort)priority);

    /// <summary>
    /// Queries the active priority of a QUIC stream (0x0000 to 0xFFFF, default 0x7FFF).
    /// </summary>
    public static bool TryGetStreamPriority(System.IO.Stream stream, out ushort priority)
    {
        priority = (ushort)QuicStreamPriority.Normal;
        if (!Initialized || GetParam == null) return false;
        try
        {
            IntPtr streamHandle = GetStreamNativeHandle(stream, out _);
            if (streamHandle == IntPtr.Zero) return false;

            unsafe
            {
                ushort val = 0;
                uint len = sizeof(ushort);
                int status = GetParam(streamHandle, QuicParamStreamPriority, &len, (IntPtr)(&val));
                if (status == 0)
                {
                    priority = val;
                    return true;
                }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queries the active priority level of a QUIC stream.
    /// </summary>
    public static bool TryGetStreamPriority(System.IO.Stream stream, out QuicStreamPriority priority)
    {
        if (TryGetStreamPriority(stream, out ushort val))
        {
            priority = (QuicStreamPriority)val;
            return true;
        }
        priority = QuicStreamPriority.Normal;
        return false;
    }

    #endregion

    #region Path MTU Discovery (PMTUD) & Optimal Configuration

    private static readonly object MtuConfigLock = new();
    private static bool s_isMtuConfigured;

    /// <summary>
    /// Indicates whether Path MTU Discovery and custom MTU bounds have been injected into MsQuic's configuration cache.
    /// </summary>
    public static bool IsPathMtuDiscoveryActive => s_isMtuConfigured;

    /// <summary>
    /// Queries the dynamically discovered Path MTU in bytes from an active native QUIC connection.
    /// </summary>
    public static bool TryGetPathMtu(System.Net.Quic.QuicConnection connection, out ushort pathMtu)
    {
        pathMtu = 1500;
        if (TryGetTelemetry(connection, out var telem) && telem.PathMtu > 0)
        {
            pathMtu = telem.PathMtu;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Queries the dynamically discovered Path MTU in bytes from an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TryGetPathMtu(global::QuicPunch.QuicConnection connection, out ushort pathMtu)
    {
        pathMtu = 1500;
        var native = connection?.NativeConnection;
        return native != null && TryGetPathMtu(native, out pathMtu);
    }

    /// <summary>
    /// Automatically ensures Path MTU Discovery (PMTUD) and optimal high-performance settings
    /// (expanded 16MB/4MB flow control windows, 0-RTT resumption, and 5s keepalive) are activated across all MsQuic configurations.
    /// </summary>
    public static bool EnsurePathMtuDiscovery(ushort minMtu = 1280, ushort maxMtu = 1500) =>
        EnsureOptimalConfiguration(minMtu, maxMtu);

    /// <summary>
    /// Automatically injects high-throughput flow control windows, 0-RTT server resumption, native transport keepalive,
    /// BBR congestion control, Pacing, ECN, HyStart, connection migration, datagrams, and PMTUD bounds into MsQuic's configuration cache.
    /// </summary>
    public static bool EnsureOptimalConfiguration(
        ushort minMtu = 1280,
        ushort maxMtu = 1500,
        uint connFlowControlWindow = 16 * 1024 * 1024,
        uint streamRecvWindow = 4 * 1024 * 1024,
        uint keepAliveIntervalMs = 5000,
        ulong mtuDiscoveryTimeoutUs = 600_000_000UL,
        byte mtuDiscoveryMissingProbeCount = 3,
        uint initialWindowPackets = 10,
        bool enableZeroRtt = true,
        bool pacing = true,
        bool ecn = true,
        bool hyStart = true)
    {
        if (!Initialized || SetParam == null) return false;
        lock (MtuConfigLock)
        {
            try
            {
                var asm = typeof(global::System.Net.Quic.QuicConnection).Assembly;
                var configType = asm.GetType("System.Net.Quic.MsQuicConfiguration");
                var cacheField = configType?.GetField("s_configurationCache", BindingFlags.NonPublic | BindingFlags.Static);
                var cacheObj = cacheField?.GetValue(null);
                if (cacheObj == null) return false;

                byte[] cfgSettings = new byte[QuicSettingsSize];
                // Bit  3: MtuDiscoverySearchCompleteTimeoutUs
                // Bit  6: StreamRecvWindowDefault
                // Bit  7: StreamRecvBufferDefault
                // Bit  8: ConnFlowControlWindow
                // Bit 11: InitialWindowPackets
                // Bit 16: KeepAliveIntervalMs
                // Bit 17: CongestionControlAlgorithm (BBR)
                // Bit 22: MinimumMtu
                // Bit 23: MaximumMtu
                // Bit 24: SendBufferingEnabled
                // Bit 25: PacingEnabled
                // Bit 26: MigrationEnabled
                // Bit 27: DatagramReceiveEnabled
                // Bit 28: ServerResumptionLevel
                // Bit 30: MtuDiscoveryMissingProbeCount
                // Bit 32: GreaseQuicBitEnabled
                // Bit 33: EcnEnabled
                // Bit 34: HyStartEnabled
                // Bit 35: StreamRecvWindowBidiLocalDefault
                // Bit 36: StreamRecvWindowBidiRemoteDefault
                // Bit 37: StreamRecvWindowUnidiDefault
                ulong isSetFlags =
                    (1UL << 3)
                    | (1UL << 6)
                    | (1UL << 7)
                    | (1UL << 8)
                    | (1UL << 11)
                    | (1UL << 16)
                    | (1UL << 17)
                    | (1UL << 22)
                    | (1UL << 23)
                    | (1UL << 24)
                    | (1UL << 25)
                    | (1UL << 26)
                    | (1UL << 27)
                    | (1UL << 28)
                    | (1UL << 30)
                    | (1UL << 32)
                    | (1UL << 33)
                    | (1UL << 34)
                    | (1UL << 35)
                    | (1UL << 36)
                    | (1UL << 37);

                BinaryPrimitives.WriteUInt64LittleEndian(cfgSettings.AsSpan(0, 8), isSetFlags);

                // MTU search complete timeout (600s = 600,000,000 us)
                BinaryPrimitives.WriteUInt64LittleEndian(cfgSettings.AsSpan(32, 8), mtuDiscoveryTimeoutUs);

                // Stream receive default buffer & window (4 MB)
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(48, 4), streamRecvWindow);
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(52, 4), streamRecvWindow);

                // Connection flow control window (16 MB)
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(56, 4), connFlowControlWindow);

                // Initial window packets (IW10 standard = 10)
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(68, 4), initialWindowPackets);

                // KeepAlive interval (5000 ms)
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(88, 4), keepAliveIntervalMs);

                // Congestion Control Algorithm (1 = BBR)
                BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(92, 2), (ushort)QuicCongestionAlgorithm.Bbr);

                // Minimum & Maximum MTU
                BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(102, 2), minMtu);
                BinaryPrimitives.WriteUInt16LittleEndian(cfgSettings.AsSpan(104, 2), maxMtu);

                // Offset 106: _bitfield
                // bit 0 = SendBufferingEnabled
                // bit 1 = PacingEnabled
                // bit 2 = MigrationEnabled
                // bit 3 = DatagramReceiveEnabled
                // bits 4-5 = ServerResumptionLevel (2 = QUIC_SERVER_RESUME_AND_ZERORTT)
                // bit 6 = GreaseQuicBitEnabled
                // bit 7 = EcnEnabled
                byte bitfield = (byte)((1 << 0) | (1 << 2) | (1 << 3) | (1 << 6));
                if (pacing) bitfield |= (1 << 1);
                if (enableZeroRtt) bitfield |= (byte)(2 << 4);
                if (ecn) bitfield |= (byte)(1 << 7);
                cfgSettings[106] = bitfield;

                // MTU missing probe count (3)
                cfgSettings[108] = mtuDiscoveryMissingProbeCount;

                // Flags at offset 120: bit 0 = HyStartEnabled
                if (hyStart)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(cfgSettings.AsSpan(120, 8), 1UL);
                }

                // Stream Bidi / Unidi receive windows (4 MB)
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(128, 4), streamRecvWindow);
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(132, 4), streamRecvWindow);
                BinaryPrimitives.WriteUInt32LittleEndian(cfgSettings.AsSpan(136, 4), streamRecvWindow);

                int patched = PatchConfigurations(cacheObj, cfgSettings);
                s_isMtuConfigured = patched > 0;
                return s_isMtuConfigured;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Atomically applies optimal native MsQuic tuning to an active connection:
    /// BBR congestion control, Pacing, HyStart, Datagrams, Migration, Send Buffering, Grease Bit,
    /// 16MB connection flow control window, 4MB stream receive windows, 5s Keepalive, PMTUD discovery timing,
    /// and Round-Robin stream multiplexing.
    /// </summary>
    public static bool TryApplyOptimalTuning(
        System.Net.Quic.QuicConnection connection,
        QuicCongestionAlgorithm congestionAlgorithm = QuicCongestionAlgorithm.Bbr,
        uint connFlowControlWindow = 16 * 1024 * 1024,
        uint streamRecvWindow = 4 * 1024 * 1024,
        uint keepAliveIntervalMs = 5000,
        ulong mtuDiscoveryTimeoutUs = 600_000_000UL,
        byte mtuDiscoveryMissingProbeCount = 3,
        uint initialWindowPackets = 10,
        bool pacing = true,
        bool datagrams = true,
        bool migration = true,
        bool sendBuffering = true,
        bool greaseQuicBit = true,
        bool hyStart = true,
        QuicStreamSchedulingScheme streamSchedulingScheme = QuicStreamSchedulingScheme.RoundRobin)
    {
        if (!Initialized || SetParam == null || HandleField == null)
            return false;

        try
        {
            // First ensure any configurations in the cache are also patched
            EnsureOptimalConfiguration(
                connFlowControlWindow: connFlowControlWindow,
                streamRecvWindow: streamRecvWindow,
                keepAliveIntervalMs: keepAliveIntervalMs,
                mtuDiscoveryTimeoutUs: mtuDiscoveryTimeoutUs,
                mtuDiscoveryMissingProbeCount: mtuDiscoveryMissingProbeCount,
                initialWindowPackets: initialWindowPackets,
                pacing: pacing,
                hyStart: hyStart);

            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;

            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero)
                return false;

            Span<byte> settings = stackalloc byte[QuicSettingsSize];
            settings.Clear();

            ulong isSet =
                (1UL << 3)  | // MtuDiscoverySearchCompleteTimeoutUs
                (1UL << 6)  | // StreamRecvWindowDefault
                (1UL << 7)  | // StreamRecvBufferDefault
                (1UL << 8)  | // ConnFlowControlWindow
                (1UL << 11) | // InitialWindowPackets
                (1UL << 16) | // KeepAliveIntervalMs
                (1UL << 17) | // CongestionControlAlgorithm (BBR)
                (1UL << 24) | // SendBufferingEnabled
                (1UL << 25) | // PacingEnabled
                (1UL << 26) | // MigrationEnabled
                (1UL << 27) | // DatagramReceiveEnabled
                (1UL << 30) | // MtuDiscoveryMissingProbeCount
                (1UL << 32) | // GreaseQuicBitEnabled
                (1UL << 34) | // HyStartEnabled
                (1UL << 35) | // StreamRecvWindowBidiLocalDefault
                (1UL << 36) | // StreamRecvWindowBidiRemoteDefault
                (1UL << 37);  // StreamRecvWindowUnidiDefault

            BinaryPrimitives.WriteUInt64LittleEndian(settings[..8], isSet);
            BinaryPrimitives.WriteUInt64LittleEndian(settings.Slice(32, 8), mtuDiscoveryTimeoutUs);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(48, 4), streamRecvWindow);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(52, 4), streamRecvWindow);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(56, 4), connFlowControlWindow);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(68, 4), initialWindowPackets);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(88, 4), keepAliveIntervalMs);
            BinaryPrimitives.WriteUInt16LittleEndian(settings.Slice(92, 2), (ushort)congestionAlgorithm);

            byte bf = 0;
            if (sendBuffering) bf |= (1 << 0);
            if (pacing) bf |= (1 << 1);
            if (migration) bf |= (1 << 2);
            if (datagrams) bf |= (1 << 3);
            if (greaseQuicBit) bf |= (1 << 6);
            settings[106] = bf;
            settings[108] = mtuDiscoveryMissingProbeCount;

            if (hyStart)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(settings.Slice(120, 8), 1UL);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(128, 4), streamRecvWindow);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(132, 4), streamRecvWindow);
            BinaryPrimitives.WriteUInt32LittleEndian(settings.Slice(136, 4), streamRecvWindow);

            unsafe
            {
                fixed (byte* p = settings)
                {
                    int status = SetParam(connHandle, QuicParamConnSettings, QuicSettingsSize, (IntPtr)p);
                    if (status != 0)
                        return false;
                }
            }

            TrySetStreamSchedulingScheme(connection, streamSchedulingScheme);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Atomically applies optimal native MsQuic tuning to an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TryApplyOptimalTuning(
        global::QuicPunch.QuicConnection connection,
        QuicCongestionAlgorithm congestionAlgorithm = QuicCongestionAlgorithm.Bbr,
        uint connFlowControlWindow = 16 * 1024 * 1024,
        uint streamRecvWindow = 4 * 1024 * 1024,
        uint keepAliveIntervalMs = 5000,
        ulong mtuDiscoveryTimeoutUs = 600_000_000UL,
        byte mtuDiscoveryMissingProbeCount = 3,
        uint initialWindowPackets = 10,
        bool pacing = true,
        bool datagrams = true,
        bool migration = true,
        bool sendBuffering = true,
        bool greaseQuicBit = true,
        bool hyStart = true,
        QuicStreamSchedulingScheme streamSchedulingScheme = QuicStreamSchedulingScheme.RoundRobin)
    {
        var native = connection?.NativeConnection;
        return native != null && TryApplyOptimalTuning(
            native,
            congestionAlgorithm,
            connFlowControlWindow,
            streamRecvWindow,
            keepAliveIntervalMs,
            mtuDiscoveryTimeoutUs,
            mtuDiscoveryMissingProbeCount,
            initialWindowPackets,
            pacing,
            datagrams,
            migration,
            sendBuffering,
            greaseQuicBit,
            hyStart,
            streamSchedulingScheme);
    }

    /// <summary>
    /// Configures Path MTU Discovery parameters on an active connection and ensures MTU bounds are active in the configuration cache.
    /// </summary>
    public static bool TrySetPathMtuDiscovery(
        System.Net.Quic.QuicConnection connection,
        ushort minMtu = 1280,
        ushort maxMtu = 1500,
        ulong timeoutUs = 600_000_000UL,
        byte missingProbeCount = 3)
    {
        EnsureOptimalConfiguration(minMtu, maxMtu, mtuDiscoveryTimeoutUs: timeoutUs, mtuDiscoveryMissingProbeCount: missingProbeCount);

        if (!Initialized || SetParam == null || HandleField == null)
            return false;

        try
        {
            if (HandleField.GetValue(connection) is not SafeHandle safeHandle || safeHandle.IsInvalid)
                return false;

            IntPtr connHandle = safeHandle.DangerousGetHandle();
            if (connHandle == IntPtr.Zero)
                return false;

            Span<byte> settings = stackalloc byte[QuicSettingsSize];
            settings.Clear();

            // Set Bit 3 (timeout) and Bit 30 (missing probe count)
            ulong isSet = (1UL << 3) | (1UL << 30);
            BinaryPrimitives.WriteUInt64LittleEndian(settings[..8], isSet);
            BinaryPrimitives.WriteUInt64LittleEndian(settings.Slice(32, 8), timeoutUs);
            settings[108] = missingProbeCount;

            unsafe
            {
                fixed (byte* p = settings)
                {
                    int status = SetParam(connHandle, QuicParamConnSettings, QuicSettingsSize, (IntPtr)p);
                    return status == 0;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Configures Path MTU Discovery parameters on an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TrySetPathMtuDiscovery(
        global::QuicPunch.QuicConnection connection,
        ushort minMtu = 1280,
        ushort maxMtu = 1500,
        ulong timeoutUs = 600_000_000UL,
        byte missingProbeCount = 3)
    {
        var native = connection?.NativeConnection;
        return native != null && TrySetPathMtuDiscovery(native, minMtu, maxMtu, timeoutUs, missingProbeCount);
    }

    #endregion

    #region Flow Control & Connection Tuning

    /// <summary>
    /// Dynamically sets the flow control window sizes on an active native QUIC connection.
    /// </summary>
    public static bool TrySetFlowControlWindows(
        System.Net.Quic.QuicConnection connection,
        uint connectionWindowBytes = 16 * 1024 * 1024,
        uint streamWindowBytes = 4 * 1024 * 1024)
    {
        if (!Initialized || SetParam == null || HandleField == null) return false;
        try
        {
            var handleObj = HandleField.GetValue(connection);
            if (handleObj is not SafeHandle sh || sh.IsInvalid) return false;
            IntPtr connHandle = sh.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            byte[] connSettings = new byte[QuicSettingsSize];
            ulong isSet = (1UL << 6) | (1UL << 7) | (1UL << 8) | (1UL << 35) | (1UL << 36) | (1UL << 37);
            BinaryPrimitives.WriteUInt64LittleEndian(connSettings.AsSpan(0, 8), isSet);

            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(48, 4), streamWindowBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(52, 4), streamWindowBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(56, 4), connectionWindowBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(128, 4), streamWindowBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(132, 4), streamWindowBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(136, 4), streamWindowBytes);

            unsafe
            {
                fixed (byte* p = connSettings)
                {
                    return SetParam(connHandle, QuicParamConnSettings, QuicSettingsSize, (IntPtr)p) == 0;
                }
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Dynamically sets the flow control window sizes on an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TrySetFlowControlWindows(
        global::QuicPunch.QuicConnection connection,
        uint connectionWindowBytes = 16 * 1024 * 1024,
        uint streamWindowBytes = 4 * 1024 * 1024)
    {
        var native = connection?.NativeConnection;
        return native != null && TrySetFlowControlWindows(native, connectionWindowBytes, streamWindowBytes);
    }

    /// <summary>
    /// Dynamically adjusts the keepalive PING interval on an active native QUIC connection.
    /// </summary>
    public static bool TrySetKeepAliveInterval(System.Net.Quic.QuicConnection connection, uint intervalMs)
    {
        if (!Initialized || SetParam == null || HandleField == null) return false;
        try
        {
            var handleObj = HandleField.GetValue(connection);
            if (handleObj is not SafeHandle sh || sh.IsInvalid) return false;
            IntPtr connHandle = sh.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            byte[] connSettings = new byte[QuicSettingsSize];
            ulong isSet = 1UL << 16; // KeepAliveIntervalMs
            BinaryPrimitives.WriteUInt64LittleEndian(connSettings.AsSpan(0, 8), isSet);
            BinaryPrimitives.WriteUInt32LittleEndian(connSettings.AsSpan(88, 4), intervalMs);

            unsafe
            {
                fixed (byte* p = connSettings)
                {
                    return SetParam(connHandle, QuicParamConnSettings, QuicSettingsSize, (IntPtr)p) == 0;
                }
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Dynamically adjusts the keepalive PING interval on an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TrySetKeepAliveInterval(global::QuicPunch.QuicConnection connection, uint intervalMs)
    {
        var native = connection?.NativeConnection;
        return native != null && TrySetKeepAliveInterval(native, intervalMs);
    }

    /// <summary>
    /// Sends an immediate native transport PING / keepalive signal across the QUIC connection to measure RTT and keep NAT punchholes alive.
    /// </summary>
    public static bool TrySendTransportPing(global::QuicPunch.QuicConnection connection)
    {
        if (connection == null) return false;
        if (connection.DatagramChannel != null)
        {
            ReadOnlySpan<byte> pingByte = stackalloc byte[1] { 0xFF };
            if (connection.SendDatagram(pingByte))
                return true;
        }

        var native = connection.NativeConnection;
        return native != null && TrySetKeepAliveInterval(native, 5000);
    }

    /// <summary>
    /// Attempts to retrieve the TLS 1.3 0-RTT session resumption ticket from an active native client connection.
    /// </summary>
    public static bool TryGetResumptionTicket(System.Net.Quic.QuicConnection connection, out byte[] ticket)
    {
        ticket = Array.Empty<byte>();
        if (!Initialized || GetParam == null || HandleField == null) return false;
        try
        {
            var handleObj = HandleField.GetValue(connection);
            if (handleObj is not SafeHandle sh || sh.IsInvalid) return false;
            IntPtr connHandle = sh.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            unsafe
            {
                uint bufferLen = 0;
                int status = GetParam(connHandle, QuicParamConnResumptionTicket, &bufferLen, IntPtr.Zero);
                if (bufferLen > 0)
                {
                    byte[] buf = new byte[bufferLen];
                    fixed (byte* pb = buf)
                    {
                        if (GetParam(connHandle, QuicParamConnResumptionTicket, &bufferLen, (IntPtr)pb) == 0)
                        {
                            ticket = buf;
                            return true;
                        }
                    }
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Attempts to retrieve the TLS 1.3 0-RTT session resumption ticket from an active QuicPunch client connection.
    /// </summary>
    public static bool TryGetResumptionTicket(global::QuicPunch.QuicConnection connection, out byte[] ticket)
    {
        ticket = Array.Empty<byte>();
        var native = connection?.NativeConnection;
        return native != null && TryGetResumptionTicket(native, out ticket);
    }

    /// <summary>
    /// Sets a TLS 1.3 0-RTT session resumption ticket on a native client connection prior to connecting.
    /// </summary>
    public static bool TrySetResumptionTicket(System.Net.Quic.QuicConnection connection, ReadOnlySpan<byte> ticket)
    {
        if (!Initialized || SetParam == null || HandleField == null || ticket.IsEmpty) return false;
        try
        {
            var handleObj = HandleField.GetValue(connection);
            if (handleObj is not SafeHandle sh || sh.IsInvalid) return false;
            IntPtr connHandle = sh.DangerousGetHandle();
            if (connHandle == IntPtr.Zero) return false;

            unsafe
            {
                fixed (byte* pt = ticket)
                {
                    return SetParam(connHandle, QuicParamConnResumptionTicket, (uint)ticket.Length, (IntPtr)pt) == 0;
                }
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Sets a TLS 1.3 0-RTT session resumption ticket on a QuicPunch client connection prior to connecting.
    /// </summary>
    public static bool TrySetResumptionTicket(global::QuicPunch.QuicConnection connection, ReadOnlySpan<byte> ticket)
    {
        var native = connection?.NativeConnection;
        return native != null && TrySetResumptionTicket(native, ticket);
    }

    #endregion


    private static int PatchConfigurations(object cacheObj, byte[] cfgSettings)
    {
        int count = 0;
        var curType = cacheObj.GetType();
        while (curType != null && curType != typeof(object))
        {
            foreach (var f in curType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                var val = f.GetValue(cacheObj);
                if (val is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry entry in dict)
                    {
                        if (entry.Value is SafeHandle sh && !sh.IsInvalid)
                        {
                            IntPtr hConfig = sh.DangerousGetHandle();
                            unsafe
                            {
                                fixed (byte* p = cfgSettings)
                                {
                                    if (SetParam!(hConfig, QuicParamConfigSettings, QuicSettingsSize, (IntPtr)p) == 0)
                                    {
                                        count++;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            curType = curType.BaseType;
        }
        return count;
    }
}

public enum QuicCongestionAlgorithm : ushort
{
    Cubic = 0,
    Bbr = 1
}

/// <summary>
/// Scheduling scheme used by MsQuic to schedule packets across multiple streams on a connection.
/// </summary>
public enum QuicStreamSchedulingScheme : uint
{
    Fifo = 0,
    RoundRobin = 1
}

public enum QuicDscpPriority : byte
{
    Default = 0,
    LowPriority = 8,
    Video = 34,      // Assured Forwarding 41 (AF41)
    Voice = 46       // Expedited Forwarding (EF)
}

/// <summary>
/// Relative priority levels for QUIC streams (maps to MsQuic uint16 priority 0..65535).
/// Data on streams with higher priority is scheduled and transmitted ahead of lower-priority streams.
/// </summary>
public enum QuicStreamPriority : ushort
{
    Lowest = 0x0000,
    VeryLow = 0x2000,
    Low = 0x4000,
    Normal = 0x7FFF,   // Default in MsQuic (32767)
    High = 0xC000,
    VeryHigh = 0xE000,
    Critical = 0xFFFF   // Highest priority (65535)
}

[StructLayout(LayoutKind.Sequential)]
public struct QuicStatisticsV2
{
    public ulong CorrelationId;
    public uint Flags;
    public uint Rtt;                           // Microseconds
    public uint MinRtt;                        // Microseconds
    public uint MaxRtt;                        // Microseconds
    public ulong TimingStart;
    public ulong TimingInitialFlightEnd;
    public ulong TimingHandshakeFlightEnd;
    public uint HandshakeClientFlight1Bytes;
    public uint HandshakeServerFlight1Bytes;
    public uint HandshakeClientFlight2Bytes;
    public ushort SendPathMtu;
    // 2 bytes padding automatically inserted by runtime for 8-byte alignment of ulong
    public ulong SendTotalPackets;
    public ulong SendRetransmittablePackets;
    public ulong SendSuspectedLostPackets;
    public ulong SendSpuriousLostPackets;
    public ulong SendTotalBytes;
    public ulong SendTotalStreamBytes;
    public uint SendCongestionCount;
    public uint SendPersistentCongestionCount;
    public ulong RecvTotalPackets;
    public ulong RecvReorderedPackets;
    public ulong RecvDroppedPackets;
    public ulong RecvDuplicatePackets;
    public ulong RecvTotalBytes;
    public ulong RecvTotalStreamBytes;
    public ulong RecvDecryptionFailures;
    public ulong RecvValidAckFrames;
    public uint KeyUpdateCount;
    public uint SendCongestionWindow;
    public uint DestCidUpdateCount;
    public uint SendEcnCongestionCount;
    public byte HandshakeHopLimitTTL;
    // 3 bytes padding
    public uint RttVariance;
}

/// <summary>
/// Represents real-time performance telemetry, protocol statistics, and connection metadata
/// queried directly from the native MsQuic engine.
/// </summary>
public record QuicConnectionTelemetry
{
    /// <summary>Gets the smoothed round-trip time in milliseconds.</summary>
    public double RttMs { get; init; }

    /// <summary>Gets the minimum round-trip time observed on the connection in milliseconds.</summary>
    public double MinRttMs { get; init; }

    /// <summary>Gets the maximum round-trip time observed on the connection in milliseconds.</summary>
    public double MaxRttMs { get; init; }

    /// <summary>Gets the round-trip time variance in milliseconds.</summary>
    public double RttVarianceMs { get; init; }

    /// <summary>Gets the current path Maximum Transmission Unit (MTU) in bytes.</summary>
    public ushort PathMtu { get; init; }

    /// <summary>Gets the total number of packets transmitted on the connection.</summary>
    public ulong SendTotalPackets { get; init; }

    /// <summary>Gets the total number of retransmittable packets transmitted.</summary>
    public ulong SendRetransmittablePackets { get; init; }

    /// <summary>Gets the total number of transmitted packets suspected to have been lost.</summary>
    public ulong SendSuspectedLostPackets { get; init; }

    /// <summary>Gets the total number of transmitted packets spuriously marked as lost.</summary>
    public ulong SendSpuriousLostPackets { get; init; }

    /// <summary>Gets the total number of bytes transmitted on the wire, including headers and datagrams.</summary>
    public ulong SendTotalBytes { get; init; }

    /// <summary>Gets the total number of application stream payload bytes transmitted.</summary>
    public ulong SendTotalStreamBytes { get; init; }

    /// <summary>Gets the total number of congestion window reduction events encountered.</summary>
    public uint SendCongestionCount { get; init; }

    /// <summary>Gets the total number of persistent congestion events encountered.</summary>
    public uint SendPersistentCongestionCount { get; init; }

    /// <summary>Gets the total number of Explicit Congestion Notification (ECN-CE) marks received.</summary>
    public uint SendEcnCongestionCount { get; init; }

    /// <summary>Gets the current congestion window size in bytes.</summary>
    public uint SendCongestionWindow { get; init; }

    /// <summary>Gets the total number of packets received on the connection.</summary>
    public ulong RecvTotalPackets { get; init; }

    /// <summary>Gets the total number of packets received out of sequence.</summary>
    public ulong RecvReorderedPackets { get; init; }

    /// <summary>Gets the total number of packets dropped by the transport pipeline.</summary>
    public ulong RecvDroppedPackets { get; init; }

    /// <summary>Gets the total number of duplicate packets received.</summary>
    public ulong RecvDuplicatePackets { get; init; }

    /// <summary>Gets the total number of bytes received on the wire, including headers and datagrams.</summary>
    public ulong RecvTotalBytes { get; init; }

    /// <summary>Gets the total number of application stream payload bytes received.</summary>
    public ulong RecvTotalStreamBytes { get; init; }

    /// <summary>Gets the total number of packets that failed cryptographic integrity verification.</summary>
    public ulong RecvDecryptionFailures { get; init; }

    /// <summary>Gets the total number of valid acknowledgment (ACK) frames processed.</summary>
    public ulong RecvValidAckFrames { get; init; }

    /// <summary>Gets the total number of TLS 1.3 cryptographic key update rotations executed.</summary>
    public uint KeyUpdateCount { get; init; }

    /// <summary>Gets the total number of destination Connection ID migrations observed.</summary>
    public uint DestCidUpdateCount { get; init; }

    /// <summary>Gets the network hop limit (TTL) recorded during initial flight exchange.</summary>
    public byte HandshakeHopLimitTtl { get; init; }

    /// <summary>Gets the handshake duration in milliseconds between connection start and handshake completion.</summary>
    public double HandshakeDurationMs { get; init; }

    /// <summary>Gets the byte length of client initial cryptographic parameters.</summary>
    public uint HandshakeClientFlight1Bytes { get; init; }

    /// <summary>Gets the byte length of server handshake response flight.</summary>
    public uint HandshakeServerFlight1Bytes { get; init; }

    /// <summary>Gets the byte length of client handshake completion flight.</summary>
    public uint HandshakeClientFlight2Bytes { get; init; }

    /// <summary>Gets the active congestion control algorithm configured on this connection.</summary>
    public QuicCongestionAlgorithm? CongestionAlgorithm { get; init; }

    /// <summary>Gets the name of the active congestion control algorithm.</summary>
    public string CongestionAlgorithmName => CongestionAlgorithm?.ToString() ?? "Unknown";

    /// <summary>Gets the physical local IP address and port assigned by the native transport layer.</summary>
    public System.Net.IPEndPoint? NativeLocalEndPoint { get; init; }

    /// <summary>Gets the physical remote IP address and port targeted by the native transport layer.</summary>
    public System.Net.IPEndPoint? NativeRemoteEndPoint { get; init; }

    /// <summary>Gets the negotiated TLS cipher suite.</summary>
    public string? CipherSuite { get; init; }

    /// <summary>Gets the negotiated Application-Layer Protocol Negotiation (ALPN) token.</summary>
    public string? Alpn { get; init; }

    /// <summary>Gets the TLS security protocol version.</summary>
    public string? SslProtocol { get; init; }

    /// <summary>Gets the targeted hostname or Server Name Indication (SNI).</summary>
    public string? TargetHostName { get; init; }

    /// <summary>Gets the UTC timestamp at which this telemetry sample was queried.</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Gets the estimated packet loss ratio based on total and lost transmitted packets.</summary>
    public double PacketLossRatio => SendTotalPackets == 0 ? 0.0 :
        Math.Max(0.0, (double)(SendSuspectedLostPackets - SendSpuriousLostPackets) / SendTotalPackets);
}

/// <summary>
/// Extension methods for setting and querying QUIC stream priorities directly on Stream instances.
/// </summary>
public static class QuicStreamExtensions
{
    /// <summary>
    /// Sets the relative transport priority for this QUIC stream.
    /// </summary>
    public static bool SetQuicPriority(this System.IO.Stream stream, QuicStreamPriority priority) =>
        MsQuicTuner.TrySetStreamPriority(stream, priority);

    /// <summary>
    /// Sets the raw numeric transport priority (0x0000 to 0xFFFF) for this QUIC stream.
    /// </summary>
    public static bool SetQuicPriority(this System.IO.Stream stream, ushort priority) =>
        MsQuicTuner.TrySetStreamPriority(stream, priority);

    /// <summary>
    /// Gets the current relative transport priority for this QUIC stream.
    /// </summary>
    public static QuicStreamPriority GetQuicPriority(this System.IO.Stream stream) =>
        MsQuicTuner.TryGetStreamPriority(stream, out QuicStreamPriority p) ? p : QuicStreamPriority.Normal;
}

/// <summary>
/// Extension methods for applying optimal MsQuic native tunings directly on QuicConnection instances.
/// </summary>
public static class QuicConnectionExtensions
{
    /// <summary>
    /// Atomically applies BBR congestion control, Pacing, HyStart, PMTUD, 16MB/4MB flow control windows, and keepalive to this connection.
    /// </summary>
    public static bool ApplyOptimalTuning(this System.Net.Quic.QuicConnection connection) =>
        MsQuicTuner.TryApplyOptimalTuning(connection);

    /// <summary>
    /// Atomically applies BBR congestion control, Pacing, HyStart, PMTUD, 16MB/4MB flow control windows, and keepalive to this connection.
    /// </summary>
    public static bool ApplyOptimalTuning(this global::QuicPunch.QuicConnection connection) =>
        MsQuicTuner.TryApplyOptimalTuning(connection);

    /// <summary>
    /// Queries the active congestion control algorithm of this native QUIC connection.
    /// </summary>
    public static bool TryGetCongestionControl(this System.Net.Quic.QuicConnection connection, out QuicCongestionAlgorithm algorithm) =>
        MsQuicTuner.TryGetCongestionControl(connection, out algorithm);

    /// <summary>
    /// Queries the active congestion control algorithm of this QuicPunch QUIC connection.
    /// </summary>
    public static bool TryGetCongestionControl(this global::QuicPunch.QuicConnection connection, out QuicCongestionAlgorithm algorithm) =>
        MsQuicTuner.TryGetCongestionControl(connection, out algorithm);

    /// <summary>
    /// Configures Path MTU Discovery parameters on an active native QUIC connection.
    /// </summary>
    public static bool TrySetPathMtuDiscovery(this System.Net.Quic.QuicConnection connection, ushort minMtu = 1280, ushort maxMtu = 1500, ulong timeoutUs = 600_000_000UL, byte missingProbeCount = 3) =>
        MsQuicTuner.TrySetPathMtuDiscovery(connection, minMtu, maxMtu, timeoutUs, missingProbeCount);

    /// <summary>
    /// Configures Path MTU Discovery parameters on an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TrySetPathMtuDiscovery(this global::QuicPunch.QuicConnection connection, ushort minMtu = 1280, ushort maxMtu = 1500, ulong timeoutUs = 600_000_000UL, byte missingProbeCount = 3) =>
        MsQuicTuner.TrySetPathMtuDiscovery(connection, minMtu, maxMtu, timeoutUs, missingProbeCount);

    /// <summary>
    /// Sets the stream scheduling scheme on an active native QUIC connection (FIFO or RoundRobin).
    /// </summary>
    public static bool TrySetStreamSchedulingScheme(this System.Net.Quic.QuicConnection connection, QuicStreamSchedulingScheme scheme) =>
        MsQuicTuner.TrySetStreamSchedulingScheme(connection, scheme);

    /// <summary>
    /// Sets the stream scheduling scheme on an active QuicPunch QUIC connection (FIFO or RoundRobin).
    /// </summary>
    public static bool TrySetStreamSchedulingScheme(this global::QuicPunch.QuicConnection connection, QuicStreamSchedulingScheme scheme) =>
        MsQuicTuner.TrySetStreamSchedulingScheme(connection, scheme);

    /// <summary>
    /// Queries the stream scheduling scheme of an active native QUIC connection.
    /// </summary>
    public static bool TryGetStreamSchedulingScheme(this System.Net.Quic.QuicConnection connection, out QuicStreamSchedulingScheme scheme) =>
        MsQuicTuner.TryGetStreamSchedulingScheme(connection, out scheme);

    /// <summary>
    /// Queries the stream scheduling scheme of an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TryGetStreamSchedulingScheme(this global::QuicPunch.QuicConnection connection, out QuicStreamSchedulingScheme scheme) =>
        MsQuicTuner.TryGetStreamSchedulingScheme(connection, out scheme);
}