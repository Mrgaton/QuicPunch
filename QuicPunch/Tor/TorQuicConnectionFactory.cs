using System.Net;
using System.Net.Quic;

namespace QuicPunch;

public sealed class TorQuicConnectionFactory : IQuicConnectionFactory
{
    private readonly TorManager _tor;
    private readonly TorPeerTransportHub _hub;
    private readonly int _maxMessageBytes;

    public TorQuicConnectionFactory(
        TorManager tor,
        TorPeerTransportHub hub,
        int maxMessageBytes = 4 * 1024 * 1024)
    {
        _tor = tor ?? throw new ArgumentNullException(nameof(tor));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _maxMessageBytes = maxMessageBytes;
    }

    public bool IsSupported => _tor.Runtime.IsRunning;

    public async ValueTask<IQuicConnectionTransport> ConnectAsync(
        QuicClientConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RemoteEndPoint is not DnsEndPoint dns)
        {
            throw new ArgumentException(
                "TorQuicConnectionFactory requires QuicClientConnectionOptions.RemoteEndPoint to be a DnsEndPoint containing the peer's .onion hostname.",
                nameof(options));
        }

        string onion = TorManager.NormalizeOnion(dns.Host);

        TorQuicConnectionManager manager = await TorQuicConnectionManager
            .ConnectAsync(
                _tor,
                _hub,
                onion,
                dns.Port,
                _maxMessageBytes,
                cancellationToken)
            .ConfigureAwait(false);

        return new DummyQuicConnectionTransport(
            manager,
            QuicConnectionRole.Client,
            manager.ConnectionId);
    }
}