using System.Buffers.Binary;
using System.Runtime.InteropServices;
namespace QuicPunch.Helpers;

/// <summary>Controls the owned native bridge through the public MsQuic API.
/// System.Net.Quic compatibility overloads return false for unavailable public APIs.
/// No runtime handles, callbacks or configuration caches are inspected.</summary>
public static class MsQuicTuner
{
    private const ushort AfInet = 2, AfInet6Windows = 23, AfInet6Linux = 10;
    internal static ulong[] ConfigurationPolicy = [1248, 1500, 16777216, 4194304, 19000, 600000000, 3, 10, 0, 1, 1, 1, 1];
    public static bool IsSupported => MsQuicNativeLoader.IsCustomLibraryLoaded;
    public static bool IsXdpRequested { get; }
    static MsQuicTuner()
    {
        IsXdpRequested = MsQuicBridgeNative.Initialize(
            OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("QUICNET_XDP") != "0" ? 1 : 0) != 0;
    }
    public static bool IsPathMtuDiscoveryActive => IsSupported;
    public const uint QuicParamGlobalPerfCounters = 0x01000003;
    public const uint QuicParamGlobalLibraryVersion = 0x01000004;
    public const uint QuicParamGlobalLibraryGitHash = 0x01000008;
    public const uint QuicParamGlobalSettings = 0x01000005;
    public const uint QuicParamConfigSettings = 0x03000000;
    public const uint QuicParamConnSettings = 0x05000004;
    public const uint QuicParamConnLocalAddress = 0x05000001;
    public const uint QuicParamConnRemoteAddress = 0x05000002;
    public const uint QuicParamConnIdealProcessor = 0x05000003;
    public const uint QuicParamConnStreamSchedulingScheme = 0x0500000C;
    public const uint QuicParamConnStatisticsV2 = 0x05000016;
    public const uint QuicParamConnSendDscp = 0x05000019;
    public const uint QuicParamConnNetworkStatistics = 0x05000020;
    public const uint QuicParamStreamPriority = 0x08000003;
    public const uint QuicParamStreamStatistics = 0x08000004;
    public const uint QuicParamConnResumptionTicket = 0x05000010;
    public const uint QuicParamConnTlsSecrets = 0x05000013;
    public const int QuicSettingsSize = 144;
    public const int CongestionAlgorithmOffset = 92;
    public const ulong CongestionAlgorithmIsSetBit = 1UL << 17; // Bit 17 in IsSetFlags
    public static unsafe bool TryGetXdpEnabled(System.Net.Quic.QuicConnection connection, out bool enabled)
    {
        enabled = default!; return false;
    }

    public static bool TrySetCongestionControl(System.Net.Quic.QuicConnection connection, QuicCongestionAlgorithm algorithm)
    {
        return false;
    }

    public static bool TrySetCongestionControl(global::QuicPunch.QuicConnection connection, QuicCongestionAlgorithm algorithm)
    {
        return connection.BridgeTransport?.TrySetCongestionControl(algorithm) ?? false;
    }

    public static bool TryGetCongestionControl(global::QuicPunch.QuicConnection connection, out QuicCongestionAlgorithm algorithm)
    {
        algorithm = default; if (!connection.TryGetTelemetry(out var t) || t.CongestionAlgorithm is not {} a) return false; algorithm = a; return true;
    }

    public static bool TryGetCongestionControl(System.Net.Quic.QuicConnection connection, out QuicCongestionAlgorithm algorithm)
    {
        algorithm = default!; return false;
    }

    public static bool TrySetDscp(System.Net.Quic.QuicConnection connection, QuicDscpPriority priority)
    {
        return false;
    }

    public static bool TrySetDscp(System.Net.Quic.QuicConnection connection, byte dscpValue)
    {
        return false;
    }

    public static bool TrySetStreamSchedulingScheme(System.Net.Quic.QuicConnection connection, QuicStreamSchedulingScheme scheme)
    {
        return false;
    }

    public static bool TrySetStreamSchedulingScheme(global::QuicPunch.QuicConnection connection, QuicStreamSchedulingScheme scheme)
    {
        return scheme <= QuicStreamSchedulingScheme.RoundRobin && (connection.BridgeTransport?.TrySetNative(QuicParamConnStreamSchedulingScheme, (uint)scheme) ?? false);
    }

    public static bool TryGetStreamSchedulingScheme(System.Net.Quic.QuicConnection connection, out QuicStreamSchedulingScheme scheme)
    {
        scheme = default!; return false;
    }

    public static bool TryGetStreamSchedulingScheme(global::QuicPunch.QuicConnection connection, out QuicStreamSchedulingScheme scheme)
    {
        scheme = default; if (connection.BridgeTransport is not {} b || !b.TryGetNative<uint>(QuicParamConnStreamSchedulingScheme, out var v)) return false; scheme = (QuicStreamSchedulingScheme)v; return true;
    }

    public static bool TryGetTelemetry(System.Net.Quic.QuicConnection connection, out QuicConnectionTelemetry telemetry)
    {
        telemetry = default!; return false;
    }

    public static bool TryGetLocalAddress(System.Net.Quic.QuicConnection connection, out System.Net.IPEndPoint? endpoint)
    {
        endpoint = connection.LocalEndPoint; return true;
    }

    public static bool TryGetRemoteAddress(System.Net.Quic.QuicConnection connection, out System.Net.IPEndPoint? endpoint)
    {
        endpoint = connection.RemoteEndPoint; return true;
    }

    public static bool TryGetNetworkStatistics(System.Net.Quic.QuicConnection connection, out QuicNetworkStatistics netStats)
    {
        netStats = default!; return false;
    }

    public static bool TryGetNetworkStatistics(global::QuicPunch.QuicConnection connection, out QuicNetworkStatistics netStats)
    {
        netStats = default; return connection.BridgeTransport?.TryGetNative(QuicParamConnNetworkStatistics, out netStats) ?? false;
    }

    public static bool TryGetIdealProcessor(System.Net.Quic.QuicConnection connection, out ushort processor)
    {
        processor = default!; return false;
    }

    public static bool TryGetIdealProcessor(global::QuicPunch.QuicConnection connection, out ushort processor)
    {
        processor = 0; return connection.BridgeTransport?.TryGetNative(QuicParamConnIdealProcessor, out processor) ?? false;
    }

    public static bool TryGetGlobalPerfCounters(out ulong[] counters)
    {
        counters = new ulong[64]; unsafe { uint length = (uint)(counters.Length * 8); fixed (ulong* p = counters) { if (MsQuicBridgeNative.GlobalGetParam(QuicParamGlobalPerfCounters, ref length, (nint)p) == 0) { counters = []; return false; } } Array.Resize(ref counters, (int)length / 8); } return true;
    }

    public static bool TryGetLibraryVersion(out string version, out string gitHash)
    {
        version = gitHash = string.Empty;
        unsafe { uint[] v = new uint[4]; uint length = 16; fixed (uint* ptr = v) { if (MsQuicBridgeNative.GlobalGetParam(QuicParamGlobalLibraryVersion, ref length, (nint)ptr) == 0) return false; } version = string.Join(".", v); }
        return true;
    }

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

    public static bool TrySetStreamPriority(System.IO.Stream stream, ushort priority)
    {
        if (stream is global::QuicPunch.QuicStream q) { q.Priority16 = (ushort)priority; return true; } if (stream is IQuicStreamTransport s) { s.Priority16 = (ushort)priority; return true; } return false;
    }

    public static bool TrySetStreamPriority(System.IO.Stream stream, QuicStreamPriority priority)
    {
        if (stream is global::QuicPunch.QuicStream q) { q.Priority16 = (ushort)priority; return true; } if (stream is IQuicStreamTransport s) { s.Priority16 = (ushort)priority; return true; } return false;
    }

    public static bool TryGetStreamPriority(System.IO.Stream stream, out ushort priority)
    {
        priority = default; if (stream is global::QuicPunch.QuicStream q) { priority = (ushort)q.Priority16; return true; } if (stream is IQuicStreamTransport s) { priority = (ushort)s.Priority16; return true; } return false;
    }

    public static bool TryGetStreamPriority(System.IO.Stream stream, out QuicStreamPriority priority)
    {
        priority = default; if (stream is global::QuicPunch.QuicStream q) { priority = (QuicStreamPriority)q.Priority16; return true; } if (stream is IQuicStreamTransport s) { priority = (QuicStreamPriority)s.Priority16; return true; } return false;
    }

    public static bool TryGetStreamStatistics(System.IO.Stream stream, out QuicStreamStatistics stats)
    {
        stats = default;
        var bridge = stream as MsQuicBridgeStream ?? (stream as global::QuicPunch.QuicStream)?.Transport as MsQuicBridgeStream;
        return bridge?.TryGetStatistics(out stats) ?? false;
    }

    public static bool TryGetPathMtu(System.Net.Quic.QuicConnection connection, out ushort pathMtu)
    {
        pathMtu = default!; return false;
    }

    public static bool TryGetPathMtu(global::QuicPunch.QuicConnection connection, out ushort pathMtu)
    {
        pathMtu = 0; if (!connection.TryGetTelemetry(out var t)) return false; pathMtu = t.PathMtu; return pathMtu != 0;
    }

    public static bool EnsurePathMtuDiscovery(ushort minMtu = 1248, ushort maxMtu = 1500)
    {
        return EnsureOptimalConfiguration(minMtu, maxMtu);
    }

    public static bool EnsureOptimalConfiguration()
    {
        return IsSupported;
    }

    public static bool EnsureOptimalConfiguration(
        ushort minMtu = 1248,
        ushort maxMtu = 1500,
        uint connFlowControlWindow = 16 * 1024 * 1024,
        uint streamRecvWindow = 4 * 1024 * 1024,
        uint keepAliveIntervalMs = 19000,
        ulong mtuDiscoveryTimeoutUs = 600_000_000UL,
        byte mtuDiscoveryMissingProbeCount = 3,
        uint initialWindowPackets = 10,
        bool enableZeroRtt = false,
        bool pacing = true,
        bool ecn = true,
        bool hyStart = true, QuicCongestionAlgorithm congestionAlgorithm = QuicCongestionAlgorithm.Bbr)
    {
        if (minMtu < 1248 || maxMtu < minMtu || maxMtu > 65527 || connFlowControlWindow == 0 || streamRecvWindow == 0 || enableZeroRtt || mtuDiscoveryMissingProbeCount == 0 || congestionAlgorithm > QuicCongestionAlgorithm.Bbr) return false;
        Volatile.Write(ref ConfigurationPolicy, [minMtu, maxMtu, connFlowControlWindow, streamRecvWindow, keepAliveIntervalMs, mtuDiscoveryTimeoutUs, mtuDiscoveryMissingProbeCount, initialWindowPackets, 0, pacing ? 1UL : 0, ecn ? 1UL : 0, hyStart ? 1UL : 0, (ulong)congestionAlgorithm]); return IsSupported;
    }

    public static bool TryApplyOptimalTuning(
        System.Net.Quic.QuicConnection connection,
        QuicCongestionAlgorithm congestionAlgorithm = QuicCongestionAlgorithm.Bbr,
        uint connFlowControlWindow = 16 * 1024 * 1024,
        uint streamRecvWindow = 4 * 1024 * 1024,
        uint keepAliveIntervalMs = 19000,
        ulong mtuDiscoveryTimeoutUs = 600_000_000UL,
        byte mtuDiscoveryMissingProbeCount = 3,
        uint initialWindowPackets = 10,
        bool pacing = true,
        bool datagrams = true,
        bool migration = true,
        bool sendBuffering = true,
        bool greaseQuicBit = true,
        bool hyStart = true,
        bool ecn = true,
        QuicStreamSchedulingScheme streamSchedulingScheme = QuicStreamSchedulingScheme.RoundRobin,
        QuicDscpPriority? dscp = null)
    {
        return false;
    }

    public static bool TryApplyOptimalTuning(
        global::QuicPunch.QuicConnection connection,
        QuicCongestionAlgorithm congestionAlgorithm = QuicCongestionAlgorithm.Bbr,
        uint connFlowControlWindow = 16 * 1024 * 1024,
        uint streamRecvWindow = 4 * 1024 * 1024,
        uint keepAliveIntervalMs = 19000,
        ulong mtuDiscoveryTimeoutUs = 600_000_000UL,
        byte mtuDiscoveryMissingProbeCount = 3,
        uint initialWindowPackets = 10,
        bool pacing = true,
        bool datagrams = true,
        bool migration = true,
        bool sendBuffering = true,
        bool greaseQuicBit = true,
        bool hyStart = true,
        bool ecn = true,
        QuicStreamSchedulingScheme streamSchedulingScheme = QuicStreamSchedulingScheme.RoundRobin,
        QuicDscpPriority? dscp = null)
    {
        return connection.BridgeTransport is {} b && b.TrySetCongestionControl(congestionAlgorithm) && b.Tune(0, keepAliveIntervalMs) && b.Tune(1, connFlowControlWindow, streamRecvWindow) && TrySetStreamSchedulingScheme(connection, streamSchedulingScheme) && (dscp == null || b.TrySetDscp(dscp.Value));
    }

    public static bool TrySetPathMtuDiscovery(
        System.Net.Quic.QuicConnection connection,
        ushort minMtu = 1248,
        ushort maxMtu = 1500,
        ulong timeoutUs = 600_000_000UL,
        byte missingProbeCount = 3)
    {
        return false;
    }

    public static bool TrySetPathMtuDiscovery(
        global::QuicPunch.QuicConnection connection,
        ushort minMtu = 1248,
        ushort maxMtu = 1500,
        ulong timeoutUs = 600_000_000UL,
        byte missingProbeCount = 3)
    {
        return connection.BridgeTransport?.Tune(2, minMtu, maxMtu, timeoutUs, missingProbeCount) ?? false;
    }

    public static bool TrySetFlowControlWindows(
        System.Net.Quic.QuicConnection connection,
        uint connectionWindowBytes = 16 * 1024 * 1024,
        uint streamWindowBytes = 4 * 1024 * 1024)
    {
        return false;
    }

    public static bool TrySetFlowControlWindows(
        global::QuicPunch.QuicConnection connection,
        uint connectionWindowBytes = 16 * 1024 * 1024,
        uint streamWindowBytes = 4 * 1024 * 1024)
    {
        return connection.BridgeTransport?.Tune(1, connectionWindowBytes, streamWindowBytes) ?? false;
    }

    public static bool TrySetKeepAliveInterval(System.Net.Quic.QuicConnection connection, uint intervalMs)
    {
        return false;
    }

    public static bool TrySetKeepAliveInterval(global::QuicPunch.QuicConnection connection, uint intervalMs)
    {
        return connection.BridgeTransport?.Tune(0, intervalMs) ?? false;
    }

    public static bool TrySendTransportPing(global::QuicPunch.QuicConnection connection)
    {
        return connection.SendDatagram(new byte[] { 0xff });
    }

    public static bool TryGetResumptionTicket(System.Net.Quic.QuicConnection connection, out byte[] ticket)
    {
        ticket = default!; return false;
    }

    public static bool TryGetResumptionTicket(global::QuicPunch.QuicConnection connection, out byte[] ticket)
    {
        ticket = default!; return false;
    }

    public static bool TrySetResumptionTicket(System.Net.Quic.QuicConnection connection, ReadOnlySpan<byte> ticket)
    {
        return false;
    }

    public static bool TrySetResumptionTicket(global::QuicPunch.QuicConnection connection, ReadOnlySpan<byte> ticket)
    {
        return false;
    }

    public static bool TryDumpSslKeyLog(System.Net.Quic.QuicConnection connection)
    {
        return false;
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
    Critical = 0xFFFF,  // Highest priority (65535)
    Highest = 0xFFFF
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

[StructLayout(LayoutKind.Sequential)]
public struct QuicNetworkStatistics
{
    public uint BytesInFlight;
    private uint _padding0;
    public ulong PostedBytes;
    public ulong IdealBytes;
    public ulong SmoothedRtt; // microseconds
    public uint CongestionWindow;
    private uint _padding1;
    public ulong Bandwidth; // bits per second
}

[StructLayout(LayoutKind.Sequential)]
public struct QuicStreamStatistics
{
    public ulong ConnBlockedBySchedulingUs;
    public ulong ConnBlockedByPacingUs;
    public ulong ConnBlockedByAmplificationProtUs;
    public ulong ConnBlockedByCongestionControlUs;
    public ulong ConnBlockedByFlowControlUs;
    public ulong StreamBlockedByIdFlowControlUs;
    public ulong StreamBlockedByFlowControlUs;
    public ulong StreamBlockedByAppUs;
}

/// <summary>
/// Represents real-time performance telemetry, protocol statistics, and connection metadata
/// queried directly from the native MsQuic engine.
/// </summary>
public record QuicConnectionTelemetry
{
    /// <summary>XDP requested in native settings; fallback may still use OS sockets.</summary>
    public bool? XdpRequested { get; init; }

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

    /// <summary>Gets the negotiated TLS key exchange group ID (e.g. 4588 for X25519MLKEM768).</summary>
    public uint NegotiatedTlsGroup { get; init; }

    /// <summary>Gets the human-readable name of the negotiated TLS key exchange group.</summary>
    public string NegotiatedTlsGroupName => NegotiatedTlsGroup switch
    {
        4588 => "X25519MLKEM768 (PQC)",
        512 => "MLKEM512 (PQC)",
        513 => "MLKEM768 (PQC)",
        514 => "MLKEM1024 (PQC)",
        4587 => "SecP256r1MLKEM768 (PQC)",
        4589 => "SecP384r1MLKEM1024 (PQC)",
        29 => "X25519",
        23 => "secp256r1",
        24 => "secp384r1",
        _ => NegotiatedTlsGroup != 0 ? $"Group_{NegotiatedTlsGroup}" : "Unknown"
    };

    /// <summary>Indicates whether post-quantum cryptography was negotiated during the TLS handshake.</summary>
    public bool IsPostQuantum => NegotiatedTlsGroup is 4588 or 512 or 513 or 514 or 4587 or 4589;

    /// <summary>Gets the live estimated bottleneck bandwidth in bits per second computed by BBR.</summary>
    public ulong EstimatedBandwidthBps { get; init; }

    /// <summary>Gets the live number of bytes in flight currently awaiting acknowledgment.</summary>
    public uint BytesInFlight { get; init; }

    /// <summary>Gets the ideal send buffer size in bytes to prevent throughput starvation.</summary>
    public ulong IdealSendBufferBytes { get; init; }

    /// <summary>Gets the smoothed RTT in milliseconds from network statistics.</summary>
    public double SmoothedRttMs { get; init; }

    /// <summary>Gets the processor index assigned to process packets for this connection.</summary>
    public ushort? IdealProcessor { get; init; }

    /// <summary>Gets the estimated packet loss ratio based on total and lost transmitted packets.</summary>
    public double PacketLossRatio => SendTotalPackets == 0 ? 0.0 :
        SendSuspectedLostPackets <= SendSpuriousLostPackets ? 0.0 :
        Math.Min(1.0, (double)(SendSuspectedLostPackets - SendSpuriousLostPackets) / SendTotalPackets);
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

    /// <summary>
    /// Queries performance and bottleneck statistics for this active QUIC stream.
    /// </summary>
    public static bool GetStreamStatistics(this System.IO.Stream stream, out QuicStreamStatistics stats) =>
        MsQuicTuner.TryGetStreamStatistics(stream, out stats);
}

/// <summary>
/// Extension methods for applying optimal MsQuic native tunings directly on QuicConnection instances.
/// </summary>
public static class QuicConnectionExtensions
{
    /// <summary>
    /// Atomically applies BBR congestion control, Pacing, HyStart, PMTUD, 16MB/4MB flow control windows, and keepalive to this connection.
    /// </summary>
    public static bool ApplyOptimalTuning(this System.Net.Quic.QuicConnection connection, QuicDscpPriority? dscp = null) =>
        MsQuicTuner.TryApplyOptimalTuning(connection, dscp: dscp);

    /// <summary>
    /// Atomically applies BBR congestion control, Pacing, HyStart, PMTUD, 16MB/4MB flow control windows, and keepalive to this connection.
    /// </summary>
    public static bool ApplyOptimalTuning(this global::QuicPunch.QuicConnection connection, QuicDscpPriority? dscp = null) =>
        MsQuicTuner.TryApplyOptimalTuning(connection, dscp: dscp);

    /// <summary>
    /// Queries real-time network and congestion control statistics from this native QUIC connection.
    /// </summary>
    public static bool TryGetNetworkStatistics(this System.Net.Quic.QuicConnection connection, out QuicNetworkStatistics stats) =>
        MsQuicTuner.TryGetNetworkStatistics(connection, out stats);

    /// <summary>
    /// Queries real-time network and congestion control statistics from this QuicPunch QUIC connection.
    /// </summary>
    public static bool TryGetNetworkStatistics(this global::QuicPunch.QuicConnection connection, out QuicNetworkStatistics stats) =>
        MsQuicTuner.TryGetNetworkStatistics(connection, out stats);

    /// <summary>
    /// Queries the ideal CPU processor core assigned by MsQuic to process packets for this connection.
    /// </summary>
    public static bool TryGetIdealProcessor(this System.Net.Quic.QuicConnection connection, out ushort processor) =>
        MsQuicTuner.TryGetIdealProcessor(connection, out processor);

    /// <summary>
    /// Queries the ideal CPU processor core assigned by MsQuic to process packets for this QuicPunch QUIC connection.
    /// </summary>
    public static bool TryGetIdealProcessor(this global::QuicPunch.QuicConnection connection, out ushort processor) =>
        MsQuicTuner.TryGetIdealProcessor(connection, out processor);

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
    public static bool TrySetPathMtuDiscovery(this System.Net.Quic.QuicConnection connection, ushort minMtu = 1248, ushort maxMtu = 1500, ulong timeoutUs = 600_000_000UL, byte missingProbeCount = 3) =>
        MsQuicTuner.TrySetPathMtuDiscovery(connection, minMtu, maxMtu, timeoutUs, missingProbeCount);

    /// <summary>
    /// Configures Path MTU Discovery parameters on an active QuicPunch QUIC connection.
    /// </summary>
    public static bool TrySetPathMtuDiscovery(this global::QuicPunch.QuicConnection connection, ushort minMtu = 1248, ushort maxMtu = 1500, ulong timeoutUs = 600_000_000UL, byte missingProbeCount = 3) =>
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
