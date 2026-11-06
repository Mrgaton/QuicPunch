using QuicPunch.Discovery;
using QuicPunch.Discovery.PortMapping;
using QuicPunch.Helpers;
using QuicPunch.PacketHandler;
using QuicPunch.Security;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch
{
    // Node startup, worker ownership and shutdown.
    public partial class QuicPunch
    {
        public enum QuicPunchLifecycleState
        {
            Created = 0,
            Starting = 1,
            Started = 2,
            Stopping = 3,
            Stopped = 4,
            Disposed = 5
        }

        private int _isDisposed = 0;
        private long _lifecycleGeneration = 0;
        public long LifecycleGeneration => Interlocked.Read(ref _lifecycleGeneration);
        public CancellationToken LifecycleToken => CancellationSource?.Token ?? new CancellationToken(true);
        public QuicPunchLifecycleState LifecycleState { get; private set; } = QuicPunchLifecycleState.Created;
        public bool IsStarted => LifecycleState == QuicPunchLifecycleState.Started;
        public bool IsDisposed => _isDisposed != 0 || LifecycleState == QuicPunchLifecycleState.Disposed;

        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly CancellationToken _parentCancellationToken;

        public void EnsureStarted()
        {
            if (LifecycleState != QuicPunchLifecycleState.Started)
            {
                throw new InvalidOperationException("QuicPunch has not been started.");
            }
        }

        private Task? _pingLoopTask;
        private Task? _maintenanceLoopTask;
        private Task? _autoConnectTask;
        private Task? _mailboxOutboxTask;
        private bool IsLifecycleWorkerCurrent(long generation, CancellationToken token, UdpClient? expectedUdp = null)
        {
            return !token.IsCancellationRequested
                && LifecycleState == QuicPunchLifecycleState.Started
                && generation == _lifecycleGeneration
                && (expectedUdp == null || ReferenceEquals(udp, expectedUdp));
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);

            try
            {
                if (LifecycleState == QuicPunchLifecycleState.Disposed)
                    throw new ObjectDisposedException(nameof(QuicPunch));

                if (LifecycleState == QuicPunchLifecycleState.Started)
                    return;

                Interlocked.Increment(ref _lifecycleGeneration);
                LifecycleState = QuicPunchLifecycleState.Starting;

                Helpers.MsQuicTuner.EnsurePathMtuDiscovery();

                CertManager.RenewSessionEntropy();
                if (!ReferenceEquals(TorCertManager, CertManager))
                    TorCertManager?.RenewSessionEntropy();

                try { CancellationSource?.Dispose(); } catch { }
                CancellationSource = _parentCancellationToken.CanBeCanceled || cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_parentCancellationToken, cancellationToken)
                    : new CancellationTokenSource();

                CancellationToken lifecycleToken = CancellationSource.Token;
                long lifecycleGeneration = _lifecycleGeneration;

                Directory.CreateDirectory(NodeAppDataPath);
                PeerStore = new PeerStore(Path.Combine(NodeAppDataPath, "peers.db"));
                _automaticContactsStartedUtc = DateTime.UtcNow;
                _reactivatedAutomaticContacts.Clear();

                if (MsQuicDatagramChannel.IsSupported)
                {
                    MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();
                }

                LifecycleState = QuicPunchLifecycleState.Started;
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                _pingLoopTask = Task.Run(() => StartPingLoopAsync(lifecycleGeneration, lifecycleToken), lifecycleToken);
                _maintenanceLoopTask = Task.Run(() => MaintenanceLoopAsync(lifecycleGeneration, lifecycleToken), lifecycleToken);
                _peerTransportTask = Task.Run(() => PeerTransportLoopAsync(lifecycleGeneration, lifecycleToken), lifecycleToken);
                _automaticContactProbeTask = Task.Run(() => AutomaticContactProbeLoopAsync(lifecycleGeneration, lifecycleToken), lifecycleToken);

                if (WanEnabled)
                {
                    try
                    {
                        await StartWanCoreAsync(LocalPort, lifecycleToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[WAN] Startup warning: {ex.Message}");
                    }
                }

                // StartWanCoreAsync launches this as soon as its UDP receiver is ready.
                // Tor-only nodes still need the saved-peer pass.
                _autoConnectTask ??= Task.Run(() => AutoConnectSavedPeersAsync(lifecycleToken), lifecycleToken);

                if (Discovery.TorNostrDiscoveryEnabled && IsTorStarted)
                {
                    try
                    {
                        await Discovery.StartTorNostrDiscoveryAsync(lifecycleToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[NOSTR-TOR] Discovery start warning: {ex.Message}");
                    }
                }

                try
                {
                    await Mailbox.StartAsync(Discovery.NostrRelays, lifecycleToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[NOSTR-MAILBOX] Startup warning: {ex.Message}");
                }

                _mailboxOutboxTask = Task.Run(() => MailboxOutboxLoopAsync(lifecycleToken), lifecycleToken);
            }
            catch (Exception)
            {
                try { CancellationSource?.Cancel(); } catch { }
                try { await Discovery.StopAllAsync().ConfigureAwait(false); } catch { }
                try { udp?.Close(); udp?.Dispose(); udp = null; } catch { }
                try { PeerStore?.Dispose(); PeerStore = null; } catch { }
                await AwaitLifecycleWorkersAsync().ConfigureAwait(false);
                LifecycleState = QuicPunchLifecycleState.Stopped;
                throw;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private void CleanupSyncCore()
        {
            try { CancellationSource?.Cancel(); } catch { }
            try { _torLoopCts?.Cancel(); } catch { }

            foreach (var w in _activeIncomingWorkers.Values)
            {
                try { w.Cts.Cancel(); } catch { }
            }

            foreach (var kvp in _activeOutboundNegotiations)
            {
                try { kvp.Value.Cts.Cancel(); } catch { }
                try { kvp.Value.Cts.Dispose(); } catch { }
            }
            _activeOutboundNegotiations.Clear();

            lock (_incomingHandshakeSessionsLock)
            {
                foreach (var session in IncomingHandshakeSessions.Values)
                {
                    session.MarkRejected();
                    session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                }
                IncomingHandshakeSessions.Clear();
            }

            try { _manager.CancelAll(); } catch { }

            foreach (var kvp in _pendingQuicReady)
            {
                if (_pendingQuicReady.TryRemove(kvp.Key, out var pending))
                {
                    pending.Tcs.TrySetCanceled();
                }
            }
            _receivedQuicReady.Clear();

            foreach (var kv in ActiveInterrogations)
            {
                try { kv.Value.Cts?.Cancel(); kv.Value.Cts?.Dispose(); } catch { }
            }
            ActiveInterrogations.Clear();

            foreach (var pending in _pendingChallenges)
            {
                if (_pendingChallenges.TryRemove(pending.Key, out var challenge))
                {
                    try { challenge.Dispose(); } catch { }
                }
            }

            try { udp?.Close(); udp?.Dispose(); udp = null; } catch { }
            try { PeerStore?.Dispose(); PeerStore = null; } catch { }

            lock (_availablePeerAdmissionLock)
            {
                foreach (var peer in AvailablePeers.Values)
                {
                    try { peer.Dispose(); } catch { }
                }
                AvailablePeers.Clear();
                _peersByCertHash.Clear();
            }

            CertManager.RenewSessionEntropy();
            if (!ReferenceEquals(TorCertManager, CertManager))
                TorCertManager?.RenewSessionEntropy();
        }

        private async Task CleanupResourcesAsync()
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _sessionRecovery.Clear();
            foreach (var intent in _sessionIntents.Values) intent.Cancel();
            _sessionIntents.Clear();
            try { CancellationSource.Cancel(); } catch { }
            await _wanLifecycleLock.WaitAsync().ConfigureAwait(false);
            try { await StopWanCoreAsync().ConfigureAwait(false); }
            finally { _wanLifecycleLock.Release(); }
            await _torLifecycleLock.WaitAsync().ConfigureAwait(false);
            try { await StopTorCoreAsync().ConfigureAwait(false); }
            finally { _torLifecycleLock.Release(); }
            foreach (var entry in _peerMultiplexers.ToArray())
            {
                if (_peerMultiplexers.TryRemove(entry.Key, out var mux))
                    try { await mux.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            try { await Discovery.StopAllAsync().ConfigureAwait(false); } catch { }
            try { await Mailbox.StopAsync().ConfigureAwait(false); } catch { }

            // Cancel and await active incoming handshake workers
            foreach (var w in _activeIncomingWorkers.Values)
            {
                try { w.Cts.Cancel(); } catch { }
            }
            var workerTasks = _activeIncomingWorkers.Values.Select(w => w.Task).ToArray();
            if (workerTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(workerTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch { }
            }
            _activeIncomingWorkers.Clear();

            lock (HandshakeCoordinationLock) _transportAttempts.Clear();
            CleanupSyncCore();

            List<(QuicConnection Connection, Stream Stream)> sessionsToDispose;
            lock (_activeProtocolSessions)
            {
                sessionsToDispose = _activeProtocolSessions.Values.ToList();
                _activeProtocolSessions.Clear();
            }
            foreach (var session in sessionsToDispose)
            {
                try { await session.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await session.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            try { await NatCoordinator.StopAsync().ConfigureAwait(false); } catch { }
        }

        private async Task AwaitLifecycleWorkersAsync()
        {
            Task?[] workers =
            {
                _receiveLoopTask,
                _stunLoopTask,
                _stunCatalogTask,
                _pingLoopTask,
                _maintenanceLoopTask,
                _peerTransportTask,
                _autoConnectTask,
                _automaticContactProbeTask,
                _rebindRefreshTask,
                _mailboxOutboxTask,
                _upnpMappingTask,
                _torAcceptLoopTask
            };

            var running = workers.Where(task => task != null).Cast<Task>()
                .Concat(_torReceiveLoopTasks.Values)
                .Concat(_mappingMonitors.Values)
                .Distinct()
                .ToArray();
            if (running.Length > 0)
            {
                try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }

            _receiveLoopTask = null;
            _stunLoopTask = null;
            _stunCatalogTask = null;
            _pingLoopTask = null;
            _maintenanceLoopTask = null;
            _peerTransportTask = null;
            _autoConnectTask = null;
            _automaticContactProbeTask = null;
            _rebindRefreshTask = null;
            _mailboxOutboxTask = null;
            _torAcceptLoopTask = null;
            _torReceiveLoopTasks.Clear();
            try { _wanLoopCts?.Dispose(); } catch { }
            _wanLoopCts = null;
            try { _torLoopCts?.Dispose(); } catch { }
            _torLoopCts = null;
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (LifecycleState != QuicPunchLifecycleState.Started && LifecycleState != QuicPunchLifecycleState.Starting)
                    return;

                LifecycleState = QuicPunchLifecycleState.Stopping;
                await CleanupResourcesAsync().ConfigureAwait(false);
                await AwaitLifecycleWorkersAsync().ConfigureAwait(false);
                LifecycleState = QuicPunchLifecycleState.Stopped;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (LifecycleState == QuicPunchLifecycleState.Started || LifecycleState == QuicPunchLifecycleState.Starting)
                {
                    LifecycleState = QuicPunchLifecycleState.Stopping;
                }

                await CleanupResourcesAsync().ConfigureAwait(false);
                await AwaitLifecycleWorkersAsync().ConfigureAwait(false);
                LifecycleState = QuicPunchLifecycleState.Disposed;
            }
            finally
            {
                _lifecycleLock.Release();
            }

            try { NatCoordinator.Dispose(); } catch { }
            try { Discovery?.Dispose(); } catch { }
            try { Mailbox?.Dispose(); } catch { }
            try { await MailboxStore.DisposeAsync().ConfigureAwait(false); } catch { }
            try { ReputationFilter?.Dispose(); } catch { }
            try { CancellationSource?.Dispose(); } catch { }
            try { CertManager?.Dispose(); } catch { }
            try { TorCertManager?.Dispose(); } catch { }
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    }
}
