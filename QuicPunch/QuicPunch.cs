using QuicPunch.Helpers;
using QuicPunch.PacketHandler;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
        private Task? _lanDiscoveryLoopTask = null;

        public int LocalDiscoveryPort { get; private set; } //Random.Shared.Next(1, 1024);
        public int LocalBoundPort => udp != null && udp.Client.LocalEndPoint is IPEndPoint ip ? ip.Port : LocalPort;

        public bool RebindListenerPort(ushort newPort)
        {
            EnsureStarted();
            _lifecycleLock.Wait();
            try
            {
                if (LifecycleState != QuicPunchLifecycleState.Started)
                    return false;

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
                TrackerScanner?.UpdateAnnouncement(CurrentPeer.Addresses, LocalDiscoveryPort);

                if (_StunServerEndpoints != null && _StunServerEndpoints.Length > 0)
                {
                    _StunClient = new SimpleStunClient(newUdp, _StunServerEndpoints);
                }

                try { oldUdp?.Close(); oldUdp?.Dispose(); } catch { }

                _receiveLoopTask = Task.Run(() => ReceiveUdpLoopAsync(newUdp, CancellationSource.Token), CancellationSource.Token);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StunRequest(resetOnFailure: true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[QuicPunch] STUN request after rebind warning: {ex.Message}");
                    }

                    int announcedPort = MostUsedPort > 0 ? MostUsedPort : (CurrentPeer.MinPort > 0 ? CurrentPeer.MinPort : LocalDiscoveryPort);
                    if (TrackerScanner != null)
                    {
                        TrackerScanner.UpdateAnnouncement(CurrentPeer.Addresses, announcedPort);
                    }
                    else if (PoolId != null && PoolId.Length == 20)
                    {
                        TrackerScanner = new TrackerScanner(PoolId, announcedPort);
                        TrackerScanner.SetPublicAddresses(CurrentPeer.Addresses);
                        TrackerScanner.OnPeerFound += OnTrackerPeerDiscovered;
                        _ = TrackerScanner.Start(CustomTrackers);
                    }
                });

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

        public static string AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicPunchV17");
        public string NodeAppDataPath { get; set; }

        public PeerInfo CurrentPeer { get; private set; } = null!;

        private IPEndPoint[] _StunServerEndpoints = Array.Empty<IPEndPoint>();
        public IReadOnlyList<IPEndPoint> StunServerEndpoints
        {
            get => _StunServerEndpoints;
            set => _StunServerEndpoints = value != null ? System.Linq.Enumerable.ToArray(value) : Array.Empty<IPEndPoint>();
        }

        private SimpleStunClient? _StunClient;

        public PeerStore PeerStore { get; private set; } = null!;

        public readonly HandshakeManager _manager = new HandshakeManager();
        public HandshakeManager Manager => _manager;

        private readonly IpRateLimiter _rateLimiter = new IpRateLimiter(500);

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();

        private int LocalPort;
        private int MostUsedPort;

        private (int minPort, int maxPort) StunPortRange;
        private void ResetNatMapping()
        {
            MostUsedPort = 0;
            if (CurrentPeer != null)
            {
                CurrentPeer.MinPort = LocalDiscoveryPort;
                CurrentPeer.MaxPort = LocalDiscoveryPort;
            }
            StunPortRange = (LocalDiscoveryPort, LocalDiscoveryPort);
        }

        public CertManager CertManager { get; private set; } = null!;
        public CertManager TorCertManager { get; private set; } = null!;

        private int CertPublicKey { get; set; }

        public string? TorOnionAddress { get; private set; }
        public TorIdentity? TorIdentity { get; private set; }
        public TorPeerTransportHub? TorHub { get; private set; }
        public TorManager? TorManager { get; private set; }
        public bool IsTorStarted => TorManager != null && TorHub != null;
        public string TorBootstrapStatus { get; private set; } = "Not initialized";
        public int TorBootstrapProgress { get; private set; } = 0;
        public string? TorLastError { get; private set; }

        public enum TransportType
        {
            Wan = 0,
            Tor = 1
        }

        private Task? _receiveLoopTask;
        private Task? _stunLoopTask;
        private Task? _maintenanceLoopTask;

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

            if (discoveryId != null)
            {
                _poolId = discoveryId.Length == 20 ? discoveryId : SHA1.HashData(discoveryId);
            }

            _connectionPassword = connectionPassword;
            if (_connectionPassword != null)
            {
                DerivePasswordHash();
            }

            AutoAcceptConnections = autoAcceptConnections;
            LocalPort = listeningPort == 0 ? Utilities.GetDeterministicPortFromCertHash(CertManager.CertPublicHash) : listeningPort;

            CurrentPeer = new PeerInfo(CertManager.PeerCertificate, CertManager.EcdhPublicKeyRaw)
            {
                Name = $"{Environment.UserName}@{Environment.MachineName}",
                Addresses = Array.Empty<IPAddress>(),
            };

            TorCertManager = new CertManager(NodeAppDataPath, "tor");
            TorCurrentPeer = new PeerInfo(TorCertManager.PeerCertificate, TorCertManager.EcdhPublicKeyRaw)
            {
                Name = $"{Environment.UserName}@{Environment.MachineName}",
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

                CertManager.RenewSessionEntropy();
                TorCertManager?.RenewSessionEntropy();

                try { CancellationSource?.Dispose(); } catch { }
                CancellationSource = _parentCancellationToken.CanBeCanceled || cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_parentCancellationToken, cancellationToken)
                    : new CancellationTokenSource();

                Directory.CreateDirectory(NodeAppDataPath);
                PeerStore = new PeerStore(Path.Combine(NodeAppDataPath, "peers.db"));

                udp = new UdpClient();
                ConfigureUdpSocket(udp);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, LocalPort));
                udp.Client.DontFragment = true;

                LocalDiscoveryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                LocalPort = LocalDiscoveryPort;

                _receiveLoopTask = Task.Run(() => ReceiveUdpLoopAsync(udp, CancellationSource.Token), CancellationSource.Token);

                try
                {
                    _lanDiscoveryUdp = new UdpClient();
                    ConfigureUdpSocket(_lanDiscoveryUdp);
                    _lanDiscoveryUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _lanDiscoveryUdp.Client.Bind(new IPEndPoint(IPAddress.Any, LanDiscoveryPort));
                    _lanDiscoveryUdp.EnableBroadcast = true;
                    try
                    {
                        _lanDiscoveryUdp.JoinMulticastGroup(IPAddress.Parse(DefaultLanDiscoveryMulticast));
                    }
                    catch { }

                    _lanDiscoveryLoopTask = Task.Run(() => ReceiveLanDiscoveryLoopAsync(_lanDiscoveryUdp, CancellationSource.Token));
                    QuicPunchLog.Info($"[LAN DISCOVERY] Listening on common port {LanDiscoveryPort} and multicast group {DefaultLanDiscoveryMulticast}");
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[LAN DISCOVERY] Notice: Could not bind dedicated LAN discovery socket on port {LanDiscoveryPort}: {ex.Message}");
                }

                var stunEndpoints = await StunGatherer.GatherStunEndpoints(ct: CancellationSource.Token).ConfigureAwait(false);
                _StunServerEndpoints = stunEndpoints.ToArray();

                _StunClient = new SimpleStunClient(udp, _StunServerEndpoints);
                try
                {
                    await StunRequest().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[QuicPunch] Initial STUN request warning: {ex.Message}");
                }

                if (_poolId != null && _poolId.Length == 20)
                {
                    int effectivePort = CurrentPeer.MinPort > 0 ? CurrentPeer.MinPort : LocalDiscoveryPort;
                    TrackerScanner = new TrackerScanner(_poolId, effectivePort);
                    TrackerScanner.SetPublicAddresses(CurrentPeer.Addresses);
                    TrackerScanner.OnPeerFound += OnTrackerPeerDiscovered;
                    _ = TrackerScanner.Start(CustomTrackers);
                }

                _stunLoopTask = Task.Run(StartStunRequest, CancellationSource.Token);
                _maintenanceLoopTask = Task.Run(MaintenanceLoopAsync, CancellationSource.Token);
                LifecycleState = QuicPunchLifecycleState.Started;

                _ = Task.Run(() => AutoConnectSavedPeersAsync(CancellationSource.Token));
            }
            catch (Exception ex)
            {
                try { CancellationSource?.Cancel(); } catch { }
                try { TrackerScanner?.Stop(); TrackerScanner?.Dispose(); TrackerScanner = null; } catch { }
                try { udp?.Close(); udp?.Dispose(); udp = null; } catch { }
                try { _lanDiscoveryUdp?.Close(); _lanDiscoveryUdp?.Dispose(); _lanDiscoveryUdp = null; } catch { }
                try { PeerStore?.Dispose(); PeerStore = null; } catch { }
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
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await lanUdp.ReceiveAsync(token).ConfigureAwait(false);
                    if (result.Buffer.Length > 0)
                    {
                        _ = ProcessIncomingPacketAsync(result.Buffer, result.RemoteEndPoint, TransportType.Wan);
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted) { break; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
                }
            }
        }

        private void CleanupSyncCore()
        {
            try { CancellationSource?.Cancel(); } catch { }

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

            foreach (var session in IncomingHandshakeSessions.Values)
            {
                session.MarkRejected();
                session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
            }
            IncomingHandshakeSessions.Clear();

            try { _manager.CancelAll(); } catch { }

            foreach (var kvp in _pendingQuicReady)
            {
                if (_pendingQuicReady.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetCanceled();
                }
            }
            _receivedQuicReady.Clear();

            foreach (var kv in ActiveInterrogations)
            {
                try { kv.Value.Cts?.Cancel(); kv.Value.Cts?.Dispose(); } catch { }
            }
            ActiveInterrogations.Clear();

            try { TrackerScanner?.Stop(); TrackerScanner?.Dispose(); TrackerScanner = null; } catch { }
            try { udp?.Close(); udp?.Dispose(); udp = null; } catch { }
            try { _lanDiscoveryUdp?.Close(); _lanDiscoveryUdp?.Dispose(); _lanDiscoveryUdp = null; } catch { }
            try { PeerStore?.Dispose(); PeerStore = null; } catch { }

            foreach (var peer in AvailablePeers.Values)
            {
                try { peer.Dispose(); } catch { }
            }
            AvailablePeers.Clear();

            CertManager.RenewSessionEntropy();
            TorCertManager?.RenewSessionEntropy();
        }

        private async Task CleanupResourcesAsync()
        {
            if (TrackerScanner != null)
            {
                try { await TrackerScanner.StopAsync().ConfigureAwait(false); } catch { }
                try { TrackerScanner.Dispose(); } catch { }
                TrackerScanner = null;
            }

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
                LifecycleState = QuicPunchLifecycleState.Stopped;
            }
            finally
            {
                _lifecycleLock.Release();
            }

            if (_receiveLoopTask != null)
            {
                try { await _receiveLoopTask.ConfigureAwait(false); } catch { }
            }
            if (_lanDiscoveryLoopTask != null)
            {
                try { await _lanDiscoveryLoopTask.ConfigureAwait(false); } catch { }
            }
            if (_stunLoopTask != null)
            {
                try { await _stunLoopTask.ConfigureAwait(false); } catch { }
            }
            if (_maintenanceLoopTask != null)
            {
                try { await _maintenanceLoopTask.ConfigureAwait(false); } catch { }
            }
        }

        public async Task StartTorAsync(int virtualPort = 0, TorRuntimeOptions? options = null, TorManager? existingTorManager = null, CancellationToken cancellationToken = default)
        {
            QuicPunchLog.Info("[TOR SERVER] Initializing Tor runtime...");

            Directory.CreateDirectory(NodeAppDataPath);
            string identityPath = Path.Combine(NodeAppDataPath, "tor_identity.key");

            if (File.Exists(identityPath))
            {
                try
                {
                    string keyBase64 = await File.ReadAllTextAsync(identityPath, cancellationToken).ConfigureAwait(false);
                    TorIdentity = TorIdentity.FromPrivateKeyBase64(keyBase64);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[TOR SERVER] Warning: Failed to load saved identity: {ex.Message}. Generating new one.");
                    TorIdentity = TorIdentity.CreateRandom();
                    await File.WriteAllTextAsync(identityPath, TorIdentity.PrivateKeyBase64, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                TorIdentity = TorIdentity.CreateRandom();
                await File.WriteAllTextAsync(identityPath, TorIdentity.PrivateKeyBase64, cancellationToken).ConfigureAwait(false);
            }

            TorCertManager = new CertManager(NodeAppDataPath, "tor");

            int resolvedPort = virtualPort > 0
                ? virtualPort
                : (LocalPort > 0 ? LocalPort : Utilities.GetDeterministicPortFromCertHash(TorCertManager.CertPublicHash));

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
                        StartupTimeout = options.StartupTimeout,
                        BootstrapTimeout = options.BootstrapTimeout,
                        ShutdownTimeout = options.ShutdownTimeout
                    }
                    : new TorRuntimeOptions
                    {
                        DataDirectory = Path.Combine(NodeAppDataPath, "TorData")
                    };

                var trm = new TorRuntimeManager(runtimeOptions);
                TorBootstrapStatus = "Bootstrapping...";
                TorBootstrapProgress = 5;
                trm.LogLine += (line) =>
                {
                    if (line.Contains("Bootstrapped ", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = line.IndexOf("Bootstrapped ", StringComparison.OrdinalIgnoreCase);
                        string progress = line[idx..];
                        TorBootstrapStatus = progress;
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d{1,3})%");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out int pVal))
                        {
                            TorBootstrapProgress = pVal;
                        }
                        WriteLine($"[TOR SERVER] {progress}");
                    }
                };

                await trm.StartAsync(cancellationToken).ConfigureAwait(false);
                TorManager = new TorManager(trm);
            }

            TorHub = await TorPeerTransportHub.CreateAsync(TorManager, TorIdentity, resolvedPort, cancellationToken).ConfigureAwait(false);

            TorOnionAddress = TorHub.OnionAddress;
            TorCurrentPeer.OnionAddress = TorHub.OnionAddress;
            TorCurrentPeer.MinPort = resolvedPort;
            TorCurrentPeer.MaxPort = resolvedPort;
            TorBootstrapProgress = 100;
            TorBootstrapStatus = "Active (100%)";
            TorLastError = null;
            WriteLine($"[TOR SERVER] Hidden Service active at: {TorOnionAddress}:{resolvedPort}");

            _ = Task.Run(() => AcceptTorLoopAsync(cancellationToken), cancellationToken);
        }

        public async Task StopTorAsync()
        {
            TorBootstrapStatus = "Stopping...";
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
            TorOnionAddress = null;
            if (CurrentPeer != null)
            {
                CurrentPeer.OnionAddress = null;
            }
            if (TorCurrentPeer != null)
            {
                TorCurrentPeer.OnionAddress = null;
            }
            TorBootstrapStatus = "Stopped";
            TorBootstrapProgress = 0;
        }

        public async Task SendResponseAsync(byte[] payload, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel = null)
        {
            EnsureStarted();
            if (transport == TransportType.Tor && torChannel != null)
            {
                await torChannel.SendMessageAsync(payload).ConfigureAwait(false);
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

        public async Task ConnectTorAsync(string remoteOnion, int remotePort = 443, CancellationToken token = default)
        {
            EnsureStarted();
            if (TorManager == null || TorHub == null)
                throw new InvalidOperationException("Tor service is not started. Call StartTorAsync first.");

            var channel = await TorQuicConnectionManager.ConnectAsync(TorManager, TorHub, remoteOnion, remotePort, cancellationToken: token).ConfigureAwait(false);
            _ = Task.Run(() => ReceiveTorConnectionLoopAsync(channel), token);

            var peerInfo = new PeerInfo
            {
                OnionAddress = remoteOnion,
                MinPort = remotePort,
                MaxPort = remotePort,
                NetworkType = NetworkType.Tor,
                ActiveTransport = TransportType.Tor,
                TorChannel = channel
            };

            var payload = GenerateHelloPayload(MessageType.Interrogation, true, transport: TransportType.Tor);
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
        public byte[] PoolId
        {
            get => _poolId;
            set
            {
                if (value.Length != 20) throw new ArgumentException("InfoHash must be 20 bytes long.");

                _poolId = value;

                if (_connectionPassword != null)
                {
                    DerivePasswordHash();
                }

                if (TrackerScanner != null)
                {
                    TrackerScanner.Stop();
                    int announcedPort = MostUsedPort > 0 ? MostUsedPort : (CurrentPeer.MinPort > 0 ? CurrentPeer.MinPort : LocalDiscoveryPort);
                    TrackerScanner = new TrackerScanner(value, announcedPort);
                    TrackerScanner.SetPublicAddresses(CurrentPeer.Addresses);
                    TrackerScanner.OnPeerFound += OnTrackerPeerDiscovered;
                    _ = TrackerScanner.Start(CustomTrackers);
                }
            }
        }
        internal byte[]? PasswordHash { get; set; }
        public bool AutoAcceptConnections { get; set; } = true;
        public bool AutoAcceptUntrustedConnections { get; set; } = false;
        public bool SharePeers { get; set; }
        public bool AcceptSharedPeers { get; set; }

        private readonly ConcurrentDictionary<Guid, bool> _autoAcceptPeers = new();
        private readonly ConcurrentDictionary<byte[], bool> _autoAcceptCertHashes = new(Utilities.ByteArrayComparer.Instance);

        public void SetAutoAcceptAll(bool autoAccept)
        {
            AutoAcceptConnections = autoAccept;
            AutoAcceptUntrustedConnections = autoAccept;
        }

        public void SetPeerAutoAccept(Guid peerId, bool autoAccept)
        {
            if (autoAccept)
            {
                _autoAcceptPeers[peerId] = true;
                if (AvailablePeers.TryGetValue(peerId, out var peer) && peer.CertHash != null)
                {
                    _autoAcceptCertHashes[peer.CertHash] = true;
                }
            }
            else
            {
                _autoAcceptPeers.TryRemove(peerId, out _);
                if (AvailablePeers.TryGetValue(peerId, out var peer) && peer.CertHash != null)
                {
                    _autoAcceptCertHashes.TryRemove(peer.CertHash, out _);
                }
            }
        }

        public void SetPeerAutoAccept(byte[] certHash, bool autoAccept)
        {
            if (certHash == null) return;
            if (autoAccept)
            {
                _autoAcceptCertHashes[certHash] = true;
                var peer = AvailablePeers.Values.FirstOrDefault(p => p.CertHash != null && CryptographicOperations.FixedTimeEquals(p.CertHash, certHash));
                if (peer != null) _autoAcceptPeers[peer.Id] = true;
            }
            else
            {
                _autoAcceptCertHashes.TryRemove(certHash, out _);
                var peer = AvailablePeers.Values.FirstOrDefault(p => p.CertHash != null && CryptographicOperations.FixedTimeEquals(p.CertHash, certHash));
                if (peer != null) _autoAcceptPeers.TryRemove(peer.Id, out _);
            }
        }

        public bool IsPeerAutoAccepted(Guid peerId)
        {
            if (AutoAcceptUntrustedConnections) return true;
            if (_autoAcceptPeers.ContainsKey(peerId)) return true;
            if (AvailablePeers.TryGetValue(peerId, out var peer) && peer.CertHash != null)
            {
                return IsPeerAutoAccepted(peer.CertHash);
            }
            return false;
        }

        public bool IsPeerAutoAccepted(byte[] certHash)
        {
            if (AutoAcceptUntrustedConnections) return true;
            if (certHash != null && _autoAcceptCertHashes.ContainsKey(certHash)) return true;
            if (AutoAcceptConnections && certHash != null && IsTrustedPeer(certHash)) return true;
            return false;
        }

        public IReadOnlyList<Guid> GetAutoAcceptedPeers()
        {
            var result = new HashSet<Guid>(_autoAcceptPeers.Keys);
            foreach (var kvp in AvailablePeers)
            {
                if (kvp.Value.CertHash != null && _autoAcceptCertHashes.ContainsKey(kvp.Value.CertHash))
                {
                    result.Add(kvp.Key);
                }
            }
            return result.ToList();
        }

        public TrackerScanner? TrackerScanner { get; private set; }
        public string[]? CustomTrackers { get; set; }
        public CancellationTokenSource CancellationSource { get; private set; }

        private string? LastToken;
        public async Task StartStunRequest()
        {
            _ = SendLocalLanDiscoveryAsync();
            _ = StartPingLoopAsync();

            while (!CancellationSource.IsCancellationRequested)
            {
                try
                {
                    await StunRequest();
                }
                catch (OperationCanceledException) when (CancellationSource.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!CancellationSource.IsCancellationRequested)
                        QuicPunchLog.Error("[QuicPunch] Error in STUN loop", ex);
                }

                try
                {
                    await Task.Delay(20000, CancellationSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async Task StartPingLoopAsync()
        {
            while (!CancellationSource.IsCancellationRequested)
            {
                try
                {
                    if (!AvailablePeers.IsEmpty)
                    {
                        byte[] pingReq = BuildPingPacket(Stopwatch.GetTimestamp(), false);
                        foreach (var peer in AvailablePeers.Values)
                        {
                            if (peer.ActiveEndPoint != null)
                            {
                                await udp.SendAsync(pingReq, peer.ActiveEndPoint);
                            }
                        }
                    }
                }
                catch { }

                await Task.Delay(2000, CancellationSource.Token);
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
                    try { await udp.SendAsync(payload, new IPEndPoint(IPAddress.Parse(DefaultLanDiscoveryMulticast), LanDiscoveryPort)).ConfigureAwait(false); } catch { }
                    try { await udp.SendAsync(payload, new IPEndPoint(IPAddress.Broadcast, LanDiscoveryPort)).ConfigureAwait(false); } catch { }
                    try { await udp.SendAsync(payload, new IPEndPoint(IPAddress.Loopback, LanDiscoveryPort)).ConfigureAwait(false); } catch { }
                }
            }
            catch { }
        }

        private void OnTrackerPeerDiscovered(IPEndPoint ep)
        {
            try
            {
                if (IPAddress.IsLoopback(ep.Address)) return;
                if (CurrentPeer.ActiveEndPoint != null && CurrentPeer.ActiveEndPoint.Equals(ep)) return;
                if (ep.Port == LocalDiscoveryPort && CurrentPeer.Addresses != null && CurrentPeer.Addresses.Contains(ep.Address)) return;

                bool alreadyKnown = AvailablePeers.Values.Any(p =>
                    (p.ActiveEndPoint != null && p.ActiveEndPoint.Equals(ep)) ||
                    (p.Addresses != null && p.Addresses.Contains(ep.Address) && (p.MinPort <= ep.Port && p.MaxPort >= ep.Port)));

                if (!alreadyKnown)
                {
                    var peerInfo = new PeerInfo()
                    {
                        Addresses = new[] { ep.Address },
                        MinPort = ep.Port,
                        MaxPort = ep.Port
                    };
                    _ = PeerInterrogation(peerInfo, CancellationSource.Token);
                }
            }
            catch { }
        }

        private int _consecutiveStunFailures = 0;

        public async Task<bool> RefreshStunEndpointsAsync(bool force = false)
        {
            try
            {
                var stunEndpoints = await StunGatherer.GatherStunEndpoints(forceRefresh: force, ct: CancellationSource.Token).ConfigureAwait(false);
                if (stunEndpoints.Count > 0)
                {
                    _StunServerEndpoints = stunEndpoints.ToArray();
                    if (udp != null)
                    {
                        _StunClient = new SimpleStunClient(udp, _StunServerEndpoints);
                    }
                    _consecutiveStunFailures = 0;
                    QuicPunchLog.Info($"[STUN RECOVERY] Refreshed STUN endpoints. Active server count: {_StunServerEndpoints.Length}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[STUN REFRESH] Could not refresh STUN endpoints: {ex.Message}");
            }
            return false;
        }

        public static readonly TimeSpan StunRetentionWindow = TimeSpan.FromSeconds(80);

        private async Task StunRequest(bool resetOnFailure = false)
        {
            if (_StunClient == null || _StunServerEndpoints.Length == 0)
            {
                await RefreshStunEndpointsAsync().ConfigureAwait(false);
                if (_StunClient == null || _StunServerEndpoints.Length == 0)
                {
                    if (resetOnFailure)
                    {
                        ResetNatMapping();
                    }
                    return;
                }
            }

            await _StunClient.SendRequest(CancellationSource.Token).ConfigureAwait(false);

            await Task.Delay(1000, CancellationSource.Token).ConfigureAwait(false);

            _StunClient.PruneExpiredHits(StunRetentionWindow);

            var activeHits = _StunClient.GetActiveHitsSnapshot();

            if (activeHits.Count == 0)
            {
                _consecutiveStunFailures++;
                if (_consecutiveStunFailures >= 3)
                {
                    QuicPunchLog.Info($"[STUN RESILIENCE] {_consecutiveStunFailures} consecutive STUN failures. Triggering endpoint refresh...");
                    _ = Task.Run(async () => await RefreshStunEndpointsAsync(force: true));
                }

                if (resetOnFailure || _consecutiveStunFailures >= 3)
                {
                    ResetNatMapping();
                }

                if (CurrentPeer.Addresses == null || CurrentPeer.Addresses.Length == 0)
                {
                    var localIps = Utilities.GetValidLocalIPAddresses();
                    if (localIps.Count > 0)
                    {
                        CurrentPeer.Addresses = localIps.OrderBy(Utilities.IpToUint).ToArray();
                        TrackerScanner?.SetPublicAddresses(CurrentPeer.Addresses);
                    }
                }

                if (CurrentPeer.MinPort <= 0) CurrentPeer.MinPort = LocalDiscoveryPort;
                if (CurrentPeer.MaxPort <= 0) CurrentPeer.MaxPort = LocalDiscoveryPort;

                if (resetOnFailure)
                {
                    TrackerScanner?.UpdateAnnouncement(CurrentPeer.Addresses, LocalDiscoveryPort);
                }
                return;
            }

            _consecutiveStunFailures = 0;

            CurrentPeer.NetworkType = Utilities.GetNetworkType(activeHits);
            MostUsedPort = Utilities.GetMostUsedPort(activeHits);
            if (MostUsedPort <= 0) MostUsedPort = LocalDiscoveryPort;

            var ports = activeHits.Keys.Select(k => k.Port).ToList();
            int minObservedPort = ports.Count > 0 ? ports.Min() : MostUsedPort;
            int maxObservedPort = ports.Count > 0 ? ports.Max() : MostUsedPort;

            if (minObservedPort == maxObservedPort || ports.All(p => p == MostUsedPort))
            {
                StunPortRange = (MostUsedPort, MostUsedPort);
            }
            else
            {
                StunPortRange = (Math.Clamp(minObservedPort, 1, 65535), Math.Clamp(maxObservedPort, 1, 65535));
            }

            var discoveredAddresses = activeHits.Keys
                .Select(k => k.Address)
                .Where(a => !SimpleStunClient.IsBogonOrLocalhost(a))
                .Distinct()
                .OrderBy(Utilities.IpToUint)
                .ToArray();

            if (discoveredAddresses.Length > 0)
            {
                CurrentPeer.Addresses = discoveredAddresses;
            }

            CurrentPeer.MinPort = Math.Max(1, StunPortRange.minPort);
            CurrentPeer.MaxPort = Math.Max(CurrentPeer.MinPort, StunPortRange.maxPort);

            int announcedPort = MostUsedPort > 0 ? MostUsedPort : (CurrentPeer.MinPort > 0 ? CurrentPeer.MinPort : LocalDiscoveryPort);
            TrackerScanner?.UpdateAnnouncement(CurrentPeer.Addresses, announcedPort);

            var newToken = GetToken();

            if (newToken != LastToken)
            {
                LastToken = newToken;

                QuicPunchLog.Info($"New token generated: {newToken}");
            }
        }

        public const int PunchIntervalMiliseconds = 2500 / 2;

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PNch");

        public class ExpectedPeerCertSet
        {
            private readonly ConcurrentDictionary<byte[], byte> _dict = new(Utilities.ByteArrayComparer.Instance);
            public void Add(byte[] certHash) { if (certHash != null) _dict[certHash] = 0; }
        public bool Contains(byte[] certHash) => certHash != null && _dict.ContainsKey(certHash);
            public bool Remove(byte[] certHash) => certHash != null && _dict.TryRemove(certHash, out _);
            public void Clear() => _dict.Clear();
        }

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
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                WriteLine($"[AUTO-CONNECT] Connecting via Tor to {sp.Name ?? sp.OnionAddress}...");
                                await ConnectTorAsync(sp.OnionAddress, sp.MinPort > 0 ? sp.MinPort : 443, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                WriteLine($"[AUTO-CONNECT TOR] Failed for {sp.Name ?? sp.OnionAddress}: {ex.Message}");
                            }
                        }, ct);
                    }

                    if (sp.Addresses != null && sp.Addresses.Length > 0 && sp.Addresses.Any(a => !IPAddress.IsLoopback(a)))
                    {
                        var peerInfo = CreatePeerInfoFromSavedPeer(sp);

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                WriteLine($"[AUTO-CONNECT] Interrogating WAN peer {sp.Name ?? string.Join(", ", sp.Addresses.Select(a => a.ToString()))}...");
                                await PeerInterrogation(peerInfo, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                WriteLine($"[AUTO-CONNECT WAN] Failed for {sp.Name ?? "Saved Peer"}: {ex.Message}");
                            }
                        }, ct);
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
            if (peer.CertHash != null)
            {
                ExpectedPeerCerts.Add(peer.CertHash);
                TrustPeer(peer.CertHash);
            }
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
            ExpectedPeerCerts.Remove(certHash);
            return PeerStore != null && PeerStore.Remove(certHash);
        }

        public void TrustPeer(byte[] certHash)
        {
            ArgumentNullException.ThrowIfNull(certHash);
            ExpectedPeerCerts.Add(certHash);
        }

        public void UntrustPeer(byte[] certHash)
        {
            ArgumentNullException.ThrowIfNull(certHash);
            ExpectedPeerCerts.Remove(certHash);
        }

        public bool IsTrustedPeer(byte[]? certHash) => certHash != null && ExpectedPeerCerts.Contains(certHash);
        public bool IsTrustedPeer(PeerInfo? peer) => peer?.CertHash != null && IsTrustedPeer(peer.CertHash);
        public bool IsTrustedPeer(Guid peerId) => AvailablePeers.TryGetValue(peerId, out var p) && IsTrustedPeer(p);

        public ConcurrentDictionary<Guid, PeerInfo> AvailablePeers { get; } = new();
        public ExpectedPeerCertSet ExpectedPeerCerts { get; } = new();
        public event Action<Guid, byte[]>? OnDataReceived;
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
                if (peer.ActiveEndPoint != null || peer.TorChannel != null)
                {
                    byte[] packet = BuildDisconnectPacket(peer.ActiveTransport);
                    _ = SendToPeerAsync(peer, packet, peer.ActiveTransport);
                }

                if (peer.CertHash != null)
                {
                    var keysToCancel = ActiveInterrogations
                        .Where(kv => kv.Value.Peer.CertHash != null && kv.Value.Peer.CertHash.SequenceEqual(peer.CertHash))
                        .Select(kv => kv.Key).ToList();
                    foreach (var k in keysToCancel) CancelInterrogation(k);
                }

                _ = CloseAllPeerSessionsAsync(peerId);
                peer.Dispose();

                if (AvailablePeers.IsEmpty)
                {
                    GetCertManager(peer.ActiveTransport).RenewSessionEntropy();
                }

                WriteLine($"Disconnected peer {peer.Name} ({peerId})");
                OnPeerDisconnected?.Invoke(peer);
            }
        }

        public bool RemovePeer(Guid peerId)
        {
            EnsureStarted();
            if (AvailablePeers.TryRemove(peerId, out var peer))
            {
                if (peer.CertHash != null)
                {
                    var keysToCancel = ActiveInterrogations
                        .Where(kv => kv.Value.Peer.CertHash != null && kv.Value.Peer.CertHash.SequenceEqual(peer.CertHash))
                        .Select(kv => kv.Key).ToList();
                    foreach (var k in keysToCancel) CancelInterrogation(k);
                }

                _ = CloseAllPeerSessionsAsync(peerId);
                peer.Dispose();

                if (AvailablePeers.IsEmpty)
                {
                    GetCertManager(peer.ActiveTransport).RenewSessionEntropy();
                }

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
                Channel.CreateUnbounded<(Guid, byte[])>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false })).Reader;
        }

        internal void PublishReceivedData(Guid peerId, ushort packetType, byte[] payload)
        {
            var channel = _packetChannels.GetOrAdd(packetType, _ =>
                Channel.CreateUnbounded<(Guid, byte[])>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false }));
            channel.Writer.TryWrite((peerId, payload));
        }

        public interface IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public string ProtocolName { get; }
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
        public int ActiveIncomingWorkerCount => _activeIncomingWorkers.Count;

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

        internal IncomingHandshakeSession? GetOrAddIncomingHandshakeSession(Guid guid, Guid peerId, Guid protocolId)
        {
            if (IncomingHandshakeSessions.TryGetValue(guid, out var existing))
                return existing;

            if (IncomingHandshakeSessions.Count >= MaxIncomingHandshakeSessions)
            {
                PruneExpiredIncomingHandshakeSessions();
            }

            if (IncomingHandshakeSessions.Count >= MaxIncomingHandshakeSessions)
            {
                EvictOldestIncomingHandshakeSession();
            }

            var newSession = new IncomingHandshakeSession(guid, peerId, protocolId);
            return IncomingHandshakeSessions.GetOrAdd(guid, newSession);
        }

        private void EvictOldestIncomingHandshakeSession()
        {
            Guid? oldestKey = null;
            long oldestScore = long.MaxValue;

            foreach (var kvp in IncomingHandshakeSessions)
            {
                long score = kvp.Value.CreatedTimestampMonotonic;
                if (kvp.Value.State != HandshakeSessionState.Pending)
                {
                    score -= TimeSpan.FromDays(1).Ticks;
                }

                if (score < oldestScore)
                {
                    oldestScore = score;
                    oldestKey = kvp.Key;
                }
            }

            if (oldestKey.HasValue)
            {
                IncomingHandshakeSessions.TryRemove(oldestKey.Value, out _);
            }
        }

        public int PruneExpiredIncomingHandshakeSessions()
        {
            return PruneExpiredIncomingHandshakeSessions(HandshakePendingTtl, HandshakeCompletedTtl, HandshakeRejectedTtl);
        }

        public int PruneExpiredIncomingHandshakeSessions(TimeSpan pendingTtl, TimeSpan completedTtl, TimeSpan rejectedTtl)
        {
            int pruned = 0;
            foreach (var kvp in IncomingHandshakeSessions)
            {
                if (kvp.Value.IsExpired(pendingTtl, completedTtl, rejectedTtl))
                {
                    if (IncomingHandshakeSessions.TryRemove(kvp.Key, out _))
                    {
                        pruned++;
                    }
                }
            }
            return pruned;
        }

        public void PruneExpiredIncomingHandshakeSessions(TimeSpan maxAge)
        {
            PruneExpiredIncomingHandshakeSessions(maxAge, maxAge, maxAge);
        }

        private async Task MaintenanceLoopAsync()
        {
            var token = CancellationSource.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                    PruneExpiredIncomingHandshakeSessions();
                    PruneExpiredChallenges(TimeSpan.FromSeconds(60));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    QuicPunchLog.Error("[QuicPunch] Error in maintenance loop", ex);
                }
            }
        }

        internal sealed class PendingChallenge
        {
            public byte[] Nonce { get; }
            public long CreatedTimestampMonotonic { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
            public IPEndPoint? TargetEndPoint { get; }

            public PendingChallenge(byte[] nonce, IPEndPoint? targetEndPoint = null)
            {
                Nonce = nonce;
                TargetEndPoint = targetEndPoint;
            }

            public bool IsExpired(TimeSpan timeout)
            {
                return System.Diagnostics.Stopwatch.GetElapsedTime(CreatedTimestampMonotonic) > timeout;
            }
        }

        internal readonly ConcurrentDictionary<string, PendingChallenge> _pendingChallenges = new();

        public byte[] CreatePendingChallenge(IPEndPoint? target = null)
        {
            var nonce = RandomNumberGenerator.GetBytes(24);
            var key = Convert.ToBase64String(nonce);
            _pendingChallenges[key] = new PendingChallenge(nonce, target);
            return nonce;
        }

        public bool ValidateAndConsumeChallenge(byte[] nonce)
        {
            if (nonce == null || nonce.Length != 24) return false;
            var key = Convert.ToBase64String(nonce);
            if (_pendingChallenges.TryRemove(key, out var challenge))
            {
                return !challenge.IsExpired(TimeSpan.FromSeconds(30));
            }
            return false;
        }

        public void PruneExpiredChallenges(TimeSpan maxAge)
        {
            foreach (var kvp in _pendingChallenges)
            {
                if (kvp.Value.IsExpired(maxAge))
                {
                    _pendingChallenges.TryRemove(kvp.Key, out _);
                }
            }
        }

        internal bool TryGetActiveOutboundNegotiation(Guid peerId, Guid protocolId, out OutboundNegotiation? negotiation) =>
            _activeOutboundNegotiations.TryGetValue((peerId, protocolId), out negotiation);

        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ushort>> _pendingQuicReady = new();
        private readonly ConcurrentDictionary<Guid, (ushort Port, long Generation)> _receivedQuicReady = new();

        public async Task<ushort> WaitForQuicReadyAsync(Guid connectionGuid, TimeSpan timeout, CancellationToken ct)
        {
            if (_receivedQuicReady.TryRemove(connectionGuid, out var alreadyReceived))
            {
                if (alreadyReceived.Generation == _lifecycleGeneration)
                {
                    return alreadyReceived.Port;
                }
            }

            var tcs = _pendingQuicReady.GetOrAdd(connectionGuid, _ => new TaskCompletionSource<ushort>(TaskCreationOptions.RunContinuationsAsynchronously));

            if (_receivedQuicReady.TryRemove(connectionGuid, out alreadyReceived))
            {
                _pendingQuicReady.TryRemove(connectionGuid, out _);
                if (alreadyReceived.Generation == _lifecycleGeneration)
                {
                    return alreadyReceived.Port;
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            using (timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token)))
            {
                try
                {
                    return await tcs.Task.ConfigureAwait(false);
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
            var peerId = new Guid(r.ReadBytes(16));
            var connectionGuid = new Guid(r.ReadBytes(16));
            var listeningPort = r.ReadUInt16();
            var timestamp = r.ReadInt64();
            var signature = r.ReadBytes(CertManager.SignatureLength);

            if (!AvailablePeers.TryGetValue(peerId, out var peer) || peer == null)
                return;

            if (!peer.Curve.VerifyData(buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature, HashAlgorithmName.SHA3_256))
            {
                QuicPunchLog.Info($"[QUIC READY] Received invalid signature from {remoteEndPoint}");
                return;
            }

            if (LifecycleState != QuicPunchLifecycleState.Started)
                return;

            long currentGen = _lifecycleGeneration;
            QuicPunchLog.Info($"[QUIC READY] Received QUIC_READY from peer {peer.Name} (Guid: {connectionGuid}, Port: {listeningPort})");
            _receivedQuicReady[connectionGuid] = (listeningPort, currentGen);
            if (_pendingQuicReady.TryRemove(connectionGuid, out var tcs))
            {
                _receivedQuicReady.TryRemove(connectionGuid, out _);
                tcs.TrySetResult(listeningPort);
            }
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

        public async Task<(bool Success, UdpClient? Client, IPEndPoint? remoteEndpoint)> InitUdpConnection(Guid protocolHandler, PeerInfo peer, ushort localPort = 0, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
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
            if (!ProtocolHandlers.TryGetValue(protocolHandler, out var handler))
            {
                throw new KeyNotFoundException("Handler not found for protocol: " + nameof(protocolHandler));
            }

            var flightKey = (peer.Id, protocolHandler);

            if (_activeProtocolSessions.TryGetValue(flightKey, out _))
            {
                QuicPunchLog.Info($"[QUIC INIT] Protocol session with {peer.Name} ({peer.Id}) for {handler.ProtocolName} ({protocolHandler}) is already active. Reusing existing session.");
                return;
            }

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

            var (nudp, boundPort, candidates) = await CreateBoundSocketAndGatherCandidatesAsync(localPort, $"[QUIC INIT] Gathered candidates for connection with {peer.Name}", cancellationToken).ConfigureAwait(false);
            try
            {
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
                nudp.Dispose();
                flightTcs.TrySetException(ex);
                throw;
            }
        }

        //TODO: make peer database for long term storage of peers and their info and add some way to manually add peers to it for first time connections

        public class ActiveInterrogationSession
        {
            public string Id { get; } = Guid.NewGuid().ToString();
            public PeerInfo Peer { get; }
            public DateTime StartTime { get; } = DateTime.Now;
            public CancellationTokenSource Cts { get; }

            public ActiveInterrogationSession(PeerInfo peer, CancellationTokenSource cts)
            {
                Peer = peer;
                Cts = cts;
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
            await PeerInterrogation(p, cancellationToken).ConfigureAwait(false);
        }

        public async Task PeerInterrogation(PeerInfo peer, CancellationToken cancellationToken = default)
        {
            if (peer.CertHash != null && CurrentPeer?.CertHash != null && peer.CertHash.SequenceEqual(CurrentPeer.CertHash))
            {
                return;
            }

            if (peer.CertHash != null && TorCurrentPeer?.CertHash != null && peer.CertHash.SequenceEqual(TorCurrentPeer.CertHash))
            {
                return;
            }

            bool isTorPeer = peer.NetworkType == NetworkType.Tor || (peer.Addresses == null || peer.Addresses.Length == 0 && !string.IsNullOrEmpty(peer.OnionAddress));
            bool isWanPeer = peer.Addresses != null && peer.Addresses.Length > 0;

            if (isTorPeer)
            {
                if (!IsTorStarted || TorManager == null || TorHub == null)
                {
                    throw new InvalidOperationException("Cannot connect to Tor token: Tor service is not started. Start Tor first.");
                }

                if (peer.CertHash != null)
                {
                    ExpectedPeerCerts.Add(peer.CertHash);
                    TrustPeer(peer.CertHash);
                }

                if (!string.IsNullOrEmpty(peer.OnionAddress))
                {
                    await ConnectTorAsync(peer.OnionAddress, peer.MinPort > 0 ? peer.MinPort : 443, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (isWanPeer)
            {
                if (!IsStarted || udp == null)
                {
                    throw new InvalidOperationException("Cannot connect to WAN token: WAN/UDP transport is not active. Start WAN service first.");
                }

                if (peer.CertHash != null)
                {
                    ExpectedPeerCerts.Add(peer.CertHash);
                    TrustPeer(peer.CertHash);
                }

                var lcts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken);
                var session = new ActiveInterrogationSession(peer, lcts);

                var existingKeys = ActiveInterrogations
                    .Where(kv => kv.Value.Peer.CertHash != null && peer.CertHash != null && kv.Value.Peer.CertHash.SequenceEqual(peer.CertHash))
                    .Select(kv => kv.Key).ToList();
                foreach (var k in existingKeys)
                {
                    CancelInterrogation(k);
                }

                ActiveInterrogations[session.Id] = session;

                WriteLine($"Starting interrogation for {string.Join(", ", peer.Addresses)}...");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await SendLoopAsync(udp!, peer, lcts.Token);
                    }
                    finally
                    {
                        ActiveInterrogations.TryRemove(session.Id, out _);
                        lcts.Dispose();
                    }
                });
            }
        }

        public async Task ProcessIncomingPacketAsync(byte[] buffer, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel = null)
        {
            if (LifecycleState == QuicPunchLifecycleState.Disposed || LifecycleState == QuicPunchLifecycleState.Stopped || CancellationSource == null || CancellationSource.IsCancellationRequested)
                return;

            if (buffer == null || buffer.Length < MagicHeader.Length + 1)
                return;

            for (int i = 0; i < MagicHeader.Length; i++)
            {
                if (buffer[i] != MagicHeader[i])
                    return;
            }

            using (MemoryStream ms = new MemoryStream(buffer))
            using (BinaryReader r = new BinaryReader(ms))
            {
                ms.Position = MagicHeader.Length;
                byte messageType = r.ReadByte();

                switch (messageType)
                {
                    case (byte)MessageType.Interrogation:
                    case (byte)MessageType.Hello:
                        HelloHandler.HandleHello(this, r, udp, remoteEndPoint, buffer, messageType, transport, torChannel);
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

                    case (byte)MessageType.Data:
                        var span = buffer.AsSpan();
                        const int TagSize = 16; 
                        int headerAadSize = MagicHeader.Length + sizeof(byte) + sizeof(ushort) + 16 + sizeof(ulong);

                        int minimumLength = headerAadSize + TagSize;

                        if (span.Length < minimumLength)
                        {
                            return;
                        }

                        int offset = 0;

                        offset += MagicHeader.Length;
                        offset++;

                        ushort packetType = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, sizeof(ushort)));
                        offset += sizeof(ushort);

                        ReadOnlySpan<byte> senderId = span.Slice(offset, 16);
                        var peerId = new Guid(senderId);

                        if (!this.AvailablePeers.TryGetValue(peerId, out var peer) || peer.RxCipher == null || peer.RxSalt == null)
                        {
                            return;
                        }

                        offset += 16;

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

                        PublishReceivedData(peerId, packetType, plaintext);
                        break;

                    default:
                        WriteLine($"Received unknown message type {(char)messageType} from {remoteEndPoint}");
                        break;
                }
            }
        }

        private async Task ReceiveUdpLoopAsync(UdpClient socket, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !CancellationSource.IsCancellationRequested)
            {
                try
                {
                    var result = await socket.ReceiveAsync(ct).ConfigureAwait(false);

                    if (_StunClient != null && _StunClient.TryProcessIncoming(result.Buffer, result.RemoteEndPoint))
                    {
                        continue;
                    }

                    if (!_rateLimiter.IsAllowed(Utilities.IpToUint(result.RemoteEndPoint.Address)))
                        continue;

                    await ProcessIncomingPacketAsync(result.Buffer, result.RemoteEndPoint, TransportType.Wan).ConfigureAwait(false);
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
                    if (ct.IsCancellationRequested || CancellationSource.IsCancellationRequested ||
                        sex.SocketErrorCode == SocketError.OperationAborted ||
                        sex.SocketErrorCode == SocketError.Interrupted ||
                        sex.SocketErrorCode == SocketError.InvalidArgument)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || CancellationSource.IsCancellationRequested)
                        break;
                    QuicPunchLog.Error($"[ReceiveUdpLoopAsync] Error processing packet: {ex.Message}", ex);
                }
            }
        }

        private async Task ReceiveTorConnectionLoopAsync(TorQuicConnectionManager channel)
        {
            try
            {
                string onionHost = !string.IsNullOrEmpty(channel.RemoteOnion) ? channel.RemoteOnion : "127.0.0.1";
                var endPoint = channel.RemoteEndPoint ?? new DnsEndPoint(onionHost, channel.RemoteVirtualPort > 0 ? channel.RemoteVirtualPort : 443);
                while (!CancellationSource.IsCancellationRequested && !channel.IsClosed)
                {
                    try
                    {
                        var messageMemory = await channel.ReceiveMessageAsync(CancellationSource.Token).ConfigureAwait(false);
                        byte[] message = messageMemory.ToArray();
                        if (message.Length > 0)
                        {
                            await ProcessIncomingPacketAsync(message, endPoint, TransportType.Tor, channel).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        if (channel.IsClosed || CancellationSource.IsCancellationRequested)
                            break;
                        QuicPunchLog.Error("Error processing packet in ReceiveTorConnectionLoopAsync", ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                QuicPunchLog.Error("Error in ReceiveTorConnectionLoopAsync", ex);
            }
        }

        private async Task AcceptTorLoopAsync(CancellationToken token)
        {
            if (TorHub == null) return;
            QuicPunchLog.Info($"[AcceptTorLoopAsync] Starting loop for node {CurrentPeer.Name}...");
            try
            {
                while (!token.IsCancellationRequested && !CancellationSource.IsCancellationRequested)
                {
                    var channel = await TorQuicConnectionManager.AcceptAsync(TorHub, cancellationToken: token).ConfigureAwait(false);
                    QuicPunchLog.Info($"[ACCEPT TOR LOOP] Accepted incoming Tor channel {channel.ConnectionId} from {channel.RemoteOnion}");
                    _ = Task.Run(() => ReceiveTorConnectionLoopAsync(channel), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested && !CancellationSource.IsCancellationRequested)
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
                    bool peerResponded = AvailablePeers.TryGetValue(peer.Id, out PeerInfo availablePeer);

                    var helloPayload = GenerateHelloPayload(MessageType.Hello, includePassword, targetPeer: availablePeer ?? peer);

                    if (peerResponded)
                    {
                        await udp.SendAsync(helloPayload, availablePeer.ActiveEndPoint);
                    }
                    else
                    {
                        var payload = GenerateHelloPayload(MessageType.Interrogation, true, targetPeer: peer);
                        await udp.BigSendAsync(payload, peer);
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
                catch (Exception ex)
                {
                    QuicPunchLog.Error("[QuicPunch] Error in periodic hello loop", ex);
                    await Task.Delay(250, token);
                }
            }
        }

        public async ValueTask SendPayloadAsync(PeerInfo peer, ushort packetType, ReadOnlyMemory<byte> payload)
        {
            EnsureStarted();
            if (peer.TxCipher == null || peer.TxSalt == null)
                throw new InvalidOperationException("Peer cipher is not initialized.");

            var currentPeer = GetCurrentPeer(peer.ActiveTransport);

            const int TagSize = 16;
            int headerAadSize = MagicHeader.Length + sizeof(byte) + sizeof(ushort) + 16 + sizeof(ulong);

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

                currentPeer.IdRaw.CopyTo(span[offset..]);
                offset += 16;

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
                    await udp.BigSendAsync(packet.AsMemory(0, packetLength), peer)
                             .ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
        public string GetToken() => GetWanToken();

        public string GetWanToken() => 
            Utilities.EncodeEndpointToken(CurrentPeer);

        public string? GetTorToken()
        {
            if (string.IsNullOrEmpty(TorCurrentPeer?.OnionAddress))
                return null;
            return Utilities.EncodeEndpointToken(TorCurrentPeer);
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
