using System.Collections.Concurrent;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Watch.Protocols;

namespace QuicNet.Watch.Services;

public sealed class PeerConnector
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly WatchControlHandler _control;
    private readonly MediaTransferHandler _media;
    private readonly CancellationToken _lifetime;
    private readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), byte> _connections = new();

    public event Action<string>? Error;

    public PeerConnector(
        QuicPunch.QuicPunch qcc,
        WatchControlHandler control,
        MediaTransferHandler media,
        CancellationToken lifetime)
    {
        _qcc = qcc;
        _control = control;
        _media = media;
        _lifetime = lifetime;
    }

    public async Task<PeerInfo> AddTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Introduce un token válido.", nameof(token));
        using PeerInfo decoded = Utilities.DecodeEndpointToken(token.Trim());
        Guid expectedId = decoded.Id;
        if (expectedId == Guid.Empty)
            throw new ArgumentException("El token debe incluir la identidad del dispositivo.", nameof(token));
        await _qcc.PeerInterrogation(token.Trim(), ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (_qcc.AvailablePeers.TryGetValue(expectedId, out PeerInfo? peer)) return peer;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("No se pudo localizar el dispositivo indicado por el token.");
        }
    }

    public async Task ConnectAsync(Guid peerId, CancellationToken ct)
    {
        if (!_qcc.AvailablePeers.TryGetValue(peerId, out PeerInfo? peer))
            throw new KeyNotFoundException("El participante ya no está disponible.");

        StartProtocol(peer, _control.ProtocolId);
        StartProtocol(peer, _media.ProtocolId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (_control.ConnectedPeerIds.Contains(peerId) && _media.ConnectedPeerIds.Contains(peerId)) return;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("No se pudieron abrir los canales de sala y vídeo.");
        }
    }

    private void StartProtocol(PeerInfo peer, Guid protocolId)
    {
        var key = (peer.Id, protocolId);
        if (!_connections.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _qcc.InitQuicConnection(protocolId, peer, 0, _lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { Error?.Invoke(ex.Message); }
            finally { _connections.TryRemove(key, out _); }
        }, CancellationToken.None);
    }
}
