namespace QuicPunch;

public partial class QuicPunch
{
    private async Task CloseTransportSessionsAsync(TransportType transport)
    {
        foreach (var attempt in ActiveInterrogations.Values.Where(s =>
            (s.Peer.NetworkType == NetworkType.Tor) == (transport == TransportType.Tor)).ToArray())
            CancelInterrogation(attempt.Id);
        var peers = AvailablePeers.Values.Where(p => p.ActiveTransport == transport).ToArray();
        var ids = peers.Select(p => p.Id).ToHashSet();
        foreach (var flight in _activeOutboundNegotiations.Where(p => ids.Contains(p.Key.Item1)).Select(p => p.Value))
            try { flight.Cts.Cancel(); } catch (ObjectDisposedException) { }
        var workers = IncomingHandshakeSessions.Values.Where(s => s.Transport == transport)
            .Select(s => _activeIncomingWorkers.GetValueOrDefault(s.ConnectionGuid)).Where(w => w != null).ToArray();
        foreach (var worker in workers) try { worker!.Cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var peer in peers) await CloseAllPeerSessionsAsync(peer.Id).ConfigureAwait(false);
        if (workers.Length > 0) { try { await Task.WhenAll(workers.Select(w => w!.Task)).ConfigureAwait(false); } catch { } }
    }

    private bool IsPeerTransportEnabled(PeerInfo peer) => !TransportLifetime(peer.ActiveTransport).IsCancellationRequested;

    internal CancellationToken TransportLifetime(TransportType transport) =>
        (transport == TransportType.Tor ? IsTorStarted : IsWanStarted)
            ? (transport == TransportType.Tor ? _torLoopCts : _wanLoopCts)?.Token ?? LifecycleToken
            : new CancellationToken(true);
}
