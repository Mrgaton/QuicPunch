using System.Threading.Channels;
namespace QuicPunch.Helpers;
/// <summary>Compatibility facade. Use QuicPunch.QuicConnection.DatagramChannel for native datagrams.
/// The .NET public API does not expose a datagram channel; attaching via private handles is unsupported.</summary>
public sealed class MsQuicDatagramChannel : IQuicDatagramChannel
{
    public static bool IsSupported => false;
    public static bool IsConfigurationCachePatched => false;
    public static void EnsureInitialized() { }
    public static bool EnableDatagramsOnConfigurationCache(List<System.Net.Security.SslApplicationProtocol>? customProtocols = null) => false;
    public static bool EnsureClientConfigurationPatched(System.Net.Quic.QuicClientConnectionOptions options) => false;
    public static bool EnsureServerConfigurationPatched(System.Net.Quic.QuicServerConnectionOptions options, string? targetHost = null) => false;
    public static MsQuicDatagramChannel Attach(System.Net.Quic.QuicConnection connection) => throw new PlatformNotSupportedException("Use the QuicPunch native transport's DatagramChannel.");
    private MsQuicDatagramChannel() { }
    public bool IsSendEnabled => false;
    public bool IsReceiveEnabled => false;
    public ushort MaxSendDatagramLength => 0;
    public bool IsDisposed => true;
    public Channel<byte[]> IncomingDatagrams { get; } = Channel.CreateBounded<byte[]>(1);
    public event Action<byte[]>? OnDatagramReceived { add { } remove { } }
    public event Action<System.Net.IPEndPoint>? OnPeerAddressChanged { add { } remove { } }
    public bool Send(ReadOnlySpan<byte> data) => false;
    public void RefreshStatus() { }
    public void Dispose() { }
    internal static int PendingSendBufferCount => 0;
}
public interface IQuicDatagramChannel : IDisposable
{
    bool IsSendEnabled { get; }
    bool IsReceiveEnabled { get; }
    ushort MaxSendDatagramLength { get; }
    bool Send(ReadOnlySpan<byte> datagram);
    System.Threading.Channels.Channel<byte[]> IncomingDatagrams { get; }
    event Action<byte[]>? OnDatagramReceived;
    event Action<System.Net.IPEndPoint>? OnPeerAddressChanged;
}
