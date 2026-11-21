using QuicNet.Watch.Protocols;
using QuicNet.Watch.Services;
using QuicNet.Watch.Web;
using Node = QuicPunch.QuicPunch;

namespace QuicNet.Watch;

/// <summary>Owns the QuicWatch protocols, library and web server for one application node.</summary>
internal sealed class WatchApplication : IAsyncDisposable
{
    private readonly Node _node;
    private readonly WatchControlHandler _control;
    private readonly MediaTransferHandler _media;
    private readonly MediaLibrary _library;
    private int _disposed;

    public WatchWebServer Web { get; }

    internal WatchApplication(Node node, CancellationTokenSource lifetime)
    {
        Guid localPeerId = node.CurrentPeer?.Id ?? throw new InvalidOperationException("The local peer identity is unavailable.");
        _node = node;
        _control = new WatchControlHandler();
        _media = new MediaTransferHandler();
        _library = new MediaLibrary(_media, Path.Combine(node.NodeAppDataPath, "watch-media-cache"));
        var rooms = new RoomCoordinator(_control, _library, localPeerId);
        var connector = new PeerConnector(node, _control, _media, lifetime.Token);
        Web = new WatchWebServer(node, _control, _media, _library, rooms, connector, lifetime);

        node.RegisterProtocol(_control);
        node.RegisterProtocol(_media);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _node.ProtocolHandlers.TryRemove(new KeyValuePair<Guid, Node.IProtocolHandler>(_control.ProtocolId, _control));
        _node.ProtocolHandlers.TryRemove(new KeyValuePair<Guid, Node.IProtocolHandler>(_media.ProtocolId, _media));
        try
        {
            await Web.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            using (_media)
            using (_control)
                _library.Dispose();
        }
    }
}
