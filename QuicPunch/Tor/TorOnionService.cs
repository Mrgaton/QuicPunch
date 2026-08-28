#nullable enable

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

public sealed class TorOnionService : IAsyncDisposable
{
    private readonly TorManager _manager;
    private readonly TcpListener _listener;
    private int _disposed;

    internal TorOnionService(
        TorManager manager,
        TorIdentity identity,
        int virtualPort,
        TcpListener listener)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        VirtualPort = virtualPort;
        _listener = listener ?? throw new ArgumentNullException(nameof(listener));
    }

    public TorIdentity Identity { get; }
    public string ServiceId => Identity.ServiceId;
    public string OnionAddress => Identity.OnionAddress;
    public int VirtualPort { get; }
    public IPEndPoint LocalForwardEndPoint => (IPEndPoint)_listener.LocalEndpoint;

    public async ValueTask<TorTcpConnection> AcceptConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        return new TorTcpConnection(client, remoteOnion: null, remoteVirtualPort: 0, inbound: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _listener.Stop(); } catch { }

        try
        {
            await _manager.DeleteOnionAsync(ServiceId).ConfigureAwait(false);
        }
        catch
        {
            // Tor may already be shutting down. The detached service disappears
            // with the daemon anyway.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
