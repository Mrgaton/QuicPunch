using QuicPunch.Discovery;
using QuicPunch.Discovery.PortMapping;
using QuicPunch.Helpers;
using QuicPunch.PacketHandler;
using QuicPunch.Security;
using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
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
    public class QuicPunch : IDisposable, IAsyncDisposable
    {
        public const int SioUdpConnReset = unchecked((int)0x9800000C);
        public const int SioUdpNetReset = unchecked((int)0x9800000F);

        public static void ConfigureUdpSocket(UdpClient client)
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (!OperatingSystem.IsWindows())
            {
                try { client.Client.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15, true); } catch { }
            }
            if (OperatingSystem.IsWindows())
            {
                byte[] optionInValue = new byte[] { 0 };
                client.Client.IOControl(SioUdpConnReset, optionInValue, null);
                client.Client.IOControl(SioUdpNetReset, optionInValue, null);
            }
        }

        public static bool EnableLogging
        {
            get => QuicPunchLog.EnableLogging;
            set => QuicPunchLog.EnableLogging = value;
        }

        public static bool EnableErrorLogging
        {
            get => QuicPunchLog.EnableErrorLogging;
            set => QuicPunchLog.EnableErrorLogging = value;
        }

        public static Action<string>? LogHandler
        {
            get => QuicPunchLog.LogHandler;
            set => QuicPunchLog.LogHandler = value;
        }

        public static Action<string>? ErrorHandler
        {
            get => QuicPunchLog.ErrorHandler;
            set => QuicPunchLog.ErrorHandler = value;
        }

        public static void WriteLine(string m) => QuicPunchLog.Info(m);
        public static void WriteError(string m, Exception? ex = null) => QuicPunchLog.Error(m, ex);

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

        public UdpClient? udp = null;

        public const int DefaultLanDiscoveryPort = 7227;
        public const string DefaultLanDiscoveryMulticast = "239.255.72.27";
        public int LanDiscoveryPort { get; set; } = DefaultLanDiscoveryPort;
        private UdpClient? _lanDiscoveryUdp = null;
        public int LocalDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);
        public int LocalBoundPort => udp != null && udp.Client.LocalEndPoint is IPEndPoint ip ? ip.Port : LocalPort;

        public bool RebindListenerPort(ushort newPort)
        {
            EnsureStarted();
            _lifecycleLock.Wait();
            try
            {
                if (LifecycleState != QuicPunchLifecycleState.Started || CancellationSource.IsCancellationRequested)
                    return false;

                if (!IsWanStarted)
                {
                    LocalPort = newPort;
                    LocalDiscoveryPort = newPort;
                    return true;
                }

                long generation = _lifecycleGeneration;
                CancellationToken token = CancellationSource.Token;
                var oldUdp = udp;
                var newUdp = new UdpClient();
                ConfigureUdpSocket(newUdp);
                newUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                newUdp.Client.Bind(new IPEndPoint(IPAddress.Any, newPort));
                newUdp.Client.DontFragment = true;

                udp = newUdp;
                LocalDiscoveryPort = ((IPEndPoint)newUdp.Client.LocalEndPoint!).Port;
                LocalPort = LocalDiscoveryPort;

                ResetNatMapping();
                NatCoordinator.SetUdpClient(newUdp);

                try { oldUdp?.Close(); oldUdp?.Dispose(); } catch { }

                var receiveTask = Task.Run(() => ReceiveUdpLoopAsync(newUdp, token), token);
                _receiveLoopTask = _receiveLoopTask == null ? receiveTask : Task.WhenAll(_receiveLoopTask, receiveTask);

                var refreshTask = Task.Run(() => RefreshAfterRebindAsync(newUdp, generation, token), token);
                _rebindRefreshTask = _rebindRefreshTask == null ? refreshTask : Task.WhenAll(_rebindRefreshTask, refreshTask);
                return true;
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error($"[QuicPunch] Failed to rebind listener port to {newPort}", ex);
                return false;
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private bool IsLifecycleWorkerCurrent(long generation, CancellationToken token, UdpClient? expectedUdp = null)
        {
            return !token.IsCancellationRequested
                && LifecycleState == QuicPunchLifecycleState.Started
                && generation == _lifecycleGeneration
                && (expectedUdp == null || ReferenceEquals(udp, expectedUdp));
        }

        private async Task RefreshAfterRebindAsync(UdpClient expectedUdp, long generation, CancellationToken token)
        {
            try
            {
                await StunRequest(resetOnFailure: true, cancellationToken: token, generation: generation, expectedUdp: expectedUdp).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    QuicPunchLog.Info($"[QuicPunch] STUN request after rebind warning: {ex.Message}");
            }

            if (!IsLifecycleWorkerCurrent(generation, token, expectedUdp))
                return;

            await PublishNostrDiscoveryAsync(token).ConfigureAwait(false);
        }

        public static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicPunchV17");
        public string NodeAppDataPath { get; set; }

        public PeerInfo CurrentPeer { get; private set; } = null!;

        public NatPinCoordinator NatCoordinator { get; }
        public IReadOnlyList<IPEndPoint> StunServerEndpoints
        {
            get => NatCoordinator.Servers;
            set => NatCoordinator.SetServerEndpoints(value ?? Array.Empty<IPEndPoint>());
        }

        public PeerStore PeerStore { get; private set; } = null!;

        public readonly HandshakeManager _manager = new HandshakeManager();
        public HandshakeManager Manager => _manager;

        private readonly IpRateLimiter _rateLimiter = new IpRateLimiter(500);

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();

        public bool EnableUpnp { get; set; } = true;
        public bool IsUpnpMapped { get; private set; }
        public string? ActivePortMappingProtocol { get; private set; }
        public PortMappingCoordinator? PortCoordinator => _portCoordinator;
        public UpnpPortMapper? UpnpMapper => _portCoordinator?.UpnpMapper;
        private PortMappingCoordinator? _portCoordinator;
        private Task? _upnpMappingTask;

        private int LocalPort;

        private void ResetNatMapping()
        {
            if (CurrentPeer != null)
            {
                CurrentPeer.MinPort = LocalDiscoveryPort;
                CurrentPeer.MaxPort = LocalDiscoveryPort;
            }
        }

        public CertManager CertManager { get; private set; } = null!;
        public CertManager TorCertManager { get; private set; } = null!;

        public string? TorOnionAddress { get; private set; }
        public TorIdentity? TorIdentity { get; private set; }
        public TorPeerTransportHub? TorHub { get; private set; }
        public TorManager? TorManager { get; private set; }
        public bool IsTorStarted => TorManager != null && TorHub != null;
        public string TorBootstrapStatus { get; private set; } = "Not initialized";
        public int TorBootstrapProgress { get; private set; } = 0;
        public string? TorLastError { get; private set; }
        public TorTransportTier TorActiveTransport => TorManager?.ActiveTransport ?? TorTransportTier.Direct;

        public enum TransportType
        {
            Wan = 0,
            Tor = 1
        }

        private Task? _receiveLoopTask;
        private Task? _lanDiscoveryLoopTask = null;
        private Task? _stunLoopTask;
        private Task? _pingLoopTask;
        private Task? _maintenanceLoopTask;
        private Task? _autoConnectTask;
        private Task? _rebindRefreshTask;
        private readonly SemaphoreSlim _torLifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _wanLifecycleLock = new(1, 1);
        private CancellationTokenSource? _torLoopCts;
        private CancellationTokenSource? _wanLoopCts;
        public bool IsWanStarted => udp != null && LifecycleState == QuicPunchLifecycleState.Started;
        public string? WanLastError { get; private set; }
        public bool WanEnabled { get; set; } = true;
        private Task? _torAcceptLoopTask;
        private readonly ConcurrentDictionary<Guid, Task> _torReceiveLoopTasks = new();

        public QuicPunch(CancellationTokenSource? cts, byte[]? discoveryId, byte[]? connectionPassword, bool autoAcceptConnections, ushort listeningPort = 0, string? appDataPath = null)
            : this(cts?.Token ?? default, discoveryId, connectionPassword, autoAcceptConnections, listeningPort, appDataPath)
        {
        }

        public QuicPunch(CancellationToken cancellationToken = default, byte[]? discoveryId = null, byte[]? connectionPassword = null, bool autoAcceptConnections = true, ushort listeningPort = 0, string? appDataPath = null)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            {
                throw new NotSupportedException("QUIC is not supported on this machine.");
            }

            NodeAppDataPath = appDataPath ?? AppDataPath;
            _parentCancellationToken = cancellationToken;
            CancellationSource = cancellationToken.CanBeCanceled 
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) 
                : new CancellationTokenSource();
            CertManager = new CertManager(NodeAppDataPath, "wan");
            TorCertManager = new CertManager(NodeAppDataPath, "tor");

            if (discoveryId != null)
            {
                _poolId = discoveryId.Length == 20 ? discoveryId : SHA1.HashData(discoveryId);
                WanNostrDiscoveryEnabled = true;
                TorNostrDiscoveryEnabled = true;
            }

            _connectionPassword = connectionPassword;
            if (_connectionPassword != null)
            {
                DerivePasswordHash();
            }

            AccessController = new PeerAccessController(
                peerStoreProvider: () => PeerStore,
                peerByIdResolver: id => AvailablePeers.TryGetValue(id, out var p) ? p : null,
                peerByCertHashResolver: TryGetPeerByCertHash,
                autoAcceptConnections: autoAcceptConnections,
                autoAcceptUntrusted: false);

            NatCoordinator = new NatPinCoordinator();
            NatCoordinator.MappingChanged += OnNatMappingChanged;
            NatCoordinator.OnStunRefreshRequested = (force, token) => RefreshStunEndpointsAsync(force, token);

            LocalPort = listeningPort == 0 ? Utilities.GetDeterministicPortFromCertHash(CertManager.CertPublicHash) : listeningPort;

            CurrentPeer = new PeerInfo(CertManager.PeerCertificate, CertManager.EcdhPublicKeyRaw)
            {
                Name = CertManager.PeerName,
                Addresses = Array.Empty<IPAddress>(),
            };

            TorCurrentPeer = new PeerInfo(TorCertManager.PeerCertificate, TorCertManager.EcdhPublicKeyRaw)
            {
                Name = TorCertManager.PeerName,
                Addresses = Array.Empty<IPAddress>(),
                NetworkType = NetworkType.Tor
            };
        }

        public PeerInfo TorCurrentPeer { get; private set; } = null!;

        public CertManager GetCertManager(TransportType transport = TransportType.Wan) =>
            transport == TransportType.Tor ? TorCertManager : CertManager;

        public PeerInfo GetCurrentPeer(TransportType transport = TransportType.Wan) =>
            (transport == TransportType.Tor && TorCurrentPeer != null) ? TorCurrentPeer : CurrentPeer;

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

                if (MsQuicDatagramChannel.IsSupported)
                {
                    MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();
                }

                LifecycleState = QuicPunchLifecycleState.Started;
                _pingLoopTask = Task.Run(() => StartPingLoopAsync(lifecycleGeneration, lifecycleToken), lifecycleToken);
                _maintenanceLoopTask = Task.Run(() => MaintenanceLoopAsync(lifecycleGeneration, lifecycleToken), lifecycleToken);
                _autoConnectTask = Task.Run(() => AutoConnectSavedPeersAsync(lifecycleToken), lifecycleToken);

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

                if (TorNostrDiscoveryEnabled && IsTorStarted)
                {
                    try
                    {
                        await StartTorNostrDiscoveryAsync(lifecycleToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[NOSTR-TOR] Discovery start warning: {ex.Message}");
                    }
                }
            }
            catch (Exception)
            {
                try { CancellationSource?.Cancel(); } catch { }
                try { await StopNostrDiscoveryAsync().ConfigureAwait(false); } catch { }
                try { udp?.Close(); udp?.Dispose(); udp = null; } catch { }
                try { _lanDiscoveryUdp?.Close(); _lanDiscoveryUdp?.Dispose(); _lanDiscoveryUdp = null; } catch { }
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

        private async Task ReceiveLanDiscoveryLoopAsync(UdpClient lanUdp, CancellationToken token)
        {
            byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        EndPoint remoteEndPoint = lanUdp.Client.AddressFamily == AddressFamily.InterNetworkV6
                            ? new IPEndPoint(IPAddress.IPv6Any, 0)
                            : new IPEndPoint(IPAddress.Any, 0);

                        SocketReceiveFromResult result = await lanUdp.Client.ReceiveFromAsync(
                            receiveBuffer.AsMemory(0, 65536),
                            SocketFlags.None,
                            remoteEndPoint,
                            token).ConfigureAwait(false);

                        int bytesRead = result.ReceivedBytes;
                        if (bytesRead > 0)
                        {
                            await ProcessIncomingPacketAsync(receiveBuffer, bytesRead, result.RemoteEndPoint, TransportType.Wan, 
                                isLanDiscovery: true).ConfigureAwait(false);
                        }
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted) { break; }
                    catch (Exception)
                    {
                        if (token.IsCancellationRequested) break;
                        try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
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

            try
            {
                _nostrLifecycleLock.Wait();
                try
                {
                    WanNostrDiscovery?.Dispose();
                    WanNostrDiscovery = null;
                }
                finally
                {
                    _nostrLifecycleLock.Release();
                }
            }
            catch { }

            try
            {
                _torNostrLifecycleLock.Wait();
                try
                {
                    TorNostrDiscovery?.Dispose();
                    TorNostrDiscovery = null;
                }
                finally
                {
                    _torNostrLifecycleLock.Release();
                }
            }
            catch { }
            try { udp?.Close(); udp?.Dispose(); udp = null; } catch { }
            try { _lanDiscoveryUdp?.Close(); _lanDiscoveryUdp?.Dispose(); _lanDiscoveryUdp = null; } catch { }
            try { PeerStore?.Dispose(); PeerStore = null; } catch { }

            lock (_availablePeerAdmissionLock)
            {
                foreach (var peer in AvailablePeers.Values)
                {
                    try { peer.Dispose(); } catch { }
                }
                AvailablePeers.Clear();
                _peersByCertHash.Clear();
                _peersByShortId.Clear();
            }

            CertManager.RenewSessionEntropy();
            if (!ReferenceEquals(TorCertManager, CertManager))
                TorCertManager?.RenewSessionEntropy();
        }

        private async Task CleanupResourcesAsync()
        {
            try { await StopNostrDiscoveryAsync().ConfigureAwait(false); } catch { }

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

            try { await StopWanAsync().ConfigureAwait(false); } catch { }
            try { await StopTorAsync().ConfigureAwait(false); } catch { }
        }

        private async Task AwaitLifecycleWorkersAsync()
        {
            Task?[] workers =
            {
                _receiveLoopTask,
                _lanDiscoveryLoopTask,
                _stunLoopTask,
                _pingLoopTask,
                _maintenanceLoopTask,
                _autoConnectTask,
                _rebindRefreshTask,
                _torAcceptLoopTask
            };

            var running = workers.Where(task => task != null).Cast<Task>()
                .Concat(_torReceiveLoopTasks.Values)
                .Distinct()
                .ToArray();
            if (running.Length > 0)
            {
                try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }

            _receiveLoopTask = null;
            _lanDiscoveryLoopTask = null;
            _stunLoopTask = null;
            _pingLoopTask = null;
            _maintenanceLoopTask = null;
            _autoConnectTask = null;
            _rebindRefreshTask = null;
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

        public async Task StartWanAsync(ushort port = 0, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            await _wanLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsWanStarted)
                    return;
                WanLastError = null;
                await StartWanCoreAsync(port > 0 ? port : LocalPort, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WanLastError = ex.Message;
                throw;
            }
            finally
            {
                _wanLifecycleLock.Release();
            }
        }

        private async Task StartWanCoreAsync(int port, CancellationToken cancellationToken)
        {
            QuicPunchLog.Info("[WAN] Starting WAN UDP service...");
            Helpers.MsQuicTuner.EnsurePathMtuDiscovery();
            try { _wanLoopCts?.Cancel(); _wanLoopCts?.Dispose(); } catch { }
            _wanLoopCts = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
            CancellationToken wanToken = _wanLoopCts.Token;

            var newUdp = new UdpClient();
            ConfigureUdpSocket(newUdp);
            int bindPort = port > 0 ? port : (LocalPort > 0 ? LocalPort : 0);
            if (bindPort > 0)
            {
                try { newUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); } catch { }
            }

            try
            {
                newUdp.Client.Bind(new IPEndPoint(IPAddress.Any, bindPort));
            }
            catch (SocketException) when (bindPort > 0)
            {
                newUdp.Dispose();
                newUdp = new UdpClient();
                ConfigureUdpSocket(newUdp);
                newUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            newUdp.Client.DontFragment = true;

            udp = newUdp;
            LocalDiscoveryPort = ((IPEndPoint)newUdp.Client.LocalEndPoint!).Port;
            LocalPort = LocalDiscoveryPort;

            if (EnableUpnp && LocalPort > 0)
            {
                int portToMap = LocalPort;
                _upnpMappingTask = Task.Run(async () =>
                {
                    try
                    {
                        _portCoordinator ??= new PortMappingCoordinator();
                        var mapResult = await _portCoordinator.TryMapPortAsync(
                            portToMap, portToMap, protocol: ProtocolType.Udp, lifetime: TimeSpan.FromHours(2), ct: wanToken).ConfigureAwait(false);
                        IsUpnpMapped = mapResult.Success;
                        ActivePortMappingProtocol = mapResult.Success ? mapResult.ProtocolName : null;
                        if (mapResult.Success)
                        {
                            var extIp = mapResult.ExternalIp ?? await _portCoordinator.GetExternalIpAddressAsync(wanToken).ConfigureAwait(false);
                            if (extIp != null)
                            {
                                QuicPunchLog.Info($"[PORT-MAP] Router external endpoint discovered: {extIp}:{portToMap} via {mapResult.ProtocolName}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[PORT-MAP] Port mapping notice: {ex.Message}");
                    }
                }, wanToken);
            }

            UdpClient listenerUdp = udp;
            _receiveLoopTask = Task.Run(() => ReceiveUdpLoopAsync(listenerUdp, wanToken), wanToken);

            if (_lanDiscoveryUdp == null)
            {
                try
                {
                    _lanDiscoveryUdp = new UdpClient();
                    ConfigureUdpSocket(_lanDiscoveryUdp);
                    _lanDiscoveryUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    if (!OperatingSystem.IsWindows())
                    {
                        try { _lanDiscoveryUdp.Client.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15, true); } catch { }
                    }
                    _lanDiscoveryUdp.Client.Bind(new IPEndPoint(IPAddress.Any, LanDiscoveryPort));
                    _lanDiscoveryUdp.EnableBroadcast = true;
                    _lanDiscoveryUdp.MulticastLoopback = true;
                    try
                    {
                        _lanDiscoveryUdp.JoinMulticastGroup(IPAddress.Parse(DefaultLanDiscoveryMulticast));
                    }
                    catch { }
                    try
                    {
                        _lanDiscoveryUdp.JoinMulticastGroup(IPAddress.Parse(DefaultLanDiscoveryMulticast), IPAddress.Loopback);
                    }
                    catch { }

                    UdpClient lanDiscoveryUdp = _lanDiscoveryUdp;
                    _lanDiscoveryLoopTask = Task.Run(() => ReceiveLanDiscoveryLoopAsync(lanDiscoveryUdp, wanToken), wanToken);
                    QuicPunchLog.Info($"[LAN DISCOVERY] Listening on common port {LanDiscoveryPort} and multicast group {DefaultLanDiscoveryMulticast}");
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[LAN DISCOVERY] Notice: Could not bind dedicated LAN discovery socket on port {LanDiscoveryPort}: {ex.Message}");
                }
            }

            if (NatCoordinator.Servers.Count == 0)
            {
                var stunEndpoints = await StunGatherer.GatherStunEndpoints(ct: wanToken).ConfigureAwait(false);
                NatCoordinator.SetServerEndpoints(stunEndpoints);
            }

            NatCoordinator.Start(udp, wanToken);
            try
            {
                await NatCoordinator.ExecuteBurstAsync(wanToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[QuicPunch] Initial STUN burst warning: {ex.Message}");
            }

            if (CurrentPeer.Addresses == null || CurrentPeer.Addresses.Length == 0)
            {
                var localIps = Utilities.GetValidLocalIPAddresses();
                if (localIps.Count > 0)
                {
                    CurrentPeer.Addresses = localIps.OrderBy(Utilities.IpToUint).ToArray();
                }
            }

            InvalidateTokenCache();
            WanLastError = null;

            if (WanNostrDiscoveryEnabled && IsStarted)
            {
                try
                {
                    await StartWanNostrDiscoveryAsync(wanToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[NOSTR-WAN] Discovery start warning: {ex.Message}");
                }
            }
        }

        public async Task StopWanAsync()
        {
            await _wanLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopWanCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _wanLifecycleLock.Release();
            }
        }

        private async Task StopWanCoreAsync()
        {
            try { await StopWanNostrDiscoveryAsync().ConfigureAwait(false); } catch { }
            try { _wanLoopCts?.Cancel(); } catch { }

            var oldUdp = udp;
            udp = null;
            try { oldUdp?.Close(); oldUdp?.Dispose(); } catch { }

            var oldLanUdp = _lanDiscoveryUdp;
            _lanDiscoveryUdp = null;
            try { oldLanUdp?.Close(); oldLanUdp?.Dispose(); } catch { }

            NatCoordinator.Stop();
            ResetNatMapping();
            if (CurrentPeer != null)
            {
                CurrentPeer.Addresses = Array.Empty<IPAddress>();
            }

            InvalidateTokenCache();
            WanLastError = null;
        }

        public async Task StartTorAsync(int virtualPort = 0, TorRuntimeOptions? options = null, TorManager? existingTorManager = null, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            await _torLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsTorStarted)
                    return;
                TorLastError = null;
                await StartTorCoreAsync(virtualPort, options, existingTorManager, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                TorLastError = ex.Message;
                TorBootstrapStatus = $"Failed: {ex.Message}";
                throw;
            }
            finally
            {
                _torLifecycleLock.Release();
            }
        }

        private async Task StartTorCoreAsync(int virtualPort, TorRuntimeOptions? options, TorManager? existingTorManager, CancellationToken cancellationToken)
        {
            QuicPunchLog.Info("[TOR SERVER] Initializing Tor runtime...");

            try { _torLoopCts?.Cancel(); _torLoopCts?.Dispose(); } catch { }
            _torLoopCts = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
            CancellationToken torToken = _torLoopCts.Token;

            Directory.CreateDirectory(NodeAppDataPath);
            string identityPath = Path.Combine(NodeAppDataPath, "tor_identity.key");

            if (File.Exists(identityPath))
            {
                try
                {
                    string keyBase64 = await File.ReadAllTextAsync(identityPath, torToken).ConfigureAwait(false);
                    TorIdentity = TorIdentity.FromPrivateKeyBase64(keyBase64);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[TOR SERVER] Warning: Failed to load saved identity: {ex.Message}. Generating new one.");
                    TorIdentity = TorIdentity.CreateRandom();
                    await File.WriteAllTextAsync(identityPath, TorIdentity.PrivateKeyBase64, torToken).ConfigureAwait(false);
                }
            }
            else
            {
                TorIdentity = TorIdentity.CreateRandom();
                await File.WriteAllTextAsync(identityPath, TorIdentity.PrivateKeyBase64, torToken).ConfigureAwait(false);
            }

            int resolvedPort = virtualPort > 0
                ? virtualPort
                : Utilities.GetDeterministicPortFromCertHash(TorCertManager.CertPublicHash);

            if (existingTorManager != null)
            {
                TorManager = existingTorManager;
            }
            else
            {
                var runtimeOptions = options != null
                    ? new TorRuntimeOptions
                    {
                        DataDirectory = options.DataDirectory ?? Path.Combine(NodeAppDataPath, "TorData"),
                        InstallDirectory = options.InstallDirectory,
                        SocksPort = options.SocksPort,
                        ControlPort = options.ControlPort,
                        TransportMode = options.TransportMode,
                        StartupTimeout = options.StartupTimeout,
                        BootstrapTimeout = options.BootstrapTimeout,
                        TierBootstrapTimeout = options.TierBootstrapTimeout,
                        ShutdownTimeout = options.ShutdownTimeout,
                        AdditionalTorrcLines = options.AdditionalTorrcLines,
                        CustomBridges = options.CustomBridges
                    }
                    : new TorRuntimeOptions
                    {
                        DataDirectory = Path.Combine(NodeAppDataPath, "TorData")
                    };

                var trm = new TorRuntimeManager(runtimeOptions);
                TorBootstrapStatus = "Bootstrapping...";
                TorBootstrapProgress = 0;
                trm.LogLine += (line) =>
                {
                    if (line.Contains("Bootstrapped ", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = line.IndexOf("Bootstrapped ", StringComparison.OrdinalIgnoreCase);
                        string progress = line[idx..];
                        TorBootstrapStatus = $"{trm.ActiveTransport}: {progress}";
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d{1,3})%");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int pVal))
                        {
                            TorBootstrapProgress = pVal;
                        }
                        WriteLine($"[TOR SERVER] [{trm.ActiveTransport}] {progress}");
                    }
                    else if (line.Contains("Cascading to ", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Attempting connection via ", StringComparison.OrdinalIgnoreCase))
                    {
                        TorBootstrapStatus = line.Trim();
                        TorBootstrapProgress = 0;
                        WriteLine($"[TOR SERVER] {line.Trim()}");
                    }
                };

                await trm.StartAsync(torToken).ConfigureAwait(false);
                TorManager = new TorManager(trm);
            }

            TorHub = await TorPeerTransportHub.CreateAsync(TorManager, TorIdentity, resolvedPort, torToken).ConfigureAwait(false);

            TorOnionAddress = TorHub.OnionAddress;
            TorCurrentPeer.OnionAddress = TorHub.OnionAddress;
            TorCurrentPeer.MinPort = resolvedPort;
            TorCurrentPeer.MaxPort = resolvedPort;
            InvalidateTokenCache();
            TorBootstrapProgress = 100;
            TorBootstrapStatus = $"Active (100%) [{TorManager.ActiveTransport}]";
            TorLastError = null;
            WriteLine($"[TOR SERVER] Hidden Service active at: {TorOnionAddress}:{resolvedPort} (via {TorManager.ActiveTransport})");

            _torAcceptLoopTask = Task.Run(() => AcceptTorLoopAsync(torToken), CancellationToken.None);

            if (TorNostrDiscoveryEnabled && IsStarted)
            {
                try
                {
                    await StartTorNostrDiscoveryAsync(torToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[NOSTR-TOR] Discovery start warning: {ex.Message}");
                }
            }
        }

        public async Task StopTorAsync()
        {
            await _torLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopTorCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _torLifecycleLock.Release();
            }
        }

        private async Task StopTorCoreAsync()
        {
            TorBootstrapStatus = "Stopping...";
            try { await StopTorNostrDiscoveryAsync().ConfigureAwait(false); } catch { }
            try { _torLoopCts?.Cancel(); } catch { }
            if (TorHub != null)
            {
                try { await TorHub.DisposeAsync().ConfigureAwait(false); } catch { }
                TorHub = null;
            }
            if (TorManager != null)
            {
                try { await TorManager.DisposeAsync().ConfigureAwait(false); } catch { }
                TorManager = null;
            }
            await AwaitTorWorkersAsync().ConfigureAwait(false);
            TorOnionAddress = null;
            if (CurrentPeer != null)
            {
                CurrentPeer.OnionAddress = null;
            }
            if (TorCurrentPeer != null)
            {
                TorCurrentPeer.OnionAddress = null;
            }
            InvalidateTokenCache();
            TorBootstrapStatus = "Stopped";
            TorBootstrapProgress = 0;
            TorLastError = null;
        }

        public async Task SendResponseAsync(byte[] payload, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel = null)
        {
            if (LifecycleState != QuicPunchLifecycleState.Started)
                return;
            if (transport == TransportType.Tor)
            {
                if (torChannel != null)
                {
                    await torChannel.SendMessageAsync(payload).ConfigureAwait(false);
                }
                else
                {
                    var peer = AvailablePeers.Values.FirstOrDefault(p =>
                        p.TorChannel != null && !p.TorChannel.IsClosed && (
                            (remoteEndPoint is DnsEndPoint dns && (string.Equals(p.OnionAddress, dns.Host, StringComparison.OrdinalIgnoreCase) || string.Equals(p.TorChannel.RemoteOnion, dns.Host, StringComparison.OrdinalIgnoreCase))) ||
                            (p.ActiveEndPoint != null && p.ActiveEndPoint.Equals(remoteEndPoint))
                        ));
                    if (peer?.TorChannel != null)
                    {
                        await peer.TorChannel.SendMessageAsync(payload).ConfigureAwait(false);
                    }
                }
            }
            else if (udp != null && remoteEndPoint is IPEndPoint ipEp)
            {
                await udp.SendAsync(payload, ipEp).ConfigureAwait(false);
            }
        }

        public async Task SendToPeerAsync(PeerInfo peer, byte[] payload, TransportType transport = TransportType.Wan)
        {
            EnsureStarted();
            if ((transport == TransportType.Tor || peer.ActiveTransport == TransportType.Tor) && peer.TorChannel != null)
            {
                await peer.TorChannel.SendMessageAsync(payload).ConfigureAwait(false);
            }
            else if (udp != null && peer.ActiveEndPoint != null)
            {
                await udp.SendAsync(payload, peer.ActiveEndPoint).ConfigureAwait(false);
            }
        }

        public async Task ConnectTorAsync(string remoteOnion, int remotePort = 443, CancellationToken token = default, PeerInfo? targetPeer = null)
        {
            EnsureStarted();
            if (TorManager == null || TorHub == null)
                throw new InvalidOperationException("Tor service is not started. Call StartTorAsync first.");

            var channel = await TorQuicConnectionManager.ConnectAsync(TorManager, TorHub, remoteOnion, remotePort, cancellationToken: token).ConfigureAwait(false);
            StartTorReceiveLoop(channel);

            var peerInfo = targetPeer ?? new PeerInfo();
            peerInfo.OnionAddress = remoteOnion;
            peerInfo.MinPort = remotePort;
            peerInfo.MaxPort = remotePort;
            peerInfo.NetworkType = NetworkType.Tor;
            peerInfo.ActiveTransport = TransportType.Tor;
            if (targetPeer == null)
            {
                peerInfo.TorChannel = channel;
            }

            var payload = GenerateHelloPayload(MessageType.Interrogation, true, transport: TransportType.Tor, targetPeer: peerInfo);
            await channel.SendMessageAsync(payload, token).ConfigureAwait(false);
        }

        private byte[]? _connectionPassword;
        private static readonly byte[] DefaultPasswordSalt = SHA3_256.HashData(Encoding.UTF8.GetBytes("QuicPunch-P2P-Default-Password-Salt-v1"));

        internal void DerivePasswordHash()
        {
            if (_connectionPassword == null)
            {
                PasswordHash = null;
                return;
            }

            byte[] salt = (_poolId != null && _poolId.Length > 0)
                ? SHA3_256.HashData(_poolId)
                : DefaultPasswordSalt;

            PasswordHash = Rfc2898DeriveBytes.Pbkdf2(_connectionPassword, salt, 100_000, HashAlgorithmName.SHA3_512, 64);
        }

        private byte[] _poolId = [];
        public bool WanNostrDiscoveryEnabled { get; set; }
        public bool TorNostrDiscoveryEnabled { get; set; }
        public bool NostrDiscoveryEnabled
        {
            get => WanNostrDiscoveryEnabled;
            set => WanNostrDiscoveryEnabled = value;
        }

        public byte[] PoolId
        {
            get => _poolId;
            set
            {
                if (value.Length != 20) throw new ArgumentException("Pool ID must be 20 bytes long.");

                _poolId = value;

                if (_connectionPassword != null)
                    DerivePasswordHash();

                if (IsStarted)
                {
                    if (WanNostrDiscoveryEnabled && WanNostrDiscovery != null)
                        _ = RestartWanNostrDiscoveryAsync(LifecycleToken);
                    if (TorNostrDiscoveryEnabled && TorNostrDiscovery != null)
                        _ = RestartTorNostrDiscoveryAsync(LifecycleToken);
                }
            }
        }
        internal byte[]? PasswordHash { get; set; }
        public PeerAccessController AccessController { get; }
        public bool AutoAcceptConnections
        {
            get => AccessController.AutoAcceptConnections;
            set => AccessController.AutoAcceptConnections = value;
        }

        public bool AutoAcceptUntrustedConnections
        {
            get => AccessController.AutoAcceptUntrustedConnections;
            set => AccessController.AutoAcceptUntrustedConnections = value;
        }

        public bool SharePeers { get; set; }
        public bool AcceptSharedPeers { get; set; }

        public void SetAutoAcceptAll(bool autoAccept) => AccessController.SetAutoAcceptAll(autoAccept);
        public void SetPeerAutoAccept(Guid peerId, bool autoAccept) => AccessController.SetPeerAutoAccept(peerId, autoAccept);
        public void SetPeerAutoAccept(byte[] certHash, bool autoAccept) => AccessController.SetPeerAutoAccept(certHash, autoAccept);
        public bool IsPeerAutoAccepted(Guid peerId) => AccessController.IsAutoAccepted(peerId);
        public bool IsPeerAutoAccepted(byte[] certHash) => AccessController.IsAutoAccepted(certHash);
        public IReadOnlyList<Guid> GetAutoAcceptedPeers() => AccessController.GetAutoAcceptedPeers();

        public NostrDiscovery? WanNostrDiscovery { get; private set; }
        public NostrDiscovery? TorNostrDiscovery { get; private set; }
        public NostrDiscovery? NostrDiscovery => WanNostrDiscovery;
        public string[]? NostrRelays { get; set; }
        public string[]? TorNostrRelays { get; set; }
        public ConcurrentDictionary<string, DiscoveredPeerInfo> DiscoveredPeers { get; } = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxNostrCandidateCount = 256;
        private static readonly TimeSpan NostrCandidateCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan NostrCandidateTtl = TimeSpan.FromMinutes(3);
        private readonly object _nostrCandidateLock = new();
        private readonly Dictionary<string, long> _nostrCandidates = new(StringComparer.Ordinal);
        private const int MaxConcurrentDiscoveryInterrogations = 8;
        private readonly SemaphoreSlim _discoveryInterrogationSlots = new(MaxConcurrentDiscoveryInterrogations, MaxConcurrentDiscoveryInterrogations);
        private readonly SemaphoreSlim _nostrLifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _torNostrLifecycleLock = new(1, 1);
        public CancellationTokenSource CancellationSource { get; private set; }

        public void RecordDiscoveredPeer(PeerInfo peer, string token, string source = "Nostr")
        {
            if (peer == null || !peer.TryGetCertificateHash(out var hash))
                return;

            string certHashB64 = Convert.ToBase64String(hash);
            string hexKey = Convert.ToHexString(hash);

            string resolvedName = !string.IsNullOrWhiteSpace(peer.Name) ? peer.Name : "";
            if (string.IsNullOrWhiteSpace(resolvedName) && PeerStore != null && PeerStore.TryGet(hash, out var sp) && !string.IsNullOrWhiteSpace(sp?.Name))
            {
                resolvedName = sp.Name;
            }
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                if (TryGetPeerByCertHash(hash, out var avail) && !string.IsNullOrWhiteSpace(avail?.Name))
                    resolvedName = avail.Name;
            }
            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                var act = ActiveInterrogations.Values.FirstOrDefault(s => s.Peer.TryGetCertificateHash(out var h) && CryptographicOperations.FixedTimeEquals(h, hash));
                if (act != null && !string.IsNullOrWhiteSpace(act.Peer.Name) && act.Peer.Name != "Saved Peer" && act.Peer.Name != "Peer" && act.Peer.Name != "Unknown")
                    resolvedName = act.Peer.Name;
            }

            if (!string.IsNullOrWhiteSpace(resolvedName))
                peer.Name = resolvedName;

            var info = new DiscoveredPeerInfo
            {
                Id = peer.Id != Guid.Empty ? peer.Id.ToString() : hexKey[..16],
                Name = !string.IsNullOrWhiteSpace(resolvedName) ? resolvedName : "Discovered Peer",
                Token = token,
                CertHash = hash,
                CertHashBase64 = certHashB64,
                Addresses = peer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                OnionAddress = peer.OnionAddress ?? "",
                MinPort = peer.MinPort,
                MaxPort = peer.MaxPort,
                NetworkType = peer.NetworkType,
                NostrPubKey = peer.NostrPubKey,
                Source = source,
                DiscoveredAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                IsCertVerified = peer.IsCertVerified,
                CertPublicKey = peer.CertPublicKey,
                CertPublicKeyBase64 = peer.CertPublicKey != null ? Convert.ToBase64String(peer.CertPublicKey) : null
            };

            DiscoveredPeers.AddOrUpdate(hexKey, info, (_, existing) =>
            {
                existing.Token = token;
                existing.LastSeen = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(resolvedName) && resolvedName != "Discovered Peer") existing.Name = resolvedName;
                else if (!string.IsNullOrWhiteSpace(peer.Name) && peer.Name != "Discovered Peer") existing.Name = peer.Name;
                if (peer.Addresses != null && peer.Addresses.Length > 0)
                    existing.Addresses = peer.Addresses.Select(a => a.ToString()).ToArray();
                if (!string.IsNullOrWhiteSpace(peer.OnionAddress))
                    existing.OnionAddress = peer.OnionAddress;
                if (!string.IsNullOrWhiteSpace(peer.NostrPubKey))
                    existing.NostrPubKey = peer.NostrPubKey;
                existing.MinPort = peer.MinPort;
                existing.MaxPort = peer.MaxPort;
                existing.NetworkType = peer.NetworkType;
                existing.Source = source;
                existing.IsCertVerified = peer.IsCertVerified;
                if (peer.CertPublicKey != null)
                {
                    existing.CertPublicKey = peer.CertPublicKey;
                    existing.CertPublicKeyBase64 = Convert.ToBase64String(peer.CertPublicKey);
                }
                return existing;
            });

            PruneStaleDiscoveredPeers();
        }

        public void PruneStaleDiscoveredPeers(TimeSpan? maxAge = null)
        {
            var age = maxAge ?? TimeSpan.FromMinutes(5);
            var cutoff = DateTime.UtcNow - age;
            foreach (var kv in DiscoveredPeers)
            {
                if (kv.Value.LastSeen < cutoff)
                {
                    DiscoveredPeers.TryRemove(kv.Key, out _);
                }
            }
        }

        public Task SetPeerDiscoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            SetWanPeerDiscoveryEnabledAsync(enabled, cancellationToken);

        public async Task SetWanPeerDiscoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(QuicPunch));

            cancellationToken.ThrowIfCancellationRequested();
            WanNostrDiscoveryEnabled = enabled;
            if (!IsStarted)
                return;

            await _nostrLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (enabled)
                {
                    await StartWanNostrDiscoveryCoreAsync(LifecycleToken).ConfigureAwait(false);
                }
                else
                {
                    lock (_nostrCandidateLock)
                    {
                        foreach (var key in _nostrCandidates.Keys.Where(k => !k.EndsWith(":Tor", StringComparison.OrdinalIgnoreCase)).ToArray())
                            _nostrCandidates.Remove(key);
                    }

                    await StopWanNostrDiscoveryCoreAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _nostrLifecycleLock.Release();
            }
        }

        public async Task SetTorPeerDiscoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (IsDisposed)
                throw new ObjectDisposedException(nameof(QuicPunch));

            cancellationToken.ThrowIfCancellationRequested();
            TorNostrDiscoveryEnabled = enabled;
            if (!IsStarted || !IsTorStarted)
                return;

            await _torNostrLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (enabled)
                {
                    await StartTorNostrDiscoveryCoreAsync(LifecycleToken).ConfigureAwait(false);
                }
                else
                {
                    lock (_nostrCandidateLock)
                    {
                        foreach (var key in _nostrCandidates.Keys.Where(k => k.EndsWith(":Tor", StringComparison.OrdinalIgnoreCase)).ToArray())
                            _nostrCandidates.Remove(key);
                    }

                    await StopTorNostrDiscoveryCoreAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _torNostrLifecycleLock.Release();
            }
        }

        private async Task StartWanNostrDiscoveryAsync(CancellationToken token)
        {
            await _nostrLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StartWanNostrDiscoveryCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _nostrLifecycleLock.Release();
            }
        }

        private async Task StartWanNostrDiscoveryCoreAsync(CancellationToken token)
        {
            if (!WanNostrDiscoveryEnabled || token.IsCancellationRequested || !IsStarted)
                return;

            if (WanNostrDiscovery is { IsRunning: true })
                return;

            await StopWanNostrDiscoveryCoreAsync().ConfigureAwait(false);

            var discovery = new NostrDiscovery(
                _poolId.Length == 20 ? _poolId : null,
                GetWanNostrDiscoveryPayload,
                scope: "wan",
                nostrPrivateKey: CertManager.NostrPrivateKey);
            discovery.OnEventDiscovered += OnWanNostrEventDiscovered;
            discovery.OnTokenFound += OnWanNostrTokenDiscovered;
            WanNostrDiscovery = discovery;
            try
            {
                await discovery.StartAsync(NostrRelays, token).ConfigureAwait(false);
            }
            catch
            {
                if (ReferenceEquals(WanNostrDiscovery, discovery))
                    WanNostrDiscovery = null;
                discovery.Dispose();
                throw;
            }
        }

        private async Task StopWanNostrDiscoveryAsync()
        {
            await _nostrLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopWanNostrDiscoveryCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _nostrLifecycleLock.Release();
            }
        }

        private async Task StopWanNostrDiscoveryCoreAsync()
        {
            var previous = WanNostrDiscovery;
            WanNostrDiscovery = null;
            if (previous == null)
                return;

            previous.OnEventDiscovered -= OnWanNostrEventDiscovered;
            previous.OnTokenFound -= OnWanNostrTokenDiscovered;
            try { await previous.StopAsync().ConfigureAwait(false); } catch { }
            previous.Dispose();
        }

        private async Task RestartWanNostrDiscoveryAsync(CancellationToken token)
        {
            await _nostrLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StopWanNostrDiscoveryCoreAsync().ConfigureAwait(false);
                if (WanNostrDiscoveryEnabled && IsStarted && !token.IsCancellationRequested)
                    await StartWanNostrDiscoveryCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _nostrLifecycleLock.Release();
            }
        }

        private async Task StartTorNostrDiscoveryAsync(CancellationToken token)
        {
            await _torNostrLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StartTorNostrDiscoveryCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _torNostrLifecycleLock.Release();
            }
        }

        private async Task StartTorNostrDiscoveryCoreAsync(CancellationToken token)
        {
            if (!TorNostrDiscoveryEnabled || token.IsCancellationRequested || !IsStarted || !IsTorStarted)
                return;

            if (TorNostrDiscovery is { IsRunning: true })
                return;

            await StopTorNostrDiscoveryCoreAsync().ConfigureAwait(false);

            var discovery = new NostrDiscovery(
                _poolId.Length == 20 ? _poolId : null,
                GetTorNostrDiscoveryPayload,
                scope: "tor",
                proxyProvider: GetTorSocksProxy,
                nostrPrivateKey: TorCertManager.NostrPrivateKey);
            discovery.OnEventDiscovered += OnTorNostrEventDiscovered;
            discovery.OnTokenFound += OnTorNostrTokenDiscovered;
            TorNostrDiscovery = discovery;
            try
            {
                await discovery.StartAsync(TorNostrRelays ?? NostrRelays, token).ConfigureAwait(false);
            }
            catch
            {
                if (ReferenceEquals(TorNostrDiscovery, discovery))
                    TorNostrDiscovery = null;
                discovery.Dispose();
                throw;
            }
        }

        private async Task StopTorNostrDiscoveryAsync()
        {
            await _torNostrLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopTorNostrDiscoveryCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _torNostrLifecycleLock.Release();
            }
        }

        private async Task StopTorNostrDiscoveryCoreAsync()
        {
            var previous = TorNostrDiscovery;
            TorNostrDiscovery = null;
            if (previous == null)
                return;

            previous.OnEventDiscovered -= OnTorNostrEventDiscovered;
            previous.OnTokenFound -= OnTorNostrTokenDiscovered;
            try { await previous.StopAsync().ConfigureAwait(false); } catch { }
            previous.Dispose();
        }

        private async Task RestartTorNostrDiscoveryAsync(CancellationToken token)
        {
            await _torNostrLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StopTorNostrDiscoveryCoreAsync().ConfigureAwait(false);
                if (TorNostrDiscoveryEnabled && IsStarted && IsTorStarted && !token.IsCancellationRequested)
                    await StartTorNostrDiscoveryCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _torNostrLifecycleLock.Release();
            }
        }

        private async Task StartNostrDiscoveryAsync(CancellationToken token)
        {
            await StartWanNostrDiscoveryAsync(token).ConfigureAwait(false);
            if (IsTorStarted)
                await StartTorNostrDiscoveryAsync(token).ConfigureAwait(false);
        }

        private async Task StopNostrDiscoveryAsync()
        {
            await StopWanNostrDiscoveryAsync().ConfigureAwait(false);
            await StopTorNostrDiscoveryAsync().ConfigureAwait(false);
        }

        private async Task RestartNostrDiscoveryAsync(CancellationToken token)
        {
            await RestartWanNostrDiscoveryAsync(token).ConfigureAwait(false);
            if (IsTorStarted)
                await RestartTorNostrDiscoveryAsync(token).ConfigureAwait(false);
        }

        private async Task PublishNostrDiscoveryAsync(CancellationToken token)
        {
            var wanDiscovery = WanNostrDiscovery;
            if (wanDiscovery != null && wanDiscovery.IsRunning && !token.IsCancellationRequested)
            {
                try { await wanDiscovery.PublishNowAsync(token).ConfigureAwait(false); } catch { }
            }

            var torDiscovery = TorNostrDiscovery;
            if (torDiscovery != null && torDiscovery.IsRunning && !token.IsCancellationRequested)
            {
                if (wanDiscovery != null && wanDiscovery.IsRunning)
                {
                    int delayMs = RandomNumberGenerator.GetInt32(500, 3000);
                    try { await Task.Delay(delayMs, token).ConfigureAwait(false); } catch { }
                }
                try { await torDiscovery.PublishNowAsync(token).ConfigureAwait(false); } catch { }
            }
        }

        private IWebProxy? GetTorSocksProxy()
        {
            int socksPort = TorManager?.SocksPort ?? 0;
            if (socksPort > 0)
            {
                return new WebProxy($"socks5://127.0.0.1:{socksPort}");
            }
            return null;
        }

        private string? LastToken;

        public Task StartStunRequest() => StartStunRequest(_lifecycleGeneration, LifecycleToken);

        private async Task StartStunRequest(long generation, CancellationToken token)
        {
            await SendLocalLanDiscoveryAsync().ConfigureAwait(false);

            while (IsLifecycleWorkerCurrent(generation, token))
            {
                try
                {
                    await StunRequest(cancellationToken: token, generation: generation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        QuicPunchLog.Error("[QuicPunch] Error in STUN loop", ex);
                }

                try
                {
                    await Task.Delay(20000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public Task StartPingLoopAsync() => StartPingLoopAsync(_lifecycleGeneration, LifecycleToken);

        private async Task StartPingLoopAsync(long generation, CancellationToken token)
        {
            while (IsLifecycleWorkerCurrent(generation, token))
            {
                try
                {
                    if (!AvailablePeers.IsEmpty)
                    {
                        byte[]? wanPingReq = udp != null ? BuildPingPacket(PreciseTime.GetCorrectTime().Ticks, false, TransportType.Wan) : null;
                        byte[]? torPingReq = IsTorStarted ? BuildPingPacket(PreciseTime.GetCorrectTime().Ticks, false, TransportType.Tor) : null;

                        foreach (var peer in AvailablePeers.Values)
                        {
                            if (peer.ActiveTransport == TransportType.Tor || (peer.TorChannel != null && !peer.TorChannel.IsClosed))
                            {
                                if (torPingReq != null && peer.TorChannel != null && !peer.TorChannel.IsClosed)
                                {
                                    try
                                    {
                                        await peer.TorChannel.SendMessageAsync(torPingReq, token).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        QuicPunchLog.Info($"[TOR PING] Notice: ping to {peer.Name} failed: {ex.Message}");
                                    }
                                }
                            }
                            else if (wanPingReq != null && udp != null && peer.ActiveEndPoint != null)
                            {
                                await udp.SendAsync(wanPingReq, peer.ActiveEndPoint, token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { }

                try { await Task.Delay(2000, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        public async Task SendLocalLanDiscoveryAsync()
        {
            try
            {
                var payload = GenerateHelloPayload(MessageType.Interrogation, true);
                if (udp != null)
                {
                    udp.EnableBroadcast = true;
                    udp.MulticastLoopback = true;
                    var mcastEp = new IPEndPoint(IPAddress.Parse(DefaultLanDiscoveryMulticast), LanDiscoveryPort);
                    try { await udp.SendAsync(payload, mcastEp).ConfigureAwait(false); } catch { }
                    try
                    {
                        using var loopSender = new UdpClient();
                        ConfigureUdpSocket(loopSender);
                        loopSender.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Loopback.GetAddressBytes());
                        loopSender.MulticastLoopback = true;
                        await loopSender.SendAsync(payload, mcastEp).ConfigureAwait(false);
                    }
                    catch { }
                    try { await udp.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, LanDiscoveryPort)).ConfigureAwait(false); } catch { }
                    try { await udp.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, LanDiscoveryPort)).ConfigureAwait(false); } catch { }
                }
            }
            catch { }
        }

        private string? GetWanNostrDiscoveryPayload()
        {
            try
            {
                string? token = GetWanToken();
                if (string.IsNullOrEmpty(token)) return null;

                byte[]? certPubKey = CertManager?.PeerCertificate?.GetPublicKey();
                if (certPubKey == null || certPubKey.Length == 0)
                    return token;

                byte[] sig = Utilities.SignToken(token, CertManager?.PeerCertificate);

                return JsonSerializer.Serialize(new
                {
                    wan = token,
                    certPubKey = Convert.ToBase64String(certPubKey),
                    sig = Convert.ToBase64String(sig)
                });
            }
            catch { return null; }
        }

        private string? GetTorNostrDiscoveryPayload()
        {
            try
            {
                string? token = GetTorToken();
                if (string.IsNullOrEmpty(token)) return null;

                var torCert = TorCertManager?.PeerCertificate;
                if (torCert == null) return null;

                byte[]? certPubKey = torCert.GetPublicKey();
                if (certPubKey == null || certPubKey.Length == 0)
                    return token;

                byte[] sig = Utilities.SignToken(token, torCert);

                return JsonSerializer.Serialize(new
                {
                    tor = token,
                    certPubKey = Convert.ToBase64String(certPubKey),
                    sig = Convert.ToBase64String(sig)
                });
            }
            catch { return null; }
        }

        private void OnWanNostrTokenDiscovered(string token)
        {
            OnWanNostrEventDiscovered(string.Empty, token);
        }

        private void OnWanNostrEventDiscovered(string nostrPubKeyHex, string token)
        {
            if (!WanNostrDiscoveryEnabled || !IsStarted || string.IsNullOrWhiteSpace(token))
                return;

            string trimmed = token.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    byte[]? certPubKeyBytes = null;
                    byte[]? sigBytes = null;
                    if (document.RootElement.TryGetProperty("certPubKey", out var cpkEl) && cpkEl.ValueKind == JsonValueKind.String)
                    {
                        string? cpkStr = cpkEl.GetString();
                        if (!string.IsNullOrWhiteSpace(cpkStr))
                        {
                            try { certPubKeyBytes = Convert.FromBase64String(cpkStr); } catch { }
                        }
                    }
                    if (document.RootElement.TryGetProperty("sig", out var sigEl) && sigEl.ValueKind == JsonValueKind.String)
                    {
                        string? sigStr = sigEl.GetString();
                        if (!string.IsNullOrWhiteSpace(sigStr))
                        {
                            try { sigBytes = Convert.FromBase64String(sigStr); } catch { }
                        }
                    }

                    if (document.RootElement.TryGetProperty("wan", out var wanEl) && wanEl.ValueKind == JsonValueKind.String)
                    {
                        string? wan = wanEl.GetString();
                        if (!string.IsNullOrWhiteSpace(wan))
                        {
                            ProcessWanNostrEndpointToken(wan, nostrPubKeyHex, certPubKeyBytes, sigBytes);
                            return;
                        }
                    }
                }
                catch (JsonException) { return; }
            }

            ProcessWanNostrEndpointToken(trimmed, nostrPubKeyHex, null, null);
        }

        private void ProcessWanNostrEndpointToken(string token, string? nostrPubKeyHex = null, byte[]? certPubKey = null, byte[]? signature = null) =>
            ProcessNostrEndpointTokenCore(token, nostrPubKeyHex, certPubKey, signature, isTor: false);


        private void OnTorNostrTokenDiscovered(string token)
        {
            OnTorNostrEventDiscovered(string.Empty, token);
        }

        private void OnTorNostrEventDiscovered(string nostrPubKeyHex, string token)
        {
            if (!TorNostrDiscoveryEnabled || !IsStarted || string.IsNullOrWhiteSpace(token))
                return;

            string trimmed = token.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    byte[]? certPubKeyBytes = null;
                    byte[]? sigBytes = null;
                    if (document.RootElement.TryGetProperty("certPubKey", out var cpkEl) && cpkEl.ValueKind == JsonValueKind.String)
                    {
                        string? cpkStr = cpkEl.GetString();
                        if (!string.IsNullOrWhiteSpace(cpkStr))
                        {
                            try { certPubKeyBytes = Convert.FromBase64String(cpkStr); } catch { }
                        }
                    }
                    if (document.RootElement.TryGetProperty("sig", out var sigEl) && sigEl.ValueKind == JsonValueKind.String)
                    {
                        string? sigStr = sigEl.GetString();
                        if (!string.IsNullOrWhiteSpace(sigStr))
                        {
                            try { sigBytes = Convert.FromBase64String(sigStr); } catch { }
                        }
                    }

                    if (document.RootElement.TryGetProperty("tor", out var torEl) && torEl.ValueKind == JsonValueKind.String)
                    {
                        string? tor = torEl.GetString();
                        if (!string.IsNullOrWhiteSpace(tor))
                        {
                            ProcessTorNostrEndpointToken(tor, nostrPubKeyHex, certPubKeyBytes, sigBytes);
                            return;
                        }
                    }
                }
                catch (JsonException) { return; }
            }

            ProcessTorNostrEndpointToken(trimmed, nostrPubKeyHex, null, null);
        }

        private void ProcessTorNostrEndpointToken(string token, string? nostrPubKeyHex = null, byte[]? certPubKey = null, byte[]? signature = null) =>
            ProcessNostrEndpointTokenCore(token, nostrPubKeyHex, certPubKey, signature, isTor: true);

        private void ProcessNostrEndpointTokenCore(string token, string? nostrPubKeyHex, byte[]? certPubKey, byte[]? signature, bool isTor)
        {
            string tag = isTor ? "[NOSTR-TOR]" : "[NOSTR-WAN]";
            try
            {
                var peer = Utilities.DecodeEndpointToken(token);
                if (isTor ? peer.NetworkType != NetworkType.Tor : peer.NetworkType == NetworkType.Tor)
                {
                    // Filter matching transport endpoints
                    peer.Dispose();
                    return;
                }

                if (!peer.TryGetCertificateHash(out var peerHash))
                {
                    peer.Dispose();
                    return;
                }

                if (isTor && TorCurrentPeer != null && TorCurrentPeer.TryGetCertificateHash(out var torCurrentHash) &&
                    CryptographicOperations.FixedTimeEquals(peerHash, torCurrentHash))
                {
                    peer.Dispose();
                    return;
                }

                if (CurrentPeer.TryGetCertificateHash(out var currentHash) &&
                    CryptographicOperations.FixedTimeEquals(peerHash, currentHash))
                {
                    peer.Dispose();
                    return;
                }

                if (certPubKey != null && certPubKey.Length > 0 && signature != null && signature.Length > 0)
                {
                    if (Utilities.TryVerifyTokenCertificate(token, certPubKey, signature, out var verifiedHash) &&
                        verifiedHash != null && CryptographicOperations.FixedTimeEquals(verifiedHash, peerHash))
                    {
                        peer.IsCertVerified = true;
                        peer.CertPublicKey = certPubKey;
                    }
                    else
                    {
                        QuicPunchLog.Info($"{tag} REJECTED token for {peer.Name ?? Convert.ToHexString(peerHash)[..8]}: certificate public key does not match token hash or signature.");
                        peer.Dispose();
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(nostrPubKeyHex))
                {
                    peer.NostrPubKey = nostrPubKeyHex;
                }

                // If this peer is already saved in PeerStore, verify the Nostr author key before applying cooldowns
                if (PeerStore != null && PeerStore.TryGet(peerHash, out var savedPeer) && savedPeer != null)
                {
                    if (!string.IsNullOrEmpty(savedPeer.NostrPubKey) && !string.IsNullOrEmpty(nostrPubKeyHex))
                    {
                        if (!string.Equals(savedPeer.NostrPubKey, nostrPubKeyHex, StringComparison.OrdinalIgnoreCase))
                        {
                            QuicPunchLog.Info($"{tag} REJECTED spoofed token for saved peer {savedPeer.Name ?? Convert.ToHexString(peerHash)[..8]}: author {nostrPubKeyHex[..8]} != registered {savedPeer.NostrPubKey[..8]}");
                            peer.Dispose();
                            return;
                        }
                    }
                }

                string hexKey = Convert.ToHexString(peerHash);
                string candidateKey = (string.IsNullOrEmpty(nostrPubKeyHex) ? hexKey : nostrPubKeyHex) + ":" + (isTor ? "Tor" : peer.NetworkType.ToString());

                // Update saved peer endpoints and auto-reconnect if needed
                string? nostrKeyToSave = nostrPubKeyHex;
                PeerStore.SavedPeer? savedPeerToUpdate = null;
                if (PeerStore != null)
                {
                    if (!PeerStore.TryGet(peerHash, out savedPeerToUpdate) || savedPeerToUpdate == null)
                    {
                        if (!string.IsNullOrEmpty(nostrPubKeyHex))
                            PeerStore.TryGetByNostrPubKey(nostrPubKeyHex, out savedPeerToUpdate);

                        if (savedPeerToUpdate == null)
                        {
                            if (!isTor && peer.Addresses != null && peer.Addresses.Length > 0)
                                PeerStore.TryGetByAddress(peer.Addresses, out savedPeerToUpdate);
                            else if (isTor && !string.IsNullOrEmpty(peer.OnionAddress))
                                PeerStore.TryGetByOnion(peer.OnionAddress, out savedPeerToUpdate);
                        }
                    }

                    if (savedPeerToUpdate != null)
                    {
                        if (!string.IsNullOrWhiteSpace(savedPeerToUpdate.Name))
                            peer.Name = savedPeerToUpdate.Name;
                        nostrKeyToSave = savedPeerToUpdate.NostrPubKey ?? nostrPubKeyHex;

                        if (!CryptographicOperations.FixedTimeEquals(savedPeerToUpdate.CertHash, peerHash))
                        {
                            PeerStore.UpdatePeerCertificate(savedPeerToUpdate.CertHash, peerHash, save: false);
                        }

                        PeerStore.AddOrUpdate(peer, autoConnect: savedPeerToUpdate.AutoConnect, save: true, nostrPubKey: nostrKeyToSave);
                    }
                }

                bool endpointsChanged = false;
                if (DiscoveredPeers.TryGetValue(hexKey, out var existingDp))
                {
                    if (!isTor)
                    {
                        var existingIps = new HashSet<string>(existingDp.Addresses ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                        var newIps = new HashSet<string>(peer.Addresses?.Select(a => a.ToString()) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                        endpointsChanged = !existingIps.SetEquals(newIps) || existingDp.MinPort != peer.MinPort || existingDp.MaxPort != peer.MaxPort;
                    }
                    else
                    {
                        endpointsChanged = !string.Equals(existingDp.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase) ||
                                           existingDp.MinPort != peer.MinPort || existingDp.MaxPort != peer.MaxPort;
                    }
                }

                var activeInterrogations = ActiveInterrogations.Values
                    .Where(session =>
                        (session.Peer.TryGetCertificateHash(out var sHash) && CryptographicOperations.FixedTimeEquals(sHash, peerHash)) ||
                        (savedPeerToUpdate != null && session.Peer.TryGetCertificateHash(out var sHash2) && CryptographicOperations.FixedTimeEquals(sHash2, savedPeerToUpdate.CertHash)) ||
                        (!isTor && session.Peer.Addresses != null && peer.Addresses != null && session.Peer.Addresses.Any(a => !IPAddress.IsLoopback(a) && peer.Addresses.Contains(a))) ||
                        (isTor && !string.IsNullOrEmpty(session.Peer.OnionAddress) && string.Equals(session.Peer.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase)) ||
                        PeerTargetsMatch(session.Peer, peer))
                    .ToList();

                bool wasConnecting = activeInterrogations.Count > 0;
                if (wasConnecting)
                {
                    foreach (var s in activeInterrogations)
                    {
                        if (!isTor)
                        {
                            var sIps = new HashSet<IPAddress>(s.Peer.Addresses ?? Array.Empty<IPAddress>());
                            var newIps = new HashSet<IPAddress>(peer.Addresses ?? Array.Empty<IPAddress>());
                            if (!sIps.SetEquals(newIps) || s.Peer.MinPort != peer.MinPort || s.Peer.MaxPort != peer.MaxPort || (s.Peer.TryGetCertificateHash(out var sCert) && !CryptographicOperations.FixedTimeEquals(sCert, peerHash)))
                            {
                                endpointsChanged = true;
                            }
                        }
                        else
                        {
                            if (!string.Equals(s.Peer.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase) ||
                                s.Peer.MinPort != peer.MinPort || s.Peer.MaxPort != peer.MaxPort || (s.Peer.TryGetCertificateHash(out var sCert) && !CryptographicOperations.FixedTimeEquals(sCert, peerHash)))
                            {
                                endpointsChanged = true;
                            }
                        }
                    }
                }

                if (savedPeerToUpdate != null)
                {
                    if (!isTor)
                    {
                        if (!CryptographicOperations.FixedTimeEquals(savedPeerToUpdate.CertHash, peerHash) || savedPeerToUpdate.MinPort != peer.MinPort || savedPeerToUpdate.MaxPort != peer.MaxPort)
                        {
                            endpointsChanged = true;
                        }
                    }
                    else
                    {
                        if (!CryptographicOperations.FixedTimeEquals(savedPeerToUpdate.CertHash, peerHash) || savedPeerToUpdate.MinPort != peer.MinPort || savedPeerToUpdate.MaxPort != peer.MaxPort || (!string.IsNullOrEmpty(peer.OnionAddress) && !string.Equals(savedPeerToUpdate.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase)))
                        {
                            endpointsChanged = true;
                        }
                    }
                }

                long now = Stopwatch.GetTimestamp();
                lock (_nostrCandidateLock)
                {
                    foreach (var stale in _nostrCandidates
                        .Where(kvp => Stopwatch.GetElapsedTime(kvp.Value, now) > NostrCandidateTtl)
                        .Select(kvp => kvp.Key)
                        .ToArray())
                    {
                        _nostrCandidates.Remove(stale);
                    }

                    if (!endpointsChanged &&
                        _nostrCandidates.TryGetValue(candidateKey, out long lastSeen) &&
                        Stopwatch.GetElapsedTime(lastSeen, now) < NostrCandidateCooldown)
                    {
                        peer.Dispose();
                        return;
                    }

                    if (!_nostrCandidates.ContainsKey(candidateKey) && _nostrCandidates.Count >= MaxNostrCandidateCount)
                    {
                        peer.Dispose();
                        return;
                    }

                    _nostrCandidates[candidateKey] = now;
                }

                if (!TryGetPeerByCertHash(peerHash, out var activePeer) && savedPeerToUpdate?.CertHash != null)
                {
                    TryGetPeerByCertHash(savedPeerToUpdate.CertHash, out activePeer);
                }
                if (activePeer != null)
                {
                    if (!isTor)
                    {
                        if (peer.Addresses != null && peer.Addresses.Length > 0)
                            activePeer.Addresses = peer.Addresses;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(peer.OnionAddress))
                            activePeer.OnionAddress = peer.OnionAddress;
                    }
                    activePeer.MinPort = peer.MinPort;
                    activePeer.MaxPort = peer.MaxPort;
                    activePeer.NetworkType = peer.NetworkType;
                    if (!string.IsNullOrEmpty(nostrKeyToSave))
                        activePeer.NostrPubKey = nostrKeyToSave;
                    activePeer.IsCertVerified = peer.IsCertVerified;
                    if (peer.CertPublicKey != null)
                        activePeer.CertPublicKey = peer.CertPublicKey;
                    if (!string.IsNullOrWhiteSpace(peer.Name))
                        activePeer.Name = peer.Name;
                }

                // If this peer is currently being interrogated and changed endpoints, cancel old sessions
                if (wasConnecting && endpointsChanged)
                {
                    foreach (var s in activeInterrogations)
                    {
                        CancelInterrogation(s.Id);
                    }
                }

                bool isConnectedAndHealthy = activePeer != null && activePeer.IsConnectedAndResponsive();

                // Reconnect policy
                bool shouldReconnect = (wasConnecting && endpointsChanged) ||
                                       (!isConnectedAndHealthy && savedPeerToUpdate != null && savedPeerToUpdate.AutoConnect);

                if (shouldReconnect && IsStarted)
                {
                    if (!isTor)
                    {
                        QuicPunchLog.Info($"{tag} Peer {peer.Name ?? hexKey[..8]} {(wasConnecting ? "changed endpoints while connecting" : "is disconnected with auto-connect enabled")}. Initiating connection with verified new endpoints...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await PeerInterrogation(peer, LifecycleToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                QuicPunchLog.Info($"{tag} Interrogation failed for {peer.Name}: {ex.Message}");
                            }
                        });
                    }
                    else if (IsTorStarted && !string.IsNullOrEmpty(peer.OnionAddress))
                    {
                        QuicPunchLog.Info($"{tag} Peer {peer.Name ?? hexKey[..8]} {(wasConnecting ? "changed onion/port while connecting" : "is disconnected with auto-connect enabled")}. Initiating Tor connection with verified new endpoints...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ConnectTorAsync(peer.OnionAddress, peer.MinPort > 0 ? peer.MinPort : 443, LifecycleToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                QuicPunchLog.Info($"{tag} Tor connection failed for {peer.Name}: {ex.Message}");
                            }
                        });
                    }
                }

                RecordDiscoveredPeer(peer, token, isTor ? "Nostr (Tor)" : "Nostr (WAN)");
                QuicPunchLog.Info($"{tag} Discovered peer: {peer.Name} ({Convert.ToHexString(peerHash)[..8]}...){(peer.IsCertVerified ? " [VERIFIED]" : "")} via Nostr {(isTor ? "Tor" : "WAN")}");
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"{tag} Ignored invalid discovery token: {ex.Message}");
            }
        }

        public async Task<bool> RefreshStunEndpointsAsync(bool force = false, CancellationToken cancellationToken = default, long generation = 0, UdpClient? expectedUdp = null)
        {
            CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : LifecycleToken;
            try
            {
                var stunEndpoints = await StunGatherer.GatherStunEndpoints(forceRefresh: force, ct: token).ConfigureAwait(false);
                if (generation != 0 && !IsLifecycleWorkerCurrent(generation, token, expectedUdp))
                    return false;

                if (stunEndpoints.Count > 0)
                {
                    NatCoordinator.SetServerEndpoints(stunEndpoints);
                    QuicPunchLog.Info($"[STUN RECOVERY] Refreshed STUN endpoints. Active server count: {stunEndpoints.Count}");
                    return true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    QuicPunchLog.Info($"[STUN REFRESH] Could not refresh STUN endpoints: {ex.Message}");
            }
            return false;
        }

        public static readonly TimeSpan StunRetentionWindow = TimeSpan.FromSeconds(80);

        public Dictionary<IPEndPoint, int> GetStunHitsSnapshot() => NatCoordinator.GetStunHitsSnapshot();

        private void OnNatMappingChanged(object? sender, NatMappingResult mapping)
        {
            bool endpointsChanged = false;
            var localIps = Utilities.GetValidLocalIPAddresses();
            var allAddresses = (mapping.DiscoveredAddresses ?? Array.Empty<IPAddress>())
                .Concat(localIps)
                .Distinct()
                .OrderBy(Utilities.IpToUint)
                .ToArray();

            if (allAddresses.Length > 0)
            {
                if (CurrentPeer.Addresses == null || !CurrentPeer.Addresses.SequenceEqual(allAddresses))
                {
                    CurrentPeer.Addresses = allAddresses;
                    endpointsChanged = true;
                }
            }

            int newMinPort = Math.Max(1, mapping.MinPort);
            int newMaxPort = Math.Max(newMinPort, mapping.MaxPort);
            if (CurrentPeer.MinPort != newMinPort || CurrentPeer.MaxPort != newMaxPort)
            {
                CurrentPeer.MinPort = newMinPort;
                CurrentPeer.MaxPort = newMaxPort;
                endpointsChanged = true;
            }

            if (CurrentPeer.NetworkType != mapping.NetworkType && mapping.NetworkType != NetworkType.Unknown)
            {
                CurrentPeer.NetworkType = mapping.NetworkType;
                endpointsChanged = true;
            }


            if (endpointsChanged)
            {
                InvalidateTokenCache();
                var newToken = GetToken();
                if (newToken != LastToken)
                {
                    LastToken = newToken;
                    QuicPunchLog.Info($"New token generated: {newToken}");
                    _ = PublishNostrDiscoveryAsync(LifecycleToken);
                }
            }
        }

        private async Task StunRequest(bool resetOnFailure = false, CancellationToken cancellationToken = default, long generation = 0, UdpClient? expectedUdp = null)
        {
            CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : LifecycleToken;
            if (generation != 0 && !IsLifecycleWorkerCurrent(generation, token, expectedUdp))
                return;

            await NatCoordinator.ExecuteBurstAsync(token).ConfigureAwait(false);
        }

        public const int PunchIntervalMiliseconds = 2500 / 2;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PNch");


        public async Task AutoConnectSavedPeersAsync(CancellationToken ct = default)
        {
            if (PeerStore == null) return;

            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }

            var saved = PeerStore.SavedPeers.Where(p => p.AutoConnect).ToList();
            if (saved.Count == 0) return;

            WriteLine($"[AUTO-CONNECT] Initiating background connection for {saved.Count} saved peer(s)...");

            foreach (var sp in saved)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (sp.CertHash != null)
                    {
                        ExpectedPeerCerts.Add(sp.CertHash);
                        TrustPeer(sp.CertHash);
                    }

                    if (!string.IsNullOrEmpty(sp.OnionAddress) && IsTorStarted)
                    {
                        try
                        {
                            WriteLine($"[AUTO-CONNECT] Connecting via Tor to {sp.Name ?? sp.OnionAddress}...");
                            await ConnectTorAsync(sp.OnionAddress, sp.MinPort > 0 ? sp.MinPort : 443, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            WriteLine($"[AUTO-CONNECT TOR] Failed for {sp.Name ?? sp.OnionAddress}: {ex.Message}");
                        }
                    }

                    if (sp.Addresses != null && sp.Addresses.Length > 0 && sp.Addresses.Any(a => !IPAddress.IsLoopback(a)))
                    {
                        var peerInfo = CreatePeerInfoFromSavedPeer(sp);
                        try
                        {
                            WriteLine($"[AUTO-CONNECT] Interrogating WAN peer {sp.Name ?? string.Join(", ", sp.Addresses.Select(a => a.ToString()))}...");
                            await PeerInterrogation(peerInfo, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            WriteLine($"[AUTO-CONNECT WAN] Failed for {sp.Name ?? "Saved Peer"}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteLine($"[AUTO-CONNECT] Error for saved peer: {ex.Message}");
                }
            }
        }

        public static PeerInfo CreatePeerInfoFromSavedPeer(PeerStore.SavedPeer sp)
        {
            var netType = sp.NetworkType;
            if (netType == NetworkType.Unknown || netType == NetworkType.Static)
            {
                if (sp.Addresses.Length > 1 && sp.MinPort != sp.MaxPort)
                    netType = NetworkType.DynamicPortAndAddress;
                else if (sp.Addresses.Length > 1)
                    netType = NetworkType.DynamicAddress;
                else if (sp.MinPort != sp.MaxPort)
                    netType = NetworkType.DynamicPort;
                else
                    netType = NetworkType.Static;
            }

            return new PeerInfo
            {
                Name = sp.Name ?? "Saved Peer",
                NetworkType = netType,
                Addresses = sp.Addresses,
                MinPort = sp.MinPort,
                MaxPort = sp.MaxPort,
                EcdhPublicKey = sp.EcdhPublicKey,
                OnionAddress = sp.OnionAddress
            }.SetCertificateHash(sp.CertHash);
        }

        public bool SavePeer(PeerInfo peer, bool autoConnect = true)
        {
            EnsureStarted();
            ArgumentNullException.ThrowIfNull(peer);
            if (!peer.TryGetCertificateHash(out var certHash) || !IsTrustedPeer(certHash))
                return false;

            return PeerStore != null && PeerStore.AddOrUpdate(peer, autoConnect);
        }

        public bool SavePeer(string token, bool autoConnect = true)
        {
            EnsureStarted();
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            var peer = Utilities.DecodeEndpointToken(token);
            if (peer.CertHash != null)
            {
                ExpectedPeerCerts.Add(peer.CertHash);
                TrustPeer(peer.CertHash);
            }
            return PeerStore != null && PeerStore.AddOrUpdate(peer, autoConnect);
        }

        public bool RemoveSavedPeer(byte[] certHash)
        {
            EnsureStarted();
            ArgumentNullException.ThrowIfNull(certHash);
            return PeerStore != null && PeerStore.Remove(certHash);
        }

        public void TrustPeer(byte[] certHash) => AccessController.TrustPeer(certHash);
        public void UntrustPeer(byte[] certHash) => AccessController.UntrustPeer(certHash);
        public bool IsTrustedPeer(byte[]? certHash) => AccessController.IsTrusted(certHash);
        public bool IsTrustedPeer(PeerInfo? peer) => AccessController.IsTrusted(peer);
        public bool IsTrustedPeer(Guid peerId) => AccessController.IsTrusted(peerId);

        internal void UpdateSavedPeerIfPresent(PeerInfo peer)
        {
            if (PeerStore == null || peer == null) return;
            try
            {
                if (!peer.TryGetCertificateHash(out var hash) || hash == null)
                    return;

                if (PeerStore.TryGet(hash, out var saved) && saved != null)
                {
                    var peerAddresses = peer.Addresses ?? Array.Empty<IPAddress>();
                    bool addrsSame = saved.Addresses != null &&
                        new HashSet<IPAddress>(saved.Addresses).SetEquals(peerAddresses);
                    bool portsSame = saved.MinPort == peer.MinPort && saved.MaxPort == peer.MaxPort;
                    bool nameSame = string.Equals(saved.Name, peer.Name, StringComparison.Ordinal);

                    if (!addrsSame || !portsSame || !nameSame)
                    {
                        string? nostrKey = !string.IsNullOrEmpty(saved.NostrPubKey) ? saved.NostrPubKey : peer.NostrPubKey;
                        PeerStore.AddOrUpdate(peer, autoConnect: saved.AutoConnect, save: true, nostrPubKey: nostrKey);
                    }
                }
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[PEER STORE UPDATE] Warning updating saved peer info: {ex.Message}");
            }
        }

        internal const int MaxDiscoveredPeerCount = 2048;
        internal static readonly TimeSpan DiscoveredPeerTtl = TimeSpan.FromMinutes(10);
        private readonly object _availablePeerAdmissionLock = new();

        public ConcurrentDictionary<Guid, PeerInfo> AvailablePeers { get; } = new();
        public ExpectedPeerCertSet ExpectedPeerCerts => AccessController.ExpectedPeerCerts;
        private readonly ConcurrentDictionary<string, Guid> _peersByCertHash = new();
        private readonly ConcurrentDictionary<uint, PeerInfo> _peersByShortId = new();

        internal bool TryGetPeerByCertHash(byte[] certHash, out PeerInfo? peer)
        {
            peer = null;
            if (certHash == null || certHash.Length == 0) return false;
            string key = Base64Url.EncodeToString(certHash);
            if (_peersByCertHash.TryGetValue(key, out var peerId) && AvailablePeers.TryGetValue(peerId, out peer))
            {
                return true;
            }
            peer = AvailablePeers.Values.FirstOrDefault(p => p.TryGetCertificateHash(out var hash) && CryptographicOperations.FixedTimeEquals(hash, certHash));
            if (peer != null)
            {
                _peersByCertHash[key] = peer.Id;
                return true;
            }
            return false;
        }

        internal void IndexPeerCertHash(PeerInfo peer)
        {
            if (peer.TryGetCertificateHash(out var hash))
            {
                string key = Base64Url.EncodeToString(hash);
                _peersByCertHash[key] = peer.Id;
            }
            if (peer.ShortId != 0)
            {
                _peersByShortId[peer.ShortId] = peer;
            }
        }

        internal bool TryAddAvailablePeer(Guid peerId, PeerInfo peer, int maxDiscoveredPeers = MaxDiscoveredPeerCount)
        {
            lock (_availablePeerAdmissionLock)
            {
                if (AvailablePeers.ContainsKey(peerId))
                    return false;

                if (!IsPeerProtectedFromDiscoveryPrune(peerId, peer))
                {
                    int limit = Math.Max(1, maxDiscoveredPeers);
                    PruneDiscoveredPeersCore(DiscoveredPeerTtl, limit - 1);
                    if (AvailablePeers.Count(kvp => !IsPeerProtectedFromDiscoveryPrune(kvp.Key, kvp.Value)) >= limit)
                        return false;
                }

                bool added = AvailablePeers.TryAdd(peerId, peer);
                if (added)
                {
                    IndexPeerCertHash(peer);
                    if (peer.ShortId != 0)
                    {
                        _peersByShortId[peer.ShortId] = peer;
                    }
                }
                return added;
            }
        }

        internal int PruneDiscoveredPeers(TimeSpan? ttl = null, int maxDiscoveredPeers = MaxDiscoveredPeerCount)
        {
            lock (_availablePeerAdmissionLock)
            {
                return PruneDiscoveredPeersCore(ttl ?? DiscoveredPeerTtl, Math.Max(0, maxDiscoveredPeers));
            }
        }

        private int PruneDiscoveredPeersCore(TimeSpan ttl, int maxDiscoveredPeers)
        {
            var removable = AvailablePeers
                .Where(kvp => !IsPeerProtectedFromDiscoveryPrune(kvp.Key, kvp.Value))
                .OrderBy(kvp => kvp.Value.LastActivityTimestampMonotonic)
                .ToList();

            int removed = 0;
            foreach (var kvp in removable.ToArray())
            {
                if (Stopwatch.GetElapsedTime(kvp.Value.LastActivityTimestampMonotonic) <= ttl)
                    continue;
                if (RemoveDiscoveredPeer(kvp.Key, kvp.Value))
                {
                    removable.Remove(kvp);
                    removed++;
                }
            }

            while (removable.Count > maxDiscoveredPeers)
            {
                var oldest = removable[0];
                removable.RemoveAt(0);
                if (RemoveDiscoveredPeer(oldest.Key, oldest.Value))
                    removed++;
            }

            return removed;
        }

        private bool IsPeerProtectedFromDiscoveryPrune(Guid peerId, PeerInfo peer)
        {
            if (IsTrustedPeer(peer))
                return true;
            if (peer.TryGetCertificateHash(out var hash) && PeerStore != null && PeerStore.TryGet(hash, out _))
                return true;
            if (_activeProtocolSessions.Keys.Any(key => key.PeerId == peerId))
                return true;
            if (_activeOutboundNegotiations.Keys.Any(key => key.PeerId == peerId))
                return true;
            if (_activeConnectionFlights.Keys.Any(key => key.PeerId == peerId))
                return true;
            if (ActiveInterrogations.Values.Any(session => PeerTargetsMatch(session.Peer, peer)))
                return true;
            return IncomingHandshakeSessions.Values.Any(session => session.PeerId == peerId && session.State == HandshakeSessionState.Pending);
        }

        private bool RemoveDiscoveredPeer(Guid peerId, PeerInfo expectedPeer)
        {
            var pair = new KeyValuePair<Guid, PeerInfo>(peerId, expectedPeer);
            if (!((ICollection<KeyValuePair<Guid, PeerInfo>>)AvailablePeers).Remove(pair))
                return false;

            if (expectedPeer.TryGetCertificateHash(out var hash))
            {
                _peersByCertHash.TryRemove(Base64Url.EncodeToString(hash), out _);
            }
            if (expectedPeer.ShortId != 0)
            {
                _peersByShortId.TryRemove(expectedPeer.ShortId, out _);
            }

            try { expectedPeer.Dispose(); } catch { }
            RaisePeerDisconnected(expectedPeer);
            return true;
        }
        public event Action<PeerInfo>? OnPeerDisconnected;
        internal void RaisePeerDisconnected(PeerInfo peer) => OnPeerDisconnected?.Invoke(peer);

        public static QuicPunchBuilder CreateBuilder() => new();

        internal byte[] BuildDisconnectPacket(TransportType transport = TransportType.Wan) =>
            PacketBuilder.BuildDisconnectPacket(this, transport);

        public void DisconnectPeer(Guid peerId)
        {
            EnsureStarted();
            if (AvailablePeers.TryRemove(peerId, out var peer))
            {
                if (peer.TryGetCertificateHash(out var hash))
                {
                    _peersByCertHash.TryRemove(Base64Url.EncodeToString(hash), out _);
                }
                if (peer.ShortId != 0)
                {
                    _peersByShortId.TryRemove(peer.ShortId, out _);
                }

                if (peer.ActiveEndPoint != null || peer.TorChannel != null)
                {
                    byte[] packet = BuildDisconnectPacket(peer.ActiveTransport);
                    _ = SendToPeerAsync(peer, packet, peer.ActiveTransport);
                }

                CancelMatchingInterrogations(peer);

                _ = CloseAllPeerSessionsAsync(peerId);
                peer.Dispose();

                // Existing peers own independent entropy clones, so rotating the
                // seed for future sessions cannot desynchronize unrelated peers.
                GetCertManager(peer.ActiveTransport).RenewSessionEntropy();

                WriteLine($"Disconnected peer {peer.Name} ({peerId})");
                OnPeerDisconnected?.Invoke(peer);
            }
        }

        public bool RemovePeer(Guid peerId)
        {
            EnsureStarted();
            if (AvailablePeers.TryRemove(peerId, out var peer))
            {
                if (peer.TryGetCertificateHash(out var hash))
                {
                    _peersByCertHash.TryRemove(Base64Url.EncodeToString(hash), out _);
                }
                if (peer.ShortId != 0)
                {
                    _peersByShortId.TryRemove(peer.ShortId, out _);
                }

                CancelMatchingInterrogations(peer);

                _ = CloseAllPeerSessionsAsync(peerId);
                peer.Dispose();

                // Existing peers own independent entropy clones, so rotating the
                // seed for future sessions cannot desynchronize unrelated peers.
                GetCertManager(peer.ActiveTransport).RenewSessionEntropy();

                WriteLine($"Removed peer {peer.Name} ({peerId})");
                RaisePeerDisconnected(peer);
                return true;
            }
            return false;
        }

        private readonly ConcurrentDictionary<ushort, Channel<(Guid Peer, byte[] Payload)>> _packetChannels = new();
        public ChannelReader<(Guid Peer, byte[] Payload)> GetPacketReader(ushort packetType)
        {
            return _packetChannels.GetOrAdd(packetType, _ =>
                Channel.CreateBounded<(Guid, byte[])>(new BoundedChannelOptions(2048)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                })).Reader;
        }

        internal void PublishReceivedData(Guid peerId, ushort packetType, byte[] payload)
        {
            var channel = _packetChannels.GetOrAdd(packetType, _ =>
                Channel.CreateBounded<(Guid, byte[])>(new BoundedChannelOptions(2048)
                {
                    SingleReader = false,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                }));
            channel.Writer.TryWrite((peerId, payload));
        }

        public interface IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public string ProtocolName { get; }
            public ushort StreamPriority => (ushort)Helpers.QuicStreamPriority.Normal;
            public ZstandardCompressionOptions? CompressionOptions { get; }
            Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct);
            Task DeniedAsync(PeerInfo peer, CancellationToken ct);
        }
        public bool RemoveProtocol(IProtocolHandler handler) => ProtocolHandlers.TryRemove(handler.ProtocolId, out _);
        public void RegisterProtocol(IProtocolHandler handler) => ProtocolHandlers[handler.ProtocolId] = handler;

        public event Action<PeerInfo>? OnPeerAvailable;
        internal void RaisePeerAvailable(PeerInfo peerInfo)
        {
            OnPeerAvailable?.Invoke(peerInfo);
        }

        private readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), (QuicConnection Connection, Stream Stream)> _activeProtocolSessions = new();

        public async Task<bool> RegisterProtocolSessionAsync(Guid peerId, Guid protocolId, QuicConnection connection, Stream stream, long generation = 0)
        {
            if (!AutoAcceptUntrustedConnections &&
                (!AvailablePeers.TryGetValue(peerId, out var applicationPeer) || !IsTrustedPeer(applicationPeer)))
            {
                QuicPunchLog.Info($"[SESSION REGISTRATION REJECTED] Peer {peerId} is not trusted for application protocols.");
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
                return false;
            }

            if (LifecycleState != QuicPunchLifecycleState.Started ||
                (generation != 0 && generation != _lifecycleGeneration) ||
                (CancellationSource?.IsCancellationRequested ?? true))
            {
                QuicPunchLog.Info($"[SESSION REGISTRATION REJECTED] Cannot register session for peer {peerId} protocol {protocolId}: LifecycleState={LifecycleState}, generation mismatch (worker: {generation}, current: {_lifecycleGeneration}). Disposing connection.");
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
                return false;
            }

            var key = (peerId, protocolId);
            (QuicConnection Connection, Stream Stream) oldSession = default;
            bool hasOld = false;

            lock (_activeProtocolSessions)
            {
                if (LifecycleState != QuicPunchLifecycleState.Started ||
                    (generation != 0 && generation != _lifecycleGeneration) ||
                    (CancellationSource?.IsCancellationRequested ?? true))
                {
                    // Rejected inside lock
                }
                else
                {
                    if (_activeProtocolSessions.TryRemove(key, out oldSession))
                    {
                        hasOld = true;
                    }
                    _activeProtocolSessions[key] = (connection, stream);
                    goto Proceed;
                }
            }

            QuicPunchLog.Info($"[SESSION REGISTRATION REJECTED] Cannot register session for peer {peerId} protocol {protocolId}: instance stopped during registration. Disposing connection.");
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
            return false;

        Proceed:
            if (ProtocolHandlers.TryGetValue(protocolId, out var protoHandler))
            {
                Helpers.MsQuicTuner.TrySetStreamPriority(stream, protoHandler.StreamPriority);
            }

            if (hasOld)
            {
                try { await oldSession.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await oldSession.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            return true;
        }

        public async Task UnregisterProtocolSessionAsync(Guid peerId, Guid protocolId, QuicConnection? connection = null)
        {
            var key = (peerId, protocolId);
            if (_activeProtocolSessions.TryGetValue(key, out var session))
            {
                if (connection == null || ReferenceEquals(session.Connection, connection))
                {
                    var kvp = new KeyValuePair<(Guid PeerId, Guid ProtocolId), (QuicConnection Connection, Stream Stream)>(key, session);
                    if (((ICollection<KeyValuePair<(Guid PeerId, Guid ProtocolId), (QuicConnection Connection, Stream Stream)>>)_activeProtocolSessions).Remove(kvp))
                    {
                        try { await session.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                        try { await session.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
            }
        }

        public async Task CloseAllPeerSessionsAsync(Guid peerId)
        {
            var keys = _activeProtocolSessions.Keys.Where(k => k.PeerId == peerId).ToList();
            foreach (var key in keys)
            {
                if (_activeProtocolSessions.TryRemove(key, out var session))
                {
                    try { await session.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                    try { await session.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }
        }

        public sealed class OutboundNegotiation
        {
            public Guid ConnectionGuid { get; }
            public CancellationTokenSource Cts { get; }
            public volatile bool IsYielded;
            public TaskCompletionSource<(HandshakeDecision decision, Guid connectionGuid)> DecisionTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public OutboundNegotiation(Guid connectionGuid, CancellationTokenSource cts)
            {
                ConnectionGuid = connectionGuid;
                Cts = cts;
            }
        }

        internal sealed class ConnectionFlight
        {
            public Guid AttemptId { get; } = Guid.NewGuid();
            public Task Task { get; }

            public ConnectionFlight(Task task)
            {
                Task = task;
            }
        }

        private readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), OutboundNegotiation> _activeOutboundNegotiations = new();
        private readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), ConnectionFlight> _activeConnectionFlights = new();

        public bool IsConnectionInFlight(Guid peerId, Guid protocolId) =>
            _activeConnectionFlights.ContainsKey((peerId, protocolId));

        public bool HasActiveProtocolSession(Guid peerId, Guid protocolId) =>
            _activeProtocolSessions.ContainsKey((peerId, protocolId));

        public int ActiveOutboundNegotiationsCount => _activeOutboundNegotiations.Count;
        public int ActiveOutboundNegotiationCount => _activeOutboundNegotiations.Count;
        public int ActiveConnectionFlightsCount => _activeConnectionFlights.Count;
        public int ActiveProtocolSessionCount => _activeProtocolSessions.Count;
        public int PendingQuicReadyCount => _pendingQuicReady.Count;
        public int ReceivedQuicReadyCacheCount => _receivedQuicReady.Count;
        public int ActiveIncomingWorkerCount => _activeIncomingWorkers.Count;

        /// <summary>
        /// Queries and aggregates point-in-time performance telemetry and protocol metadata for all active QUIC protocol sessions.
        /// Also updates each associated <see cref="PeerInfo.LastTelemetry"/> reference with fresh statistics.
        /// </summary>
        /// <returns>A read-only collection of <see cref="QuicSessionTelemetryInfo"/> representing active sessions.</returns>
        public IReadOnlyList<QuicSessionTelemetryInfo> GetActiveSessionsTelemetry()
        {
            var results = new List<QuicSessionTelemetryInfo>();
            foreach (var kvp in _activeProtocolSessions)
            {
                var (peerId, protocolId) = kvp.Key;
                var (connection, _) = kvp.Value;
                if (connection == null) continue;

                if (connection.TryGetTelemetry(out var telemetry))
                {
                    AvailablePeers.TryGetValue(peerId, out var peer);
                    if (peer != null)
                    {
                        peer.LastTelemetry = telemetry;
                    }

                    string? protocolName = null;
                    if (ProtocolHandlers.TryGetValue(protocolId, out var handler))
                    {
                        protocolName = handler.ProtocolName;
                    }

                    results.Add(new QuicSessionTelemetryInfo
                    {
                        PeerId = peerId,
                        PeerName = peer?.Name ?? "Peer",
                        ProtocolId = protocolId,
                        ProtocolName = protocolName ?? protocolId.ToString(),
                        TransportType = connection.TransportType,
                        Telemetry = telemetry,
                        SampleTimeUtc = DateTime.UtcNow
                    });
                }
            }
            return results;
        }

        /// <summary>
        /// Queries the latest real-time telemetry from an active connection associated with the specified peer,
        /// or returns the last known cached telemetry if the connection has completed.
        /// </summary>
        /// <param name="peerId">The unique identifier of the peer.</param>
        /// <param name="telemetry">When this method returns, contains the queried or cached <see cref="QuicConnectionTelemetry"/>, or <c>null</c> if unavailable.</param>
        /// <returns><c>true</c> if telemetry is available; otherwise, <c>false</c>.</returns>
        public bool TryGetPeerTelemetry(Guid peerId, out QuicConnectionTelemetry? telemetry)
        {
            telemetry = null;
            var sessionKvp = _activeProtocolSessions.FirstOrDefault(k => k.Key.PeerId == peerId);
            if (sessionKvp.Value.Connection != null && sessionKvp.Value.Connection.TryGetTelemetry(out var liveTelemetry))
            {
                telemetry = liveTelemetry;
                if (AvailablePeers.TryGetValue(peerId, out var peer))
                {
                    peer.LastTelemetry = liveTelemetry;
                }
                return true;
            }

            if (AvailablePeers.TryGetValue(peerId, out var cachedPeer) && cachedPeer.LastTelemetry != null)
            {
                telemetry = cachedPeer.LastTelemetry;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Dynamically modifies the congestion control algorithm (such as CUBIC or BBR) on an active protocol session.
        /// </summary>
        /// <param name="peerId">The unique identifier of the remote peer.</param>
        /// <param name="protocolId">The protocol identifier of the session.</param>
        /// <param name="algorithm">The target <see cref="QuicCongestionAlgorithm"/>.</param>
        /// <returns><c>true</c> if the congestion control algorithm was successfully updated; otherwise, <c>false</c>.</returns>
        public bool TrySetSessionCongestionControl(Guid peerId, Guid protocolId, QuicCongestionAlgorithm algorithm)
        {
            if (_activeProtocolSessions.TryGetValue((peerId, protocolId), out var session) && session.Connection != null)
            {
                return session.Connection.TrySetCongestionControl(algorithm);
            }
            return false;
        }

        /// <summary>
        /// Dynamically modifies the congestion control algorithm across all active protocol sessions with a remote peer.
        /// </summary>
        /// <param name="peerId">The unique identifier of the remote peer.</param>
        /// <param name="algorithm">The target <see cref="QuicCongestionAlgorithm"/>.</param>
        /// <returns><c>true</c> if at least one active connection was updated; otherwise, <c>false</c>.</returns>
        public bool TrySetPeerCongestionControl(Guid peerId, QuicCongestionAlgorithm algorithm)
        {
            bool anySuccess = false;
            foreach (var kvp in _activeProtocolSessions.Where(k => k.Key.PeerId == peerId))
            {
                if (kvp.Value.Connection != null && kvp.Value.Connection.TrySetCongestionControl(algorithm))
                {
                    anySuccess = true;
                }
            }
            return anySuccess;
        }

        internal readonly ConcurrentDictionary<Guid, IncomingHandshakeWorker> _activeIncomingWorkers = new();

        internal bool TryRegisterIncomingWorker(IncomingHandshakeWorker worker)
        {
            if (LifecycleState != QuicPunchLifecycleState.Started || CancellationSource == null || CancellationSource.IsCancellationRequested)
                return false;

            return _activeIncomingWorkers.TryAdd(worker.ConnectionGuid, worker);
        }

        internal void UnregisterIncomingWorker(Guid connectionGuid)
        {
            _activeIncomingWorkers.TryRemove(connectionGuid, out _);
        }
        internal readonly ConcurrentDictionary<Guid, long> LastSeenDisconnectTicks = new();
        public int MaxIncomingHandshakeSessions { get; set; } = 1000;
        public TimeSpan HandshakePendingTtl { get; set; } = TimeSpan.FromSeconds(45);
        public TimeSpan HandshakeCompletedTtl { get; set; } = TimeSpan.FromSeconds(90);
        public TimeSpan HandshakeRejectedTtl { get; set; } = TimeSpan.FromSeconds(30);

        internal readonly ConcurrentDictionary<Guid, IncomingHandshakeSession> IncomingHandshakeSessions = new();
        private readonly object _incomingHandshakeSessionsLock = new();

        internal IncomingHandshakeSession? GetOrAddIncomingHandshakeSession(Guid guid, Guid peerId, Guid protocolId)
        {
            lock (_incomingHandshakeSessionsLock)
            {
                if (IncomingHandshakeSessions.TryGetValue(guid, out var existing))
                    return existing;

                int capacity = Math.Max(1, MaxIncomingHandshakeSessions);
                if (IncomingHandshakeSessions.Count >= capacity)
                    PruneExpiredIncomingHandshakeSessions();
                while (IncomingHandshakeSessions.Count >= capacity)
                    EvictOldestIncomingHandshakeSession();

                var newSession = new IncomingHandshakeSession(guid, peerId, protocolId);
                return IncomingHandshakeSessions.TryAdd(guid, newSession) ? newSession : IncomingHandshakeSessions[guid];
            }
        }

        private void EvictOldestIncomingHandshakeSession()
        {
            var candidate = IncomingHandshakeSessions
                .OrderBy(kvp => kvp.Value.State == HandshakeSessionState.Pending ? 1 : 0)
                .ThenBy(kvp => kvp.Value.CreatedTimestampMonotonic)
                .FirstOrDefault();

            if (candidate.Value != null)
                IncomingHandshakeSessions.TryRemove(candidate.Key, out _);
        }

        public int PruneExpiredIncomingHandshakeSessions()
        {
            return PruneExpiredIncomingHandshakeSessions(HandshakePendingTtl, HandshakeCompletedTtl, HandshakeRejectedTtl);
        }

        public int PruneExpiredIncomingHandshakeSessions(TimeSpan pendingTtl, TimeSpan completedTtl, TimeSpan rejectedTtl)
        {
            lock (_incomingHandshakeSessionsLock)
            {
                int pruned = 0;
                foreach (var kvp in IncomingHandshakeSessions)
                {
                    if (kvp.Value.IsExpired(pendingTtl, completedTtl, rejectedTtl) && IncomingHandshakeSessions.TryRemove(kvp.Key, out _))
                        pruned++;
                }
                return pruned;
            }
        }

        public void PruneExpiredIncomingHandshakeSessions(TimeSpan maxAge)
        {
            PruneExpiredIncomingHandshakeSessions(maxAge, maxAge, maxAge);
        }

        private async Task MaintenanceLoopAsync(long generation, CancellationToken token)
        {
            while (IsLifecycleWorkerCurrent(generation, token))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                    PruneExpiredIncomingHandshakeSessions();
                    PruneExpiredChallenges(TimeSpan.FromSeconds(60));
                    PruneDiscoveredPeers();
                    PruneReceivedQuicReadyCache();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    QuicPunchLog.Error("[QuicPunch] Error in maintenance loop", ex);
                }
            }
        }

        internal sealed class PendingChallenge : IDisposable
        {
            public byte[] Nonce { get; }
            public long CreatedTimestampMonotonic { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
            public IPEndPoint? TargetEndPoint { get; }
            public byte[]? ExpectedCertHash { get; }
            private PeerInfo? _entropySnapshot;

            public PendingChallenge(
                byte[] nonce,
                IPEndPoint? targetEndPoint = null,
                byte[]? localSessionNonce = null,
                ECDiffieHellman? localEphemeralEcdh = null,
                byte[]? expectedCertHash = null)
            {
                Nonce = nonce;
                TargetEndPoint = targetEndPoint;
                ExpectedCertHash = expectedCertHash == null ? null : (byte[])expectedCertHash.Clone();

                if (localSessionNonce is { Length: 32 } && localEphemeralEcdh != null)
                {
                    _entropySnapshot = new PeerInfo();
                    _entropySnapshot.SetLocalEntropy(localSessionNonce, localEphemeralEcdh);
                }
            }

            public bool IsExpired(TimeSpan timeout)
            {
                return System.Diagnostics.Stopwatch.GetElapsedTime(CreatedTimestampMonotonic) > timeout;
            }

            public void ApplyLocalEntropy(PeerInfo peer)
            {
                _entropySnapshot?.CopyLocalEntropyTo(peer);
            }

            public void Dispose()
            {
                try { _entropySnapshot?.Dispose(); } catch { }
                _entropySnapshot = null;
            }
        }

        internal readonly ConcurrentDictionary<string, PendingChallenge> _pendingChallenges = new();

        public byte[] CreatePendingChallenge(
            IPEndPoint? target = null,
            byte[]? localSessionNonce = null,
            ECDiffieHellman? localEphemeralEcdh = null,
            byte[]? expectedCertHash = null)
        {
            var nonce = RandomNumberGenerator.GetBytes(24);
            var key = Convert.ToBase64String(nonce);
            _pendingChallenges[key] = new PendingChallenge(nonce, target, localSessionNonce, localEphemeralEcdh, expectedCertHash);
            return nonce;
        }

        internal PendingChallenge? ConsumePendingChallenge(byte[] nonce)
        {
            if (nonce == null || nonce.Length != 24)
                return null;

            var key = Convert.ToBase64String(nonce);
            if (!_pendingChallenges.TryRemove(key, out var challenge))
                return null;

            if (challenge.IsExpired(TimeSpan.FromSeconds(30)))
            {
                challenge.Dispose();
                return null;
            }

            return challenge;
        }

        public bool ValidateAndConsumeChallenge(byte[] nonce)
        {
            using var challenge = ConsumePendingChallenge(nonce);
            return challenge != null;
        }

        public void PruneExpiredChallenges(TimeSpan maxAge)
        {
            foreach (var kvp in _pendingChallenges)
            {
                if (kvp.Value.IsExpired(maxAge) && _pendingChallenges.TryRemove(kvp.Key, out var removed))
                {
                    removed.Dispose();
                }
            }
        }

        internal bool TryGetActiveOutboundNegotiation(Guid peerId, Guid protocolId, out OutboundNegotiation? negotiation) =>
            _activeOutboundNegotiations.TryGetValue((peerId, protocolId), out negotiation);

        private readonly ConcurrentDictionary<Guid, (Guid PeerId, TaskCompletionSource<ushort> Tcs)> _pendingQuicReady = new();
        private readonly ConcurrentDictionary<Guid, (ushort Port, long Generation, long ReceivedAt, Guid PeerId)> _receivedQuicReady = new();
        private readonly object _quicReadyCacheLock = new();
        private const int MaxReceivedQuicReadyCache = 128;
        private static readonly TimeSpan ReceivedQuicReadyTtl = TimeSpan.FromSeconds(15);

        private bool TryTakeCachedQuicReady(Guid connectionGuid, Guid expectedPeerId, out ushort port)
        {
            port = 0;
            lock (_quicReadyCacheLock)
            {
                if (!_receivedQuicReady.TryRemove(connectionGuid, out var cached))
                    return false;

                if (cached.Generation != _lifecycleGeneration || cached.PeerId != expectedPeerId ||
                    Stopwatch.GetElapsedTime(cached.ReceivedAt) > ReceivedQuicReadyTtl)
                    return false;

                port = cached.Port;
                return true;
            }
        }

        private void CacheQuicReady(Guid connectionGuid, Guid peerId, ushort port, long generation)
        {
            lock (_quicReadyCacheLock)
            {
                foreach (var item in _receivedQuicReady.ToArray())
                {
                    if (item.Value.Generation != _lifecycleGeneration ||
                        Stopwatch.GetElapsedTime(item.Value.ReceivedAt) > ReceivedQuicReadyTtl)
                        _receivedQuicReady.TryRemove(item.Key, out _);
                }

                while (_receivedQuicReady.Count >= MaxReceivedQuicReadyCache)
                {
                    var oldest = _receivedQuicReady.OrderBy(kvp => kvp.Value.ReceivedAt).FirstOrDefault();
                    if (oldest.Key == Guid.Empty || !_receivedQuicReady.TryRemove(oldest.Key, out _))
                        break;
                }

                _receivedQuicReady[connectionGuid] = (port, generation, Stopwatch.GetTimestamp(), peerId);
            }
        }

        private void PruneReceivedQuicReadyCache()
        {
            lock (_quicReadyCacheLock)
            {
                foreach (var item in _receivedQuicReady.ToArray())
                {
                    if (item.Value.Generation != _lifecycleGeneration ||
                        Stopwatch.GetElapsedTime(item.Value.ReceivedAt) > ReceivedQuicReadyTtl)
                        _receivedQuicReady.TryRemove(item.Key, out _);
                }
            }
        }

        public async Task<ushort> WaitForQuicReadyAsync(Guid connectionGuid, Guid expectedPeerId, TimeSpan timeout, CancellationToken ct)
        {
            if (TryTakeCachedQuicReady(connectionGuid, expectedPeerId, out var cachedPort))
                return cachedPort;

            var pending = _pendingQuicReady.GetOrAdd(connectionGuid, _ =>
                (expectedPeerId, new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously)));

            if (pending.PeerId != expectedPeerId)
                throw new InvalidOperationException("QUIC_READY connection GUID is already bound to a different peer.");

            if (TryTakeCachedQuicReady(connectionGuid, expectedPeerId, out cachedPort))
            {
                _pendingQuicReady.TryRemove(connectionGuid, out _);
                return cachedPort;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            using (timeoutCts.Token.Register(() => pending.Tcs.TrySetCanceled(timeoutCts.Token)))
            {
                try
                {
                    return await pending.Tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    _pendingQuicReady.TryRemove(connectionGuid, out _);
                }
            }
        }

        public void UnregisterPendingQuicReady(Guid connectionGuid)
        {
            _pendingQuicReady.TryRemove(connectionGuid, out _);
            _receivedQuicReady.TryRemove(connectionGuid, out _);
        }

        public async Task SendQuicReadyAsync(PeerInfo peer, Guid connectionGuid, ushort listeningPort, TransportType transport = TransportType.Wan)
        {
            EnsureApplicationPeerAllowed(peer);
            byte[] packet = PacketBuilder.BuildQuicReadyPacket(this, connectionGuid, listeningPort, transport);
            if (peer.ActiveEndPoint != null)
            {
                await SendResponseAsync(packet, peer.ActiveEndPoint, transport, peer.TorChannel).ConfigureAwait(false);
            }
            if (peer.Addresses != null)
            {
                foreach (var addr in peer.Addresses)
                {
                    if (Utilities.IsValidPeerAddress(addr))
                    {
                        if (peer.MinPort > 0)
                            _ = SendResponseAsync(packet, new IPEndPoint(addr, peer.MinPort), transport, peer.TorChannel);
                        if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Port != peer.MinPort)
                            _ = SendResponseAsync(packet, new IPEndPoint(addr, peer.ActiveEndPoint.Port), transport, peer.TorChannel);
                    }
                }
            }

            if (transport == TransportType.Wan)
            {
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        await Task.Delay(25).ConfigureAwait(false);
                        if (peer.ActiveEndPoint != null)
                        {
                            await SendResponseAsync(packet, peer.ActiveEndPoint, transport, peer.TorChannel).ConfigureAwait(false);
                        }
                    }
                });
            }
        }

        private void HandleQuicReady(BinaryReader r, EndPoint remoteEndPoint, byte[] buffer, TransportType transport, TorQuicConnectionManager? torChannel)
        {
            const int FixedPayloadBytes = 16 + 16 + 2 + 8;
            if (r.BaseStream.Length - r.BaseStream.Position < FixedPayloadBytes + CertManager.SignatureLength)
                return;

            byte[] peerIdBytes = r.ReadBytes(16);
            byte[] connectionGuidBytes = r.ReadBytes(16);
            if (peerIdBytes.Length != 16 || connectionGuidBytes.Length != 16)
                return;

            var peerId = new Guid(peerIdBytes);
            var connectionGuid = new Guid(connectionGuidBytes);
            if (connectionGuid == Guid.Empty)
                return;

            var listeningPort = r.ReadUInt16();
            if (listeningPort == 0)
                return;
            var timestamp = r.ReadInt64();

            var signature = r.ReadBytes(CertManager.SignatureLength);
            if (signature.Length != CertManager.SignatureLength)
                return;

            if (!AvailablePeers.TryGetValue(peerId, out var peer) || peer == null)
                return;

            if (!peer.Curve.VerifyData(buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature, HashAlgorithmName.SHA3_256))
            {
                QuicPunchLog.Info($"[QUIC READY] Received invalid signature from {remoteEndPoint}");
                return;
            }

            if (LifecycleState != QuicPunchLifecycleState.Started)
                return;

            if (!AutoAcceptUntrustedConnections && !IsTrustedPeer(peer))
            {
                QuicPunchLog.Info($"[QUIC READY] Ignored QUIC_READY from untrusted peer {peer.Id}");
                return;
            }

            long nowTicks = PreciseTime.GetCorrectTime().Ticks;
            long freshnessWindow = TimeSpan.FromMinutes(1).Ticks;
            if (timestamp < nowTicks - freshnessWindow || timestamp > nowTicks + freshnessWindow)
            {
                QuicPunchLog.Info($"[QUIC READY] Ignored stale/future QUIC_READY from peer {peer.Id}");
                return;
            }

            long currentGen = _lifecycleGeneration;
            QuicPunchLog.Info($"[QUIC READY] Received QUIC_READY from peer {peer.Name} (Guid: {connectionGuid}, Port: {listeningPort})");

            if (_pendingQuicReady.TryGetValue(connectionGuid, out var pending))
            {
                if (pending.PeerId != peerId)
                    return;

                if (_pendingQuicReady.TryRemove(connectionGuid, out pending))
                    pending.Tcs.TrySetResult(listeningPort);
                return;
            }

            // A QUIC_READY can legitimately arrive just before the client creates
            // its waiter. Keep only a tiny, short-lived, peer-bound early cache.
            CacheQuicReady(connectionGuid, peerId, listeningPort, currentGen);
        }

        private async Task<(HandshakeDecision decision, Guid connectionGuid)> NegotiateConnection(
            Guid protocolHandler, PeerInfo peer, ushort localPort, IReadOnlyList<CandidateEndpoint>? candidates = null, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            var connectionGuid = Guid.NewGuid();
            var transport = peer.ActiveTransport;
            var payload = GenerateHandshakePayload(HandShakeType.Request, localPort, protocolHandler, connectionGuid, candidates, transport);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken);
            var negotiationKey = (peer.Id, protocolHandler);
            var outbound = new OutboundNegotiation(connectionGuid, linkedCts);

            while (!_activeOutboundNegotiations.TryAdd(negotiationKey, outbound))
            {
                if (_activeOutboundNegotiations.TryGetValue(negotiationKey, out var existingNegotiation))
                {
                    QuicPunchLog.Info($"[Handshake Negotiation] Outbound negotiation for {peer.Name} ({negotiationKey}) already in flight. Awaiting existing negotiation.");
                    return await existingNegotiation.DecisionTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                var epForRequest = peer.ActiveEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                var decisionTask = _manager.WaitForDecisionAsync(new HandshakeRequest(connectionGuid, protocolHandler, epForRequest), TimeSpan.FromSeconds(25), false, linkedCts.Token);

                while (!decisionTask.IsCompleted && !linkedCts.Token.IsCancellationRequested)
                {
                    if (transport == TransportType.Tor && peer.TorChannel != null)
                    {
                        _ = peer.TorChannel.SendMessageAsync(payload, linkedCts.Token);
                    }
                    else if (udp != null)
                    {
                        _ = udp.BigSendAsync(payload, peer);
                    }

                    await Task.WhenAny(decisionTask, Task.Delay(500, linkedCts.Token)).ConfigureAwait(false);
                }

                var decision = await decisionTask.ConfigureAwait(false);

                if (!decision.Accepted)
                {
                    if (outbound.IsYielded)
                    {
                        WriteLine("[Handshake Glare] Outbound negotiation yielded cleanly to remote authoritative request.");
                        var yieldedResult = (new HandshakeDecision(false, null, null), connectionGuid);
                        outbound.DecisionTcs.TrySetResult(yieldedResult);
                        return yieldedResult;
                    }
                    var declinedEx = new Exception("Handshake declined by peer.");
                    outbound.DecisionTcs.TrySetException(declinedEx);
                    throw declinedEx;
                }

                WriteLine("Peer accepted :D");
                var acceptResult = (decision, connectionGuid);
                outbound.DecisionTcs.TrySetResult(acceptResult);
                return acceptResult;
            }
            catch (Exception ex)
            {
                outbound.DecisionTcs.TrySetException(ex);
                throw;
            }
            finally
            {
                _activeOutboundNegotiations.TryRemove(new KeyValuePair<(Guid PeerId, Guid ProtocolId), OutboundNegotiation>(negotiationKey, outbound));
            }
        }
        internal async Task<(UdpClient Socket, ushort BoundPort, List<CandidateEndpoint> Candidates)> CreateBoundSocketAndGatherCandidatesAsync(
            ushort localPort, string? logLabel = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nudp = new UdpClient();
            try
            {
                ConfigureUdpSocket(nudp);
                nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try
                {
                    nudp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
                }
                catch
                {
                    nudp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                }
                ushort boundPort = (ushort)((IPEndPoint)nudp.Client.LocalEndPoint!).Port;

                cancellationToken.ThrowIfCancellationRequested();
                var candidates = await SimpleStunClient.GatherCandidatesAsync(nudp, boundPort, StunServerEndpoints, TimeSpan.FromMilliseconds(1000), cancellationToken).ConfigureAwait(false);

                if (CurrentPeer?.Addresses != null)
                {
                    foreach (var addr in CurrentPeer.Addresses)
                    {
                        if (!SimpleStunClient.IsBogonOrLocalhost(addr))
                        {
                            var ep = new IPEndPoint(addr, boundPort);
                            if (!candidates.Any(c => c.EndPoint.Equals(ep)))
                            {
                                candidates.Add(new CandidateEndpoint(ep, CandidateType.ServerReflexive, 1694498800));
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(logLabel))
                {
                    QuicPunchLog.Info($"{logLabel} (BoundPort: {boundPort}): {candidates.Count} candidate(s) [{string.Join(", ", candidates.Select(c => $"{c.Type}:{c.EndPoint}"))}]");
                }
                return (nudp, boundPort, candidates);
            }
            catch
            {
                nudp.Dispose();
                throw;
            }
        }

        private void EnsureApplicationPeerAllowed(PeerInfo peer) => AccessController.EnsureAllowedForApplication(peer);

        public async Task<(bool Success, UdpClient? Client, IPEndPoint? remoteEndpoint)> InitUdpConnection(Guid protocolHandler, PeerInfo peer, ushort localPort = 0, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            EnsureApplicationPeerAllowed(peer);
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var (nudp, boundPort, candidates) = await CreateBoundSocketAndGatherCandidatesAsync(localPort, $"[UDP INIT] Gathered candidates for connection with {peer.Name}", cancellationToken).ConfigureAwait(false);
            try
            {
                var (decision, connectionGuid) = await NegotiateConnection(protocolHandler, peer, boundPort, candidates, cancellationToken);
                if (!decision.Accepted)
                {
                    nudp.Dispose();
                    return (false, null, null);
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken);
                var res = await QuicPunchConnection.OpenPortCore(CurrentPeer, nudp, peer, decision.Candidates, (ushort)decision.Port!, connectionGuid, linkedCts.Token);
                if (!res.Success) nudp.Dispose();
                return res;
            }
            catch
            {
                nudp.Dispose();
                throw;
            }
        }
        public async Task InitQuicConnection(Guid protocolHandler, PeerInfo peer, ushort localPort = 0, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            EnsureApplicationPeerAllowed(peer);
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var flightKey = (peer.Id, protocolHandler);

            // 1. If an active protocol session already exists and is healthy, reuse it without renegotiating.
            if (_activeProtocolSessions.TryGetValue(flightKey, out _))
            {
                QuicPunchLog.Info($"[QUIC INIT] Protocol session with {peer.Name} ({peer.Id}) for {handler.ProtocolName} ({protocolHandler}) is already active. Reusing existing session.");
                return;
            }

            // 2. Single flight coordination: ensure exactly one initiation runs per (PeerId, ProtocolId).
            TaskCompletionSource<object?>? myTcs = null;
            ConnectionFlight? myFlight = null;
            Task? flightToAwait = null;

            while (true)
            {
                if (_activeProtocolSessions.TryGetValue(flightKey, out _))
                {
                    QuicPunchLog.Info($"[QUIC INIT] Protocol session with {peer.Name} ({peer.Id}) for {handler.ProtocolName} ({protocolHandler}) is already active. Reusing existing session.");
                    return;
                }

                if (_activeConnectionFlights.TryGetValue(flightKey, out var existingFlight))
                {
                    QuicPunchLog.Info($"[QUIC INIT] Connection with {peer.Name} ({peer.Id}) for {handler.ProtocolName} already in flight (Attempt: {existingFlight.AttemptId}). Awaiting existing flight.");
                    flightToAwait = existingFlight.Task;
                    break;
                }

                if (myTcs == null)
                {
                    myTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    myFlight = new ConnectionFlight(myTcs.Task);
                }

                if (_activeConnectionFlights.TryAdd(flightKey, myFlight!))
                {
                    break;
                }
            }

            if (flightToAwait != null)
            {
                await flightToAwait.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // 3. We are the flight initiator
            try
            {
                await ExecuteInitQuicConnectionAsync(handler, peer, localPort, myTcs!, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _activeConnectionFlights.TryRemove(new KeyValuePair<(Guid PeerId, Guid ProtocolId), ConnectionFlight>(flightKey, myFlight!));
            }
        }

        private async Task ExecuteInitQuicConnectionAsync(
            IProtocolHandler handler,
            PeerInfo peer,
            ushort localPort,
            TaskCompletionSource<object?> flightTcs,
            CancellationToken cancellationToken)
        {
            var protocolHandler = handler.ProtocolId;

            if (peer.ActiveTransport == TransportType.Tor && peer.TorChannel != null)
            {
                try
                {
                    var (decision, connectionGuid) = await NegotiateConnection(protocolHandler, peer, localPort, null, cancellationToken).ConfigureAwait(false);
                    if (!decision.Accepted)
                    {
                        flightTcs.TrySetResult(null);
                        return;
                    }

                    var quicConn = peer.TorChannel.CreateQuicConnection();
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken);
                    var quicStream = await quicConn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, linkedCts.Token).ConfigureAwait(false);
                    bool torRegistered = await RegisterProtocolSessionAsync(peer.Id, protocolHandler, quicConn, quicStream, _lifecycleGeneration).ConfigureAwait(false);
                    flightTcs.TrySetResult(null);
                    if (!torRegistered)
                    {
                        return;
                    }

                    try
                    {
                        await handler.HandleAsync(quicConn, quicStream, peer, linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await UnregisterProtocolSessionAsync(peer.Id, protocolHandler, quicConn).ConfigureAwait(false);
                        try { await quicStream.DisposeAsync().ConfigureAwait(false); } catch { }
                        try { await quicConn.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    flightTcs.TrySetException(ex);
                    throw;
                }
                return;
            }

            UdpClient? nudp = null;
            try
            {
                var gathered = await CreateBoundSocketAndGatherCandidatesAsync(localPort, $"[QUIC INIT] Gathered candidates for connection with {peer.Name}", cancellationToken).ConfigureAwait(false);
                nudp = gathered.Socket;
                ushort boundPort = gathered.BoundPort;
                List<CandidateEndpoint> candidates = gathered.Candidates;
                var (decision, connectionGuid) = await NegotiateConnection(protocolHandler, peer, boundPort, candidates, cancellationToken).ConfigureAwait(false);
                if (!decision.Accepted)
                {
                    nudp.Dispose();
                    flightTcs.TrySetResult(null);
                    return;
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken);
                var connection = await QuicPunchConnection.InitQuicConnectionCore(this, CurrentPeer, nudp, peer, decision.Candidates, (ushort)decision.Port!, connectionGuid, CertManager.PeerCertificate!, handler.CompressionOptions, linkedCts.Token).ConfigureAwait(false);

                if (connection.Connection == null || connection.Stream == null)
                {
                    flightTcs.TrySetResult(null);
                    await handler.DeniedAsync(peer, linkedCts.Token).ConfigureAwait(false);
                }
                else
                {
                    bool wanRegistered = await RegisterProtocolSessionAsync(peer.Id, protocolHandler, connection.Connection, connection.Stream, _lifecycleGeneration).ConfigureAwait(false);
                    flightTcs.TrySetResult(null);
                    if (!wanRegistered)
                    {
                        return;
                    }

                    try
                    {
                        await handler.HandleAsync(connection.Connection, connection.Stream, peer, linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await UnregisterProtocolSessionAsync(peer.Id, protocolHandler, connection.Connection).ConfigureAwait(false);
                        try { await connection.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                        try { await connection.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                nudp?.Dispose();
                flightTcs.TrySetException(ex);
                throw;
            }
        }


        public class ActiveInterrogationSession
        {
            public string Id { get; } = Guid.NewGuid().ToString();
            public PeerInfo Peer { get; }
            public DateTime StartTime { get; } = DateTime.Now;
            public CancellationTokenSource Cts { get; }
            public bool OwnsPeer { get; }

            public ActiveInterrogationSession(PeerInfo peer, CancellationTokenSource cts, bool ownsPeer = false)
            {
                Peer = peer;
                Cts = cts;
                OwnsPeer = ownsPeer;
            }
        }

        public ConcurrentDictionary<string, ActiveInterrogationSession> ActiveInterrogations { get; } = new();

        public bool CancelInterrogation(string id)
        {
            if (ActiveInterrogations.TryRemove(id, out var session))
            {
                try { session.Cts.Cancel(); } catch { }
                return true;
            }
            return false;
        }

        public async Task PeerInterrogation(string token, CancellationToken cancellationToken = default)
        {
            var p = Utilities.DecodeEndpointToken(token);
            if (p.TryGetCertificateHash(out var certHash))
                TrustPeer(certHash);
            bool ownsDecodedPeer = p.Addresses != null && p.Addresses.Length > 0;
            await PeerInterrogationCore(p, cancellationToken, ownsDecodedPeer, useDiscoveryBudget: false).ConfigureAwait(false);
        }

        private static bool PeerTargetsMatch(PeerInfo a, PeerInfo b)
        {
            bool aHasIdentity = a.TryGetCertificateHash(out var aHash);
            bool bHasIdentity = b.TryGetCertificateHash(out var bHash);
            if (aHasIdentity && bHasIdentity)
            {
                // Once both sides have identities, endpoint reuse must never make two
                // different peers compare equal. Endpoint matching is only a bridge
                // while at least one side is still an unauthenticated candidate.
                return CryptographicOperations.FixedTimeEquals(aHash, bHash);
            }

            if (!string.IsNullOrWhiteSpace(a.OnionAddress) && !string.IsNullOrWhiteSpace(b.OnionAddress) &&
                string.Equals(a.OnionAddress, b.OnionAddress, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (a.ActiveEndPoint != null && b.ActiveEndPoint != null && a.ActiveEndPoint.Equals(b.ActiveEndPoint))
                return true;

            if (a.ActiveEndPoint != null && EndpointMatchesPeer(a.ActiveEndPoint, b))
                return true;
            if (b.ActiveEndPoint != null && EndpointMatchesPeer(b.ActiveEndPoint, a))
                return true;

            if (a.Addresses != null && b.Addresses != null && a.Addresses.Intersect(b.Addresses).Any())
            {
                int aMin = a.MinPort > 0 ? a.MinPort : a.MaxPort;
                int aMax = a.MaxPort > 0 ? a.MaxPort : a.MinPort;
                int bMin = b.MinPort > 0 ? b.MinPort : b.MaxPort;
                int bMax = b.MaxPort > 0 ? b.MaxPort : b.MinPort;

                if (aMin > 0 && aMax > 0 && bMin > 0 && bMax > 0 && Math.Max(aMin, bMin) <= Math.Min(aMax, bMax))
                    return true;
            }

            return false;
        }

        private static bool EndpointMatchesPeer(IPEndPoint endpoint, PeerInfo peer)
        {
            if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Equals(endpoint))
                return true;

            if (peer.Addresses == null || !peer.Addresses.Contains(endpoint.Address))
                return false;

            int minPort = peer.MinPort > 0 ? peer.MinPort : peer.MaxPort;
            int maxPort = peer.MaxPort > 0 ? peer.MaxPort : peer.MinPort;
            return minPort > 0 && maxPort > 0 && endpoint.Port >= minPort && endpoint.Port <= maxPort;
        }

        private PeerInfo? ResolveAvailablePeer(PeerInfo candidate)
        {
            if (candidate.TryGetId(out var peerId) && AvailablePeers.TryGetValue(peerId, out var byId))
                return byId;

            return null;
        }

        internal void CancelMatchingInterrogations(PeerInfo peer)
        {
            var keys = ActiveInterrogations
                .Where(kv => PeerTargetsMatch(kv.Value.Peer, peer))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in keys)
                CancelInterrogation(key);
        }

        public Task PeerInterrogation(PeerInfo peer, CancellationToken cancellationToken = default)
        {
            return PeerInterrogationCore(peer, cancellationToken, ownsPeer: false, useDiscoveryBudget: false);
        }

        internal Task PeerInterrogationDiscovered(PeerInfo peer, CancellationToken cancellationToken = default)
        {
            return PeerInterrogationCore(peer, cancellationToken, ownsPeer: true, useDiscoveryBudget: true);
        }

        private async Task PeerInterrogationCore(PeerInfo peer, CancellationToken cancellationToken, bool ownsPeer, bool useDiscoveryBudget)
        {
            if (peer == null)
                throw new ArgumentNullException(nameof(peer));

            if (peer.TryGetCertificateHash(out var peerHash))
            {
                if (CurrentPeer != null && CurrentPeer.TryGetCertificateHash(out var currentHash) && CryptographicOperations.FixedTimeEquals(peerHash, currentHash))
                {
                    if (ownsPeer) peer.Dispose();
                    return;
                }

                if (TorCurrentPeer != null && TorCurrentPeer.TryGetCertificateHash(out var torCurrentHash) && CryptographicOperations.FixedTimeEquals(peerHash, torCurrentHash))
                {
                    if (ownsPeer) peer.Dispose();
                    return;
                }
            }

            bool isTorPeer = peer.NetworkType == NetworkType.Tor || (peer.Addresses == null || peer.Addresses.Length == 0 && !string.IsNullOrEmpty(peer.OnionAddress));
            bool isWanPeer = peer.Addresses != null && peer.Addresses.Length > 0;

            if (isTorPeer)
            {
                if (!IsTorStarted || TorManager == null || TorHub == null)
                {
                    if (ownsPeer) peer.Dispose();
                    throw new InvalidOperationException("Cannot connect to Tor token: Tor service is not started. Start Tor first.");
                }

                if (!string.IsNullOrEmpty(peer.OnionAddress))
                {
                    try
                    {
                        await ConnectTorAsync(peer.OnionAddress, peer.MinPort > 0 ? peer.MinPort : 443, cancellationToken, peer).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (ownsPeer)
                        {
                            peer.TorChannel = null;
                            peer.Dispose();
                        }
                    }
                    return;
                }
            }

            if (!isWanPeer)
            {
                if (ownsPeer) peer.Dispose();
                return;
            }

            if (!IsStarted || udp == null)
            {
                if (ownsPeer) peer.Dispose();
                throw new InvalidOperationException("Cannot connect to WAN token: WAN/UDP transport is not active. Start WAN service first.");
            }

            bool discoverySlotAcquired = false;
            if (useDiscoveryBudget)
            {
                // Nostr/shared-peer discovery is untrusted input. Keep only a small
                // fixed number of automatic hole-punch attempts active at once.
                if (!_discoveryInterrogationSlots.Wait(0))
                {
                    if (ownsPeer) peer.Dispose();
                    return;
                }
                discoverySlotAcquired = true;
            }

            CancellationTokenSource? lcts = null;
            try
            {
                lcts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken);
                var session = new ActiveInterrogationSession(peer, lcts, ownsPeer);

                var existingKeys = ActiveInterrogations
                    .Where(kv =>
                        PeerTargetsMatch(kv.Value.Peer, peer) ||
                        (kv.Value.Peer.Addresses != null && peer.Addresses != null && kv.Value.Peer.Addresses.Any(a => !IPAddress.IsLoopback(a) && peer.Addresses.Contains(a))) ||
                        (!string.IsNullOrEmpty(kv.Value.Peer.OnionAddress) && string.Equals(kv.Value.Peer.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(kv.Value.Peer.Name) && !string.IsNullOrWhiteSpace(peer.Name) && kv.Value.Peer.Name != "Peer" && kv.Value.Peer.Name != "Saved Peer" && kv.Value.Peer.Name != "Discovered Peer" && string.Equals(kv.Value.Peer.Name, peer.Name, StringComparison.OrdinalIgnoreCase))
                    )
                    .Select(kv => kv.Key).ToList();
                foreach (var k in existingKeys)
                {
                    CancelInterrogation(k);
                }

                ActiveInterrogations[session.Id] = session;

                WriteLine($"Starting interrogation for {string.Join(", ", peer.Addresses)}...");
                var sendSocket = udp!;
                var sessionCts = lcts!;
                bool releaseDiscoverySlot = discoverySlotAcquired;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendLoopAsync(sendSocket, peer, sessionCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ActiveInterrogations.TryRemove(session.Id, out _);
                        sessionCts.Dispose();
                        if (session.OwnsPeer)
                            session.Peer.Dispose();
                        if (releaseDiscoverySlot)
                            _discoveryInterrogationSlots.Release();
                    }
                });

                // The background interrogation now owns both sessionCts and, for
                // discovered/manual WAN tokens, the temporary PeerInfo.
                lcts = null;
                discoverySlotAcquired = false;
            }
            catch
            {
                lcts?.Dispose();
                if (discoverySlotAcquired)
                    _discoveryInterrogationSlots.Release();
                if (ownsPeer) peer.Dispose();
                throw;
            }
        }

        public Task ProcessIncomingPacketAsync(byte[] buffer, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel = null, bool isLanDiscovery = false)
        {
            if (buffer == null) return Task.CompletedTask;
            return ProcessIncomingPacketAsync(buffer, buffer.Length, remoteEndPoint, transport, torChannel, isLanDiscovery);
        }

        public async Task ProcessIncomingPacketAsync(byte[] buffer, int length, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel = null, bool isLanDiscovery = false)
        {
            if (LifecycleState == QuicPunchLifecycleState.Disposed || LifecycleState == QuicPunchLifecycleState.Stopped || CancellationSource == null || CancellationSource.IsCancellationRequested)
                return;

            if (buffer == null || length < MagicHeader.Length + 1)
                return;

            var span = buffer.AsSpan(0, length);
            if (!span.Slice(0, MagicHeader.Length).SequenceEqual(MagicHeader))
                return;

            byte messageType = span[MagicHeader.Length];

            if (messageType == (byte)MessageType.Data)
            {
                const int TagSize = 16;
                int headerAadSize = MagicHeader.Length + sizeof(byte) + sizeof(ushort) + sizeof(uint) + sizeof(ulong);
                int minimumLength = headerAadSize + TagSize;

                if (span.Length < minimumLength)
                {
                    return;
                }

                int offset = MagicHeader.Length + sizeof(byte);

                ushort packetType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, sizeof(ushort)));
                offset += sizeof(ushort);

                uint senderShortId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, sizeof(uint)));
                offset += sizeof(uint);

                if (!_peersByShortId.TryGetValue(senderShortId, out var peer) || peer.RxCipher == null || peer.RxSalt == null)
                {
                    return;
                }

                // Discovery/authentication is control-plane only. Application data
                // is never delivered until the identity has been explicitly trusted,
                // unless the library caller deliberately opts into the legacy bypass.
                if (!AutoAcceptUntrustedConnections && !IsTrustedPeer(peer))
                {
                    return;
                }

                ulong sequenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, sizeof(ulong)));
                offset += sizeof(ulong);

                if (!peer.InboundReplayFilter.Check(sequenceNumber))
                {
                    return;
                }

                ReadOnlySpan<byte> associatedData = span.Slice(0, headerAadSize);
                ReadOnlySpan<byte> tag = span.Slice(offset, TagSize);
                offset += TagSize;

                ReadOnlySpan<byte> ciphertext = span[offset..];
                var plaintext = new byte[ciphertext.Length];

                Span<byte> nonce = stackalloc byte[12];
                peer.RxSalt.CopyTo(nonce.Slice(0, 4));
                BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), sequenceNumber);

                try
                {
                    peer.RxCipher.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
                }
                catch (CryptographicException)
                {
                    return;
                }

                if (!peer.InboundReplayFilter.CheckAndAdd(sequenceNumber))
                {
                    return;
                }

                PublishReceivedData(peer.Id, packetType, plaintext);
                return;
            }

            using (MemoryStream ms = new MemoryStream(buffer, 0, length, writable: false))
            using (BinaryReader r = new BinaryReader(ms))
            {
                ms.Position = MagicHeader.Length + 1;

                switch (messageType)
                {
                    case (byte)MessageType.Interrogation:
                    case (byte)MessageType.Hello:
                        HelloHandler.HandleHello(this, r, udp, remoteEndPoint, buffer, messageType, transport, torChannel, isLanDiscovery);
                        break;

                    case (byte)MessageType.Ack:
                        AckHandler.HandleAck(this, r, udp, remoteEndPoint, buffer, transport, torChannel);
                        break;

                    case (byte)MessageType.Handshake:
                        HandshakeHandler.HandleHandshake(this, r, udp, remoteEndPoint, buffer, transport, torChannel);
                        break;

                    case (byte)MessageType.Ping:
                        PingHandler.HandlePing(this, r, udp, remoteEndPoint, transport, torChannel);
                        break;

                    case (byte)MessageType.Disconnect:
                        DisconnectHandler.HandleDisconnect(this, r, udp, remoteEndPoint, buffer, transport, torChannel);
                        break;

                    case (byte)MessageType.QuicReady:
                        HandleQuicReady(r, remoteEndPoint, buffer, transport, torChannel);
                        break;


                    default:
                        WriteLine($"Received unknown message type {(char)messageType} from {remoteEndPoint}");
                        break;
                }
            }
        }

        private async Task ReceiveUdpLoopAsync(UdpClient socket, CancellationToken ct)
        {
            byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        EndPoint remoteEndPoint = socket.Client.AddressFamily == AddressFamily.InterNetworkV6
                            ? new IPEndPoint(IPAddress.IPv6Any, 0)
                            : new IPEndPoint(IPAddress.Any, 0);

                        SocketReceiveFromResult result = await socket.Client.ReceiveFromAsync(
                            receiveBuffer.AsMemory(0, 65536),
                            SocketFlags.None,
                            remoteEndPoint,
                            ct).ConfigureAwait(false);

                        int bytesRead = result.ReceivedBytes;
                        if (bytesRead <= 0) continue;

                        var remoteEp = (IPEndPoint)result.RemoteEndPoint;

                        if (NatCoordinator.TryProcessIncoming(receiveBuffer.AsSpan(0, bytesRead), remoteEp))
                        {
                            continue;
                        }

                        if (!_rateLimiter.IsAllowed(remoteEp.Address))
                            continue;

                        await ProcessIncomingPacketAsync(receiveBuffer, bytesRead, remoteEp, TransportType.Wan).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException sex)
                    {
                        if (ct.IsCancellationRequested ||
                            sex.SocketErrorCode == SocketError.OperationAborted ||
                            sex.SocketErrorCode == SocketError.Interrupted ||
                            sex.SocketErrorCode == SocketError.InvalidArgument)
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ct.IsCancellationRequested)
                            break;
                        QuicPunchLog.Error($"[ReceiveUdpLoopAsync] Error processing packet: {ex.Message}", ex);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(receiveBuffer);
            }
        }

        private void StartTorReceiveLoop(TorQuicConnectionManager channel, CancellationToken token = default)
        {
            foreach (var completed in _torReceiveLoopTasks.Where(kvp => kvp.Value.IsCompleted).ToArray())
                _torReceiveLoopTasks.TryRemove(completed.Key, out _);

            CancellationToken loopToken = _torLoopCts?.Token ?? LifecycleToken;
            var task = Task.Run(async () =>
            {
                try { await ReceiveTorConnectionLoopAsync(channel, loopToken).ConfigureAwait(false); }
                catch { }
            }, CancellationToken.None);
            _torReceiveLoopTasks[channel.ConnectionId] = task;
        }

        private async Task AwaitTorWorkersAsync()
        {
            var tasks = _torReceiveLoopTasks.Values
                .Concat(_torAcceptLoopTask != null ? new[] { _torAcceptLoopTask } : Array.Empty<Task>())
                .Distinct()
                .ToArray();
            if (tasks.Length > 0)
            {
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
            _torReceiveLoopTasks.Clear();
            _torAcceptLoopTask = null;
            try { _torLoopCts?.Dispose(); } catch { }
            _torLoopCts = null;
        }

        private async Task ReceiveTorConnectionLoopAsync(TorQuicConnectionManager channel, CancellationToken token)
        {
            try
            {
                string onionHost = !string.IsNullOrEmpty(channel.RemoteOnion) ? channel.RemoteOnion : "127.0.0.1";
                var endPoint = channel.RemoteEndPoint ?? new DnsEndPoint(onionHost, channel.RemoteVirtualPort > 0 ? channel.RemoteVirtualPort : 443);
                while (!token.IsCancellationRequested && !channel.IsClosed)
                {
                    try
                    {
                        var messageMemory = await channel.ReceiveMessageAsync(token).ConfigureAwait(false);
                        byte[] message = messageMemory.ToArray();
                        if (message.Length > 0)
                        {
                            await ProcessIncomingPacketAsync(message, endPoint, TransportType.Tor, channel).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (channel.IsClosed || token.IsCancellationRequested)
                            break;
                        QuicPunchLog.Error("Error processing packet in ReceiveTorConnectionLoopAsync", ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    QuicPunchLog.Error("Error in ReceiveTorConnectionLoopAsync", ex);
            }
        }

        private async Task AcceptTorLoopAsync(CancellationToken token)
        {
            var hub = TorHub;
            if (hub == null) return;
            QuicPunchLog.Info($"[AcceptTorLoopAsync] Starting loop for node {CurrentPeer.Name}...");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var channel = await TorQuicConnectionManager.AcceptAsync(hub, cancellationToken: token).ConfigureAwait(false);
                    QuicPunchLog.Info($"[ACCEPT TOR LOOP] Accepted incoming Tor channel {channel.ConnectionId} from {channel.RemoteOnion}");
                    StartTorReceiveLoop(channel, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    QuicPunchLog.Info($"Notice in AcceptTorLoopAsync: {ex.Message}");
            }
        }

        internal byte[] GenerateAck(bool sharePeers, TransportType transport = TransportType.Wan) =>
            PacketBuilder.GenerateAck(this, sharePeers, transport);

        internal byte[] GenerateHelloPayload(MessageType type, bool passwordProof, byte[]? challengeNonce = null, TransportType transport = TransportType.Wan, PeerInfo? targetPeer = null) =>
            PacketBuilder.GenerateHelloPayload(this, type, passwordProof, challengeNonce, transport, targetPeer);

        internal byte[] BuildPingPacket(long timestamp, bool isResponse = false, TransportType transport = TransportType.Wan) =>
            PacketBuilder.BuildPingPacket(this, timestamp, isResponse, transport);

        internal byte[] GenerateHandshakePayload(HandShakeType type, ushort port, Guid protocolId, Guid connectionGuid, IReadOnlyList<CandidateEndpoint>? candidates = null, TransportType transport = TransportType.Wan) =>
            PacketBuilder.GenerateHandshakePayload(this, type, port, protocolId, connectionGuid, candidates, transport);

        private async Task SendLoopAsync(UdpClient udp, PeerInfo peer, CancellationToken token)
        {
            int tries = 0;

            bool includePassword = PasswordHash != null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    PeerInfo? availablePeer = ResolveAvailablePeer(peer);
                    bool peerResponded = availablePeer != null;

                    var helloPayload = GenerateHelloPayload(MessageType.Hello, includePassword, targetPeer: availablePeer ?? peer);

                    if (peerResponded && availablePeer!.ActiveEndPoint != null)
                    {
                        // Keep the last authenticated path alive while probing the
                        // fresh rendezvous candidate. Identity is unchanged; only a
                        // signed Hello may promote a new endpoint.
                        await udp.SendAsync(helloPayload, availablePeer.ActiveEndPoint).ConfigureAwait(false);
                    }

                    bool hasFreshCandidate = (peer.Addresses?.Length ?? 0) > 0;
                    if (!peerResponded || hasFreshCandidate)
                    {
                        var payload = GenerateHelloPayload(MessageType.Interrogation, true, targetPeer: peer);
                        await udp.BigSendAsync(payload, peer).ConfigureAwait(false);
                    }

                    tries++;

                    int delayMs;
                    if (tries <= 5)
                    {
                        delayMs = 125;
                    }
                    else
                    {
                        delayMs = Math.Min(((tries - 5) * 2) * 1000, 20000);
                    }

                    _ = PreciseTime.WaitNextTrigger(delayMs);

                    QuicPunchLog.Info($"Send hello packet to {peer} that responded {peerResponded} at {PreciseTime.GetCorrectTime():HH:mm:ss.fff} time til next {TimeSpan.FromTicks(delayMs).Seconds}");

                    await Task.Delay(delayMs, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Error("[QuicPunch] Error in periodic hello loop", ex);
                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        public async ValueTask SendPayloadAsync(PeerInfo peer, ushort packetType, ReadOnlyMemory<byte> payload)
        {
            EnsureStarted();
            EnsureApplicationPeerAllowed(peer);
            if (peer.TxCipher == null || peer.TxSalt == null)
                throw new InvalidOperationException("Peer cipher is not initialized.");

            var currentPeer = GetCurrentPeer(peer.ActiveTransport);

            const int TagSize = 16;
            int headerAadSize = MagicHeader.Length + sizeof(byte) + sizeof(ushort) + sizeof(uint) + sizeof(ulong);

            ulong sequenceNumber = peer.GetNextOutboundSequence();

            int packetLength = headerAadSize + TagSize + payload.Length;

            byte[] packet = ArrayPool<byte>.Shared.Rent(packetLength);
            try
            {
                Span<byte> span = packet.AsSpan(0, packetLength);
                int offset = 0;

                MagicHeader.CopyTo(span[offset..]);
                offset += MagicHeader.Length;

                span[offset++] = (byte)MessageType.Data;

                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, sizeof(ushort)), packetType);
                offset += sizeof(ushort);

                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, sizeof(uint)), currentPeer.ShortId);
                offset += sizeof(uint);

                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(offset, sizeof(ulong)), sequenceNumber);
                offset += sizeof(ulong);

                ReadOnlySpan<byte> associatedData = span.Slice(0, headerAadSize);

                Span<byte> tag = span.Slice(offset, TagSize);
                offset += TagSize;

                Span<byte> ciphertext = span.Slice(offset);

                Span<byte> nonce = stackalloc byte[12];
                peer.TxSalt.CopyTo(nonce.Slice(0, 4));
                BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), sequenceNumber);

                peer.TxCipher.Encrypt(nonce, payload.Span, ciphertext, tag, associatedData);

                if (peer.ActiveTransport == TransportType.Tor && peer.TorChannel != null)
                {
                    await peer.TorChannel.SendMessageAsync(packet.AsMemory(0, packetLength)).ConfigureAwait(false);
                }
                else if (udp != null)
                {
                    if (peer.ActiveEndPoint is { } active && Utilities.IsValidPeerAddress(active.Address) && active.Port is >= 1 and <= 65535)
                    {
                        try
                        {
                            await udp.SendAsync(packet.AsMemory(0, packetLength), active).ConfigureAwait(false);
                        }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }
                    }
                    else
                    {
                        await udp.BigSendAsync(packet.AsMemory(0, packetLength), peer)
                                 .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }

        private string? _cachedWanToken;
        private string? _cachedTorToken;
        private readonly object _tokenCacheLock = new();

        public void InvalidateTokenCache()
        {
            lock (_tokenCacheLock)
            {
                _cachedWanToken = null;
                _cachedTorToken = null;
            }
        }

        public string GetToken() => GetWanToken();

        public string GetWanToken()
        {
            lock (_tokenCacheLock)
            {
                if (_cachedWanToken != null)
                    return _cachedWanToken;
                return _cachedWanToken = Utilities.EncodeEndpointToken(CurrentPeer);
            }
        }

        public string? GetTorToken()
        {
            if (string.IsNullOrEmpty(TorCurrentPeer?.OnionAddress))
                return null;
            lock (_tokenCacheLock)
            {
                if (_cachedTorToken != null)
                    return _cachedTorToken;
                return _cachedTorToken = Utilities.EncodeEndpointToken(TorCurrentPeer);
            }
        }

        public string GetToken(TransportType transport) =>
            transport == TransportType.Tor ? (GetTorToken() ?? "") : GetWanToken();

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

            try { CancellationSource?.Dispose(); } catch { }
            try { CertManager?.Dispose(); } catch { }
            try { TorCertManager?.Dispose(); } catch { }
        }

        private void CleanupResourcesSync()
        {
            CleanupSyncCore();
            _activeIncomingWorkers.Clear();

            List<(QuicConnection Connection, Stream Stream)> sessionsToDispose;
            lock (_activeProtocolSessions)
            {
                sessionsToDispose = _activeProtocolSessions.Values.ToList();
                _activeProtocolSessions.Clear();
            }
            foreach (var session in sessionsToDispose)
            {
                try { session.Stream.Dispose(); } catch { }
                try { _ = session.Connection.DisposeAsync().AsTask(); } catch { }
            }

            _torLifecycleLock.Wait();
            try
            {
                try { _torLoopCts?.Cancel(); } catch { }
                if (TorHub != null)
                {
                    try { _ = TorHub.DisposeAsync().AsTask(); } catch { }
                    TorHub = null;
                }
                if (TorManager != null)
                {
                    try { _ = TorManager.DisposeAsync().AsTask(); } catch { }
                    TorManager = null;
                }
            }
            finally
            {
                _torLifecycleLock.Release();
            }

            if (_portCoordinator != null && IsUpnpMapped && LocalPort > 0)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    _ = _portCoordinator.TryUnmapPortAsync(LocalPort, ct: cts.Token);
                }
                catch { }
                try { _portCoordinator.Dispose(); } catch { }
                _portCoordinator = null;
                IsUpnpMapped = false;
                ActivePortMappingProtocol = null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _lifecycleLock.Wait();
            try
            {
                if (LifecycleState == QuicPunchLifecycleState.Started || LifecycleState == QuicPunchLifecycleState.Starting)
                {
                    LifecycleState = QuicPunchLifecycleState.Stopping;
                }

                CleanupResourcesSync();
                LifecycleState = QuicPunchLifecycleState.Disposed;
            }
            finally
            {
                _lifecycleLock.Release();
            }

            try { CancellationSource?.Dispose(); } catch { }
            try { CertManager?.Dispose(); } catch { }
            try { TorCertManager?.Dispose(); } catch { }
        }

        public enum NetworkType : byte
        {
            Unknown = 255,
            Static  = 0,
            DynamicPort = 1,
            DynamicAddress = 2,
            DynamicPortAndAddress = 3,
            Tor = 4
        }
    }
}
