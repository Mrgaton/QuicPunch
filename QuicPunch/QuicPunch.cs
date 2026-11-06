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
    public partial class QuicPunch : IDisposable, IAsyncDisposable, IPeerDiscoveryContext
    {  
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

        public static string AppDataPath { get; set; } = ResolveDefaultAppDataPath();

        private static string ResolveDefaultAppDataPath()
        {
            string baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            }
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                baseFolder = Path.GetTempPath();
            }
            return Path.Combine(baseFolder, "QuicPunchV17");
        }
        public string NodeAppDataPath { get; set; }

        public PeerInfo CurrentPeer { get; private set; } = null!;

        public NatPinCoordinator NatCoordinator { get; }
        public IReadOnlyList<IPEndPoint> StunServerEndpoints
        {
            get => NatCoordinator.Servers;
            set => NatCoordinator.SetServerEndpoints(value ?? Array.Empty<IPEndPoint>());
        }

        public PeerStore? PeerStore { get; private set; }

        // Human consent is independent of transport/heartbeat deadlines.
        public static readonly TimeSpan SessionConsentTimeout = TimeSpan.FromMinutes(3);

        public async Task<bool> AuthorizeIncomingSessionAsync(PeerInfo peer, Guid protocolId, TransportType transport, CancellationToken ct)
        {
            if (!IsTrustedPeer(peer) || !peer.TryGetCertificateHash(out var certHash)) return false;
            if (ProtocolHandlers.TryGetValue(protocolId, out var policyHandler) && policyHandler.IncomingSessionPolicy(peer) == false) return false;
            foreach (Action<PeerInfo, Guid> observer in OnIncomingSessionRequested?.GetInvocationList() ?? [])
                try { observer(peer, protocolId); } catch (Exception ex) { QuicPunchLog.Info("[SESSION] Incoming observer failed: " + ex.Message); }
            if (HasAutomaticProtocolPermission(peer, protocolId)) return true;
            var endpoint = peer.ActiveEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
            var request = new HandshakeRequest(Guid.NewGuid(), protocolId, endpoint, peer.Id, certHash, transport);
            var decision = await Manager.WaitForDecisionAsync(request, SessionConsentTimeout, true, ct).ConfigureAwait(false);
            return decision.Accepted;
        }

        internal bool HasAutomaticProtocolPermission(PeerInfo peer, Guid protocolId)
        {
            if (!IsTrustedPeer(peer) || !peer.TryGetCertificateHash(out var hash)) return false;
            if (ProtocolHandlers.TryGetValue(protocolId, out var handler))
            {
                if (handler.IncomingSessionPolicy(peer) is bool automatic) return automatic;
                // A VPN's explicit per-peer policy overrides older global grants, including administrator profiles.
                if (handler.UsesOwnSessionPermissions) return false;
            }
            return AutomaticPermissionForProtocol(protocolId) == "chat" || PeerStore?.IsAdministrator(hash) == true ||
                PeerStore?.HasAutomaticPermission(hash, AutomaticPermissionForProtocol(protocolId)) == true;
        }

        internal static string AutomaticPermissionForProtocol(Guid protocolId) =>
            protocolId.ToString("D") switch
            {
                "00000000-0000-0000-0000-000000000001" => "chat",
                "00000000-0000-0000-0000-000000000002" => "vpn",
                "00000000-0000-0000-0000-000000000003" => "voice",
                "00000000-0000-0000-0000-000000000006" => "speedtest",
                "00000000-0000-0000-0000-000000000007" or "00000000-0000-0000-0000-000000000008" => "watch",
                "00000000-0000-0000-0000-000000000009" => "virtual-disk",
                _ => ""
            };

        public readonly HandshakeManager _manager = new HandshakeManager();
        public HandshakeManager Manager => _manager;
        public event Action<PeerInfo, Guid>? OnIncomingSessionRequested;

        private readonly IpRateLimiter _rateLimiter = new IpRateLimiter(500);
        public IpReputationFilter? ReputationFilter { get; set; }

        public readonly ConcurrentDictionary<Guid, IProtocolHandler> ProtocolHandlers = new();

        public bool EnableUpnp { get; set; } = true;
        public bool IsUpnpMapped { get; private set; }
        public string? ActivePortMappingProtocol { get; private set; }
        public PortMappingCoordinator? PortCoordinator => _controlMapping?.Mapper;
        public UpnpPortMapper? UpnpMapper => _controlMapping?.Mapper.UpnpMapper;
        private Task? _upnpMappingTask;

        private int LocalPort;

        private void ResetNatMapping()
        {
            NatCoordinator.ResetMapping();
            if (CurrentPeer != null)
            {
                CurrentPeer.PortArray = LocalDiscoveryPort > 0 ? [(ushort)LocalDiscoveryPort] : Array.Empty<ushort>();
                CurrentPeer.PreferredPort = LocalDiscoveryPort is > 0 and <= ushort.MaxValue
                    ? (ushort)LocalDiscoveryPort
                    : (ushort)0;
                CurrentPeer.ConnectionFlags.PortMode = PortMode.Single;
                CurrentPeer.ConnectionFlags.ExternalPortMayDiffer = false;
            }
            InvalidateTokenCache();
        }

        public CertManager CertManager { get; private set; } = null!;
        public CertManager TorCertManager { get; private set; } = null!;

        public string? TorOnionAddress { get; private set; }
        public TorIdentity? TorIdentity { get; private set; }
        public TorPeerTransportHub? TorHub { get; private set; }
        public TorManager? TorManager { get; private set; }
        private bool _ownsTorManager;
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

        public QuicPunch(CancellationTokenSource? cts, byte[]? discoveryId, byte[]? connectionPassword, ushort listeningPort = 0, string? appDataPath = null)
            : this(cts?.Token ?? default, discoveryId, connectionPassword, listeningPort, appDataPath)
        {
        }

        public QuicPunch(CancellationToken cancellationToken = default, byte[]? discoveryId = null, byte[]? connectionPassword = null, int listeningPort = 0, string? appDataPath = null)
        {
            if (!QuicConnection.IsSupported && !OperatingSystem.IsAndroid())
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
            Discovery = new PeerDiscoveryManager(this);
            byte[] mailboxStoreKey = HMACSHA256.HashData(CertManager.NostrPrivateKey, "quicpunch-mailbox-store-v1"u8);
            try
            {
                MailboxStore = new SqliteMailboxStore(Path.Combine(NodeAppDataPath, "nostr_mailbox.db"), mailboxStoreKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(mailboxStoreKey);
            }
            Mailbox = new NostrMailbox(CertManager, Path.Combine(NodeAppDataPath, "nostr_mailbox.state"), proxyProvider: () => !WanEnabled && !IsTorStarted ? throw new InvalidOperationException("Tor must be ready before mailbox connections can start while WAN is disabled.") : GetTorSocksProxy());
            Mailbox.OnTypedMessageReceived += HandleMailboxDirectMessage;
            Mailbox.OnGlobalMessageReceived += HandleMailboxGlobalMessage;

            if (discoveryId != null)
            {
                _poolId = discoveryId.Length == 20 ? discoveryId : SHA1.HashData(discoveryId);
            }

            _connectionPassword = connectionPassword;
            if (_connectionPassword != null)
            {
                DerivePasswordHash();
            }

            AccessController = new PeerAccessController(
                peerStoreProvider: () => PeerStore,
                peerByIdResolver: id => AvailablePeers.TryGetValue(id, out var p) ? p : null);

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

            ReputationFilter = new IpReputationFilter(
                level: 2,
                cacheFilePath: Path.Combine(NodeAppDataPath, "ipsum.cache"),
                autoUpdateInterval: TimeSpan.FromHours(24),
                autoLoadCache: true,
                autoStart: true);
        }

        public PeerInfo TorCurrentPeer { get; private set; } = null!;

        public PeerInfo GetLocalIdentityForPeer(PeerInfo peer)
        {
            ArgumentNullException.ThrowIfNull(peer);
            return GetCurrentPeer(peer.ActiveTransport == TransportType.Tor || peer.NetworkType == NetworkType.Tor ||
                !string.IsNullOrWhiteSpace(peer.OnionAddress) ? TransportType.Tor : TransportType.Wan);
        }


        public CertManager GetCertManager(TransportType transport = TransportType.Wan) =>
            transport == TransportType.Tor ? TorCertManager : CertManager;

        public PeerInfo GetCurrentPeer(TransportType transport = TransportType.Wan) =>
            (transport == TransportType.Tor && TorCurrentPeer != null) ? TorCurrentPeer : CurrentPeer;

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
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await udp.SendAsync(payload, ipEp).ConfigureAwait(false);
                        break;
                    }
                    catch (SocketException ex) when (attempt < 2 && ex.SocketErrorCode is SocketError.AccessDenied or SocketError.NetworkUnreachable or SocketError.HostUnreachable)
                    {
                        QuicPunchLog.Info($"[UDP CONTROL] Send to {ipEp} returned {ex.SocketErrorCode}; retrying.");
                        await Task.Delay(25 * (attempt + 1)).ConfigureAwait(false);
                    }
                }
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
            peerInfo.PortArray = [(ushort)remotePort];
            peerInfo.ConnectionFlags.PortMode = PortMode.Single;
            peerInfo.ConnectionFlags.IsTor = true;
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
        private static readonly byte[] DefaultPasswordSalt = Sha3Crypto.Hash256(Encoding.UTF8.GetBytes("QuicPunch-P2P-Default-Password-Salt-v1"));

        internal void DerivePasswordHash()
        {
            if (_connectionPassword == null)
            {
                PasswordHash = null;
                return;
            }

            byte[] salt = (_poolId != null && _poolId.Length > 0)
                ? Sha3Crypto.Hash256(_poolId)
                : DefaultPasswordSalt;

            PasswordHash = Sha3Crypto.Pbkdf2Sha3_512(_connectionPassword, salt, 100_000, 64);
        }

        private byte[] _poolId = [];
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
                    if (Discovery.WanNostrDiscoveryEnabled && Discovery.WanNostrDiscovery != null)
                        _ = Discovery.RestartWanNostrDiscoveryAsync(LifecycleToken);
                    if (Discovery.TorNostrDiscoveryEnabled && Discovery.TorNostrDiscovery != null)
                        _ = Discovery.RestartTorNostrDiscoveryAsync(LifecycleToken);
                }
            }
        }
        internal byte[]? PasswordHash { get; set; }
        public PeerAccessController AccessController { get; }

        public bool SharePeers { get; set; }
        public bool AcceptSharedPeers { get; set; }


        public PeerDiscoveryManager Discovery { get; }
        public NostrMailbox Mailbox { get; }
        public IMailboxStore MailboxStore { get; }

        private const int MaxConcurrentDiscoveryInterrogations = 8;
        private readonly SemaphoreSlim _discoveryInterrogationSlots = new(MaxConcurrentDiscoveryInterrogations, MaxConcurrentDiscoveryInterrogations);
        public CancellationTokenSource CancellationSource { get; private set; }

        #region IPeerDiscoveryContext Implementation

        bool IPeerDiscoveryContext.IsStarted => IsStarted;
        bool IPeerDiscoveryContext.IsTorStarted => IsTorStarted;
        CancellationToken IPeerDiscoveryContext.LifecycleToken => LifecycleToken;
        byte[] IPeerDiscoveryContext.PoolId => _poolId;
        public byte[]? LocalWanCertHash => CurrentPeer?.TryGetCertificateHash(out var h) == true ? h : null;
        public byte[]? LocalTorCertHash => (TorCurrentPeer != null && TorCurrentPeer.TryGetCertificateHash(out var th)) ? th : null;
        byte[]? IPeerDiscoveryContext.LocalWanCertHash => LocalWanCertHash;
        byte[]? IPeerDiscoveryContext.LocalTorCertHash => LocalTorCertHash;
        byte[]? IPeerDiscoveryContext.WanCertPublicKey => CertManager?.PeerCertificate?.GetPublicKey();
        byte[]? IPeerDiscoveryContext.TorCertPublicKey => TorCertManager?.PeerCertificate?.GetPublicKey();
        byte[]? IPeerDiscoveryContext.WanNostrPrivateKey => CertManager?.NostrPrivateKey;
        byte[]? IPeerDiscoveryContext.TorNostrPrivateKey => TorCertManager?.NostrPrivateKey;
        public string? WanNostrPublicKeyHex => CertManager?.NostrPublicKeyHex;
        public string? TorNostrPublicKeyHex => TorCertManager?.NostrPublicKeyHex;
        string? IPeerDiscoveryContext.WanNostrPublicKeyHex => WanNostrPublicKeyHex;
        string? IPeerDiscoveryContext.TorNostrPublicKeyHex => TorNostrPublicKeyHex;
        int IPeerDiscoveryContext.LocalBoundPort => LocalBoundPort;
        X509Certificate2? IPeerDiscoveryContext.WanCertificate => CertManager?.PeerCertificate;
        X509Certificate2? IPeerDiscoveryContext.TorCertificate => TorCertManager?.PeerCertificate;
        string? IPeerDiscoveryContext.GetWanToken() => GetWanToken();
        string? IPeerDiscoveryContext.GetTorToken() => GetTorToken();
        IWebProxy? IPeerDiscoveryContext.GetTorSocksProxy() => GetTorSocksProxy();
        PeerStore? IPeerDiscoveryContext.PeerStore => PeerStore;
        bool IPeerDiscoveryContext.IsTrustedPeer(byte[] certHash) => IsTrustedPeer(certHash);
        bool IPeerDiscoveryContext.TryGetActivePeer(byte[] certHash, out PeerInfo? peer) => TryGetPeerByCertHash(certHash, out peer);
        IReadOnlyList<ActiveInterrogationSession> IPeerDiscoveryContext.GetActiveInterrogations() => ActiveInterrogations.Values.ToList();
        void IPeerDiscoveryContext.CancelInterrogation(string sessionId) => CancelInterrogation(sessionId);
        Task IPeerDiscoveryContext.InterrogatePeerAsync(PeerInfo peer, CancellationToken cancellationToken) => PeerInterrogation(peer, cancellationToken);
        bool IPeerDiscoveryContext.QueueSavedPeerReconnect(PeerInfo peer, bool restartProbe) => QueueSavedPeerReconnect(peer, restartProbe);
        Task IPeerDiscoveryContext.ConnectTorAsync(string onionAddress, int port, CancellationToken cancellationToken) => ConnectTorAsync(onionAddress, port, cancellationToken);
        byte[] IPeerDiscoveryContext.GenerateLanBeaconPayload() => PacketBuilder.GenerateHelloPayload(this, MessageType.Interrogation, true, lanBeacon: true);
        Task IPeerDiscoveryContext.ProcessIncomingLanPacketAsync(byte[] buffer, int length, EndPoint remoteEndPoint) =>
            ProcessIncomingPacketAsync(buffer, length, remoteEndPoint, TransportType.Wan, isLanDiscovery: true);
        void IPeerDiscoveryContext.ConfigureUdpSocket(UdpClient client) => ConfigureUdpSocket(client);
        UdpClient? IPeerDiscoveryContext.WanUdpClient => udp;
        bool IPeerDiscoveryContext.AutoConnectDiscoveredPeers => PeerTransport.AutoConnectDiscoveredPeers;
        PeerInfo? IPeerDiscoveryContext.LocalWanPeer => CurrentPeer;
        PeerInfo? IPeerDiscoveryContext.LocalTorPeer => TorCurrentPeer;

        #endregion

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
                        foreach (var peer in AvailablePeers.Values)
                        {
                            // If peer has an active QUIC multiplexer tunnel, use native QUIC transport ping & RTT measurement
                            bool hasActiveMux = TryGetPeerMultiplexer(peer.Id, out var mux) && mux != null && !mux.IsDisposed;
                            if (hasActiveMux)
                            {
                                mux!.TrySendTransportPing();
                                RefreshSavedConnectionEndpoint(peer);
                                if (peer.LastPingResponseUtc is { } responseUtc) RecordAuthenticatedPeerActivity(peer, responseUtc);
                                if (mux.TryGetTelemetry(out var qTelem))
                                {
                                    peer.LastTelemetry = qTelem;
                                    peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(qTelem.RttMs, 1)));

                                }
                            }

                            if (hasActiveMux && !mux!.IsDisposed) continue;

                            // Prune peers that have had no response for more than 1 minute (60 seconds)
                            if (!peer.IsAvailableAndResponsive(TimeSpan.FromMinutes(1)))
                            {
                                QuicPunchLog.Info($"[PEER TIMEOUT] Peer {peer.Name} ({peer.Id}) has not responded for > 60s. Removing from available peers (no active QUIC tunnel).");
                                DisconnectPeer(peer.Id);
                                continue;
                            }

                            if (System.Diagnostics.Stopwatch.GetElapsedTime(peer.LastActivityTimestampMonotonic) < TimeSpan.FromSeconds(20)
                                || (peer.LastAvailabilityProbeTimestamp != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(peer.LastAvailabilityProbeTimestamp) < TimeSpan.FromSeconds(20)))
                                continue;
                            peer.LastAvailabilityProbeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                            var availabilityTransport = peer.ActiveTransport == TransportType.Tor || (peer.TorChannel != null && !peer.TorChannel.IsClosed)
                                ? TransportType.Tor : TransportType.Wan;
                            var availabilityProbe = GenerateHelloPayload(QuicPunchStructures.MessageType.Interrogation, true, transport: availabilityTransport, targetPeer: peer);

                            if (peer.ActiveTransport == TransportType.Tor || (peer.TorChannel != null && !peer.TorChannel.IsClosed))
                            {
                                if (IsTorStarted && peer.TorChannel != null && !peer.TorChannel.IsClosed)
                                {
                                    try
                                    {
                                        await peer.TorChannel.SendMessageAsync(availabilityProbe, token).ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        QuicPunchLog.Info($"[TOR AVAILABILITY] Notice: signed probe to {peer.Name} failed: {ex.Message}");
                                    }
                                }
                            }
                            else if (udp != null && peer.ActiveEndPoint != null)
                            {
                                await udp.SendAsync(availabilityProbe, peer.ActiveEndPoint, token).ConfigureAwait(false);
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

        public Task SendLocalLanDiscoveryAsync() =>
            Discovery.SendLocalLanDiscoveryAsync();

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
            ushort localPreferredPort = LocalDiscoveryPort is > 0 and <= ushort.MaxValue
                ? (ushort)LocalDiscoveryPort
                : (ushort)0;
            if (CurrentPeer.PreferredPort != localPreferredPort)
            {
                CurrentPeer.PreferredPort = localPreferredPort;
                endpointsChanged = true;
            }
            var router = _advertisedMapping is { Success: true } r && r.InternalPort == LocalDiscoveryPort ? r : null;
            // A successful mapping on a publicly addressed router is an exact,
            // renewed port lease. Random STUN mappings must not dilute it into a
            // range. A private/CGNAT gateway does not prove external reachability.
            var publicRouter = router?.ExternalIp is { AddressFamily: AddressFamily.InterNetwork } routerIp &&
                !SimpleStunClient.IsBogonOrLocalhost(routerIp) ? router : null;
            var localIps = Utilities.GetValidLocalIPAddresses();
            if (router?.ExternalIp is { AddressFamily: AddressFamily.InterNetwork } externalIp) localIps.Add(externalIp);
            var allAddresses = (publicRouter?.ExternalIp is { } mappedAddress
                    ? new[] { mappedAddress } : mapping.DiscoveredAddresses ?? Array.Empty<IPAddress>())
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

            // WAN advertisements describe externally reachable ports. The local
            // control port is advertised separately by LAN beacons and must not
            // widen a public NAT range when it differs from the mapped port.
            var advertisedPorts = publicRouter is { ExternalPort: > 0 and <= 65535 }
                ? [(ushort)publicRouter.ExternalPort]
                : PortAdvertisementCodec.Normalize(mapping.PortArray);
            if (advertisedPorts.Length == 0 && LocalDiscoveryPort > 0)
                advertisedPorts = [(ushort)LocalDiscoveryPort];

            if (advertisedPorts.Length > 0)
            {
                PortMode advertisedMode = publicRouter != null ? PortMode.Single : PortAdvertisementCodec.ResolveMode(
                    advertisedPorts,
                    mapping.ConnectionFlags.PortMode);
                advertisedPorts = PortAdvertisementCodec.NormalizeForMode(advertisedPorts, advertisedMode);
                if (CurrentPeer.PortArray == null || !CurrentPeer.PortArray.SequenceEqual(advertisedPorts))
                {
                    CurrentPeer.PortArray = advertisedPorts;
                    endpointsChanged = true;
                }

                if (CurrentPeer.ConnectionFlags.PortMode != advertisedMode)
                {
                    CurrentPeer.ConnectionFlags.PortMode = advertisedMode;
                    endpointsChanged = true;
                }
            }

            bool multipleIps = allAddresses.Length > 1;
            bool isNat = mapping.ConnectionFlags.IsNat || router != null;
            bool externalPortMayDiffer = CurrentPeer.ConnectionFlags.PortMode != PortMode.Single ||
                CurrentPeer.PortArray.Any(port => LocalDiscoveryPort > 0 && port != LocalDiscoveryPort);
            if (CurrentPeer.ConnectionFlags.ExternalPortMayDiffer != externalPortMayDiffer)
            {
                CurrentPeer.ConnectionFlags.ExternalPortMayDiffer = externalPortMayDiffer;
                endpointsChanged = true;
            }
            if (CurrentPeer.ConnectionFlags.MultipleIps != multipleIps || CurrentPeer.ConnectionFlags.IsNat != isNat)
            {
                CurrentPeer.ConnectionFlags.MultipleIps = multipleIps;
                CurrentPeer.ConnectionFlags.IsNat = isNat;
                endpointsChanged = true;
            }

            bool dynamicPorts = CurrentPeer.ConnectionFlags.PortMode != PortMode.Single;
            NetworkType advertisedNetworkType = multipleIps
                ? (dynamicPorts ? NetworkType.DynamicPortAndAddress : NetworkType.DynamicAddress)
                : (dynamicPorts ? NetworkType.DynamicPort : NetworkType.Static);
            if (CurrentPeer.NetworkType != advertisedNetworkType)
            {
                CurrentPeer.NetworkType = advertisedNetworkType;
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
                    _ = Discovery.PublishNostrDiscoveryAsync(LifecycleToken);
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

        public static byte[] MagicHeader = Encoding.UTF8.GetBytes("PNci");


        public async Task AutoConnectSavedPeersAsync(CancellationToken ct = default)
        {
            if (PeerStore == null) return;

            var saved = PeerStore.SavedPeers.Where(IsAutomaticContactEligible).ToList();
            if (saved.Count == 0) return;

            WriteLine($"[AUTO-CONNECT] Initiating background connection for {saved.Count} saved peer(s)...");

            await Parallel.ForEachAsync(saved, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (sp, token) =>
            {
                try
                {
                    if (sp.CertHash != null)
                    {
                        ExpectedPeerCerts.Add(sp.CertHash);
                        if (sp.Trusted) TrustPeer(sp.CertHash);
                    }

                    if (!string.IsNullOrEmpty(sp.OnionAddress) && IsTorStarted)
                    {
                        try
                        {
                            WriteLine($"[AUTO-CONNECT] Connecting via Tor to {sp.Name ?? sp.OnionAddress}...");
                            await ConnectTorAsync(sp.OnionAddress, sp.PortArray.Length > 0 ? sp.PortArray[0] : (ushort)443, token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            WriteLine($"[AUTO-CONNECT TOR] Failed for {sp.Name ?? sp.OnionAddress}: {ex.Message}");
                        }
                    }

                    // Persistent WAN probes are owned by AutomaticContactProbeLoopAsync.
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    WriteLine($"[AUTO-CONNECT] Error for saved peer: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        public static PeerInfo CreatePeerInfoFromSavedPeer(PeerStore.SavedPeer sp)
        {
            var netType = sp.NetworkType;
            bool hasMultiplePorts = sp.PortArray.Length > 1;
            if (netType == NetworkType.Unknown || netType == NetworkType.Static)
            {
                if (sp.Addresses.Length > 1 && hasMultiplePorts)
                    netType = NetworkType.DynamicPortAndAddress;
                else if (sp.Addresses.Length > 1)
                    netType = NetworkType.DynamicAddress;
                else if (hasMultiplePorts)
                    netType = NetworkType.DynamicPort;
                else
                    netType = NetworkType.Static;
            }

            return new PeerInfo
            {
                Name = sp.Name ?? "Saved Peer",
                NetworkType = netType,
                Addresses = sp.Addresses,
                PortArray = sp.PortArray,
                PreferredPort = sp.PreferredPort,
                ConnectionFlags = new ConnectionFlags(sp.ConnectionFlags.RawValue),
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

            bool saved = PeerStore != null && PeerStore.AddOrUpdate(peer, autoConnect);
            if (saved && TryGetPeerMultiplexer(peer.Id, out var mux) && mux is { IsDisposed: false })
                PeerStore?.RecordConnection(certHash, peer.ActiveTransport == TransportType.Tor ? null : peer.QuicEndPoint?.Address ?? peer.ActiveEndPoint?.Address);
            return saved;
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
        public void UntrustPeer(byte[] certHash)
        {
            try { AccessController.UntrustPeer(certHash); }
            finally
            {
                if (TryGetPeerByCertHash(certHash, out var peer) && peer != null && !IsTrustedPeer(peer))
                {
                    RevokeRecovery(peer.Id);
                    if (TryGetPeerMultiplexer(peer.Id, out var mux) && mux != null) _ = mux.RevokeApplicationSessionsAsync();
                }
            }
        }
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
                    bool portsSame = (saved.PortArray ?? []).SequenceEqual(peer.PortArray ?? []);
                    bool nameSame = string.Equals(saved.Name, peer.Name, StringComparison.Ordinal);

                    if (!addrsSame || !portsSame || !nameSame || saved.ConnectionFlags.RawValue != peer.ConnectionFlags.RawValue)
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
                }
                return added;
            }
        }

        internal bool TryAdmitAuthenticatedPeer(PeerInfo peer)
        {
            if (!peer.HasCertificate || !peer.IsSessionReady)
                throw new ArgumentException("An authenticated Hello session is required.", nameof(peer));

            lock (_availablePeerAdmissionLock)
            {
                if (!AvailablePeers.TryGetValue(peer.Id, out var existing))
                    return TryAddAvailablePeer(peer.Id, peer);
                if (existing.HasCertificate)
                    return false;

                // Mailbox messages can create a saved identity before its UDP Hello.
                // Replace that placeholder only after validating the full Hello and
                // deriving its session. Keep the registered mailbox identity.
                peer.NostrPubKey ??= existing.NostrPubKey;
                if (!AvailablePeers.TryUpdate(peer.Id, peer, existing))
                    return false;
                IndexPeerCertHash(peer);
                if (ReferenceEquals(existing.TorChannel, peer.TorChannel)) existing.TorChannel = null;
                existing.Dispose();
                return true;
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
            if (!peer.IsAvailableAndResponsive(TimeSpan.FromMinutes(1)))
                return false;

            if (IsTrustedPeer(peer))
                return true;
            if (peer.TryGetCertificateHash(out var hash) && PeerStore != null && PeerStore.TryGet(hash, out _))
                return true;
            if (_activeProtocolSessions.Keys.Any(key => key.PeerId == peerId))
                return true;
            if (_peerMultiplexers.ContainsKey(peerId))
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

        public interface IProtocolHandler
        {
            public Guid ProtocolId { get; }
            public string ProtocolName { get; }
            public ushort StreamPriority => (ushort)Helpers.QuicStreamPriority.Normal;
            /// <summary>False denies a session; true allows automatic acceptance; null uses the normal local permission workflow.</summary>
            bool? IncomingSessionPolicy(PeerInfo peer) => null;
            /// <summary>When true, a null policy asks locally instead of inheriting stored automatic grants.</summary>
            bool UsesOwnSessionPermissions => false;
            /// <summary>Whether this host can start the protocol's resources; this does not grant session consent.</summary>
            bool CanStartSession => true;
            /// <summary>Allows re-entering HandleAsync with the same logical SessionId after transport loss.
            /// The handler must restore its application state/checkpoints; raw bytes are never replayed.</summary>
            bool SupportsSessionRecovery => false;
            Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct);
            Task DeniedAsync(PeerInfo peer, CancellationToken ct);
            /// <summary>Provides the local identity for the remote peer's actual transport.</summary>
            void OnLocalIdentityProviderAttached(Func<PeerInfo, PeerInfo> provider) { }
            void OnDatagramReceived(QuicConnection connection, PeerInfo peer, Datagrams.QuicDatagramMessage datagram) { }
            void OnFrameReceived(QuicConnection connection, PeerInfo peer, byte category, ReadOnlyMemory<byte> framePayload, bool isKeyFrame) { }

            /// <summary>
            /// Called when a Mailbox channel is attached to this protocol handler, granting access to
            /// decentralized store-and-forward offline messaging via the Nostr network.
            /// </summary>
            bool SupportsMailbox => true;
            void OnMailboxAttached(IMailboxChannel channel) { }

            /// <summary>
            /// Invoked when an end-to-end encrypted offline direct message addressed to this protocol is received via the Nostr Mailbox.
            /// </summary>
            Task OnOfflineMessageReceivedAsync(PeerInfo peer, ReadOnlyMemory<byte> payload, DateTimeOffset timestamp, string messageId) => Task.CompletedTask;

            /// <summary>
            /// Invoked when a decentralized public global message under this protocol is received via the Nostr Mailbox.
            /// </summary>
            Task OnGlobalMessageReceivedAsync(string senderPubKey, string senderName, ReadOnlyMemory<byte> payload, DateTimeOffset timestamp, string messageId) => Task.CompletedTask;
        }

        private readonly ConcurrentDictionary<Guid, MailboxChannel> _mailboxChannels = new();

        /// <summary>
        /// Retrieves or creates a dedicated Nostr Mailbox channel for the specified protocol.
        /// </summary>
        public IMailboxChannel GetMailboxChannel(Guid protocolId) =>
            _mailboxChannels.GetOrAdd(protocolId, pid => new MailboxChannel(this, pid));

        private async Task MailboxOutboxLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await MailboxStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                do
                {
                    foreach (MailboxChannel channel in _mailboxChannels.Values)
                    {
                        if (ProtocolHandlers.TryGetValue(channel.ProtocolId, out var registered) && !registered.SupportsMailbox) continue;
                        try
                        {
                            await channel.RetryPendingAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            QuicPunchLog.Info($"[NOSTR-MAILBOX] Outbox retry warning for protocol {channel.ProtocolId}: {ex.Message}");
                        }
                    }
                }
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error("[NOSTR-MAILBOX] Durable outbox worker stopped unexpectedly.", ex);
            }
        }

        public bool RemoveProtocol(IProtocolHandler handler) => ProtocolHandlers.TryRemove(handler.ProtocolId, out _);

        public void RegisterProtocol(IProtocolHandler handler)
        {
            handler.OnLocalIdentityProviderAttached(GetLocalIdentityForPeer);
            ProtocolHandlers[handler.ProtocolId] = handler;
            if (!handler.SupportsMailbox) return;
            var channel = GetMailboxChannel(handler.ProtocolId);
            handler.OnMailboxAttached(channel);
        }

        private void HandleMailboxDirectMessage(string senderPubKey, string senderName, Guid protocolId, ReadOnlyMemory<byte> payload, string contentType, string message, DateTimeOffset timestamp, string msgId)
        {
            if (ProtocolHandlers.TryGetValue(protocolId, out var registered) && !registered.SupportsMailbox) return;
            if (IsRevokedSavedNostrContact(senderPubKey)) return;
            var peer = ResolveOrCreatePeerByNostrPubKey(senderPubKey, senderName);
            var channel = (MailboxChannel)GetMailboxChannel(protocolId);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await channel.RecordIncomingAsync(peer, senderPubKey, senderName, payload, contentType, timestamp, msgId, isGlobal: false).ConfigureAwait(false)) return;
                    if (ProtocolHandlers.TryGetValue(protocolId, out var handler))
                        await handler.OnOfflineMessageReceivedAsync(peer, payload, timestamp, msgId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[QUICPUNCH] Error dispatching offline message for protocol {protocolId}: {ex.Message}");
                }
            });
        }

        private bool IsRevokedSavedNostrContact(string publicKey) =>
            PeerStore?.TryGetByNostrPubKey(publicKey, out var saved) == true && saved != null && !IsTrustedPeer(saved.CertHash);

        private void HandleMailboxGlobalMessage(string senderPubKey, string senderName, Guid protocolId, ReadOnlyMemory<byte> payload, string message, DateTimeOffset timestamp, string eventId)
        {
            if (ProtocolHandlers.TryGetValue(protocolId, out var registered) && !registered.SupportsMailbox) return;
            var peer = ResolveOrCreatePeerByNostrPubKey(senderPubKey, senderName);
            var channel = (MailboxChannel)GetMailboxChannel(protocolId);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await channel.RecordIncomingAsync(peer, senderPubKey, senderName, payload, "text/plain; charset=utf-8", timestamp, eventId, isGlobal: true).ConfigureAwait(false)) return;
                    if (ProtocolHandlers.TryGetValue(protocolId, out var handler))
                        await handler.OnGlobalMessageReceivedAsync(senderPubKey, senderName, payload, timestamp, eventId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[QUICPUNCH] Error dispatching global mailbox message for protocol {protocolId}: {ex.Message}");
                }
            });
        }

        public PeerInfo ResolveOrCreatePeerByNostrPubKey(string senderPubKey, string? senderName = null)
        {
            lock (_availablePeerAdmissionLock)
            {
                var peer = AvailablePeers.Values.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p.NostrPubKey) &&
                    string.Equals(p.NostrPubKey, senderPubKey, StringComparison.OrdinalIgnoreCase));

                if (peer != null) return peer;

                if (PeerStore != null && PeerStore.TryGetByNostrPubKey(senderPubKey, out var savedPeer) && savedPeer != null)
                {
                    if (savedPeer.CertHash is { Length: 32 } && TryGetPeerByCertHash(savedPeer.CertHash, out var authenticated) && authenticated != null)
                    {
                        authenticated.NostrPubKey = savedPeer.NostrPubKey ?? senderPubKey;
                        return authenticated;
                    }
                    peer = new PeerInfo();
                    if (savedPeer.CertHash != null && savedPeer.CertHash.Length == 32)
                    {
                        peer.SetCertificateHash(savedPeer.CertHash);
                    }
                    if (savedPeer.EcdhPublicKey != null)
                    {
                        peer.SetECDHPublicKey(savedPeer.EcdhPublicKey);
                    }
                    peer.Name = savedPeer.Name ?? senderName ?? (senderPubKey.Length >= 8 ? senderPubKey[..8] : senderPubKey);
                    peer.NostrPubKey = savedPeer.NostrPubKey ?? senderPubKey;
                    peer.Addresses = savedPeer.Addresses ?? Array.Empty<IPAddress>();
                    peer.PortArray = (ushort[])savedPeer.PortArray.Clone();
                    peer.PreferredPort = savedPeer.PreferredPort;
                    peer.ConnectionFlags = new ConnectionFlags(savedPeer.ConnectionFlags.RawValue);
                    peer.OnionAddress = savedPeer.OnionAddress;
                    peer.NetworkType = savedPeer.NetworkType;

                    AvailablePeers.TryAdd(peer.Id, peer);
                    return peer;
                }

                peer = new PeerInfo
                {
                    Name = !string.IsNullOrWhiteSpace(senderName) ? senderName : (senderPubKey.Length >= 8 ? senderPubKey[..8] : senderPubKey),
                    NostrPubKey = senderPubKey,
                    Addresses = Array.Empty<IPAddress>(),
                    PortArray = Array.Empty<ushort>()
                };
                AvailablePeers.TryAdd(peer.Id, peer);
                return peer;
            }
        }

        private sealed class MailboxChannel : IMailboxChannel
        {
            private readonly QuicPunch _quicPunch;
            private readonly ConcurrentDictionary<string, byte> _publishing = new(StringComparer.Ordinal);

            public Guid ProtocolId { get; }
            public IMailboxStore Store => _quicPunch.MailboxStore;

            public event Action<PeerInfo, ReadOnlyMemory<byte>, DateTimeOffset, string>? OnMessageReceived;
            public event Action<string, string, ReadOnlyMemory<byte>, DateTimeOffset, string>? OnGlobalMessageReceived;

            public MailboxChannel(QuicPunch quicPunch, Guid protocolId)
            {
                _quicPunch = quicPunch;
                ProtocolId = protocolId;
            }

            public async Task<string> SendOfflineAsync(Guid peerId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
            {
                string? nostrPubKey = null;

                if (_quicPunch.AvailablePeers.TryGetValue(peerId, out var peer) && !string.IsNullOrWhiteSpace(peer.NostrPubKey))
                {
                    nostrPubKey = peer.NostrPubKey;
                }

                if (string.IsNullOrWhiteSpace(nostrPubKey) && _quicPunch.PeerStore != null)
                {
                    var sp = _quicPunch.PeerStore.SavedPeers.FirstOrDefault(x =>
                    {
                        if (x.CertHash != null && x.CertHash.Length > 0)
                        {
                            var hashId = new Guid(System.IO.Hashing.XxHash128.Hash(x.CertHash));
                            if (hashId == peerId) return true;
                        }
                        if (!string.IsNullOrWhiteSpace(x.NostrPubKey))
                        {
                            var nostrId = new Guid(System.IO.Hashing.XxHash128.Hash(Encoding.UTF8.GetBytes(x.NostrPubKey.ToLowerInvariant())));
                            if (nostrId == peerId) return true;
                        }
                        return false;
                    });

                    if (sp != null && !string.IsNullOrWhiteSpace(sp.NostrPubKey))
                    {
                        nostrPubKey = sp.NostrPubKey;
                        if (peer != null && string.IsNullOrWhiteSpace(peer.NostrPubKey))
                        {
                            peer.NostrPubKey = sp.NostrPubKey;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(nostrPubKey))
                {
                    throw new InvalidOperationException($"Cannot send offline message to peer {peerId}: no Nostr public key is known or registered.");
                }

                return await SendOfflineAsync(nostrPubKey, payload, ct).ConfigureAwait(false);
            }

            public Task<string> SendOfflineAsync(Guid peerId, string text, CancellationToken ct = default)
                => SendOfflineAsync(peerId, Encoding.UTF8.GetBytes(text), ct);

            public async Task<string> SendOfflineAsync(PeerInfo peer, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
            {
                ArgumentNullException.ThrowIfNull(peer);
                EnsureWanMailboxPeer(peer);
                if (!string.IsNullOrWhiteSpace(peer.NostrPubKey))
                {
                    return await SendOfflineAsync(peer.NostrPubKey, payload, ct).ConfigureAwait(false);
                }
                return await SendOfflineAsync(peer.Id, payload, ct).ConfigureAwait(false);
            }

            private void EnsureWanMailboxRecipient(string publicKey, PeerInfo? knownPeer = null)
            {
                knownPeer ??= _quicPunch.AvailablePeers.Values.FirstOrDefault(p => string.Equals(p.NostrPubKey, publicKey, StringComparison.OrdinalIgnoreCase));
                if (knownPeer != null) EnsureWanMailboxPeer(knownPeer);
                if (_quicPunch.PeerStore?.TryGetByNostrPubKey(publicKey, out var saved) == true && saved != null &&
                    (saved.NetworkType == NetworkType.Tor || !string.IsNullOrWhiteSpace(saved.OnionAddress)))
                    throw new InvalidOperationException("Los contactos Tor no pueden usar el buzÃƒÂ³n de identidad WAN.");
            }

            private static void EnsureWanMailboxPeer(PeerInfo peer)
            {
                if (peer.ActiveTransport == TransportType.Tor || peer.NetworkType == NetworkType.Tor || !string.IsNullOrWhiteSpace(peer.OnionAddress))
                    throw new InvalidOperationException("Los contactos Tor requieren un buzÃƒÂ³n Tor independiente; no se usarÃƒÂ¡ la identidad WAN como alternativa.");
            }

            public Task<string> SendOfflineAsync(PeerInfo peer, string text, CancellationToken ct = default)
                => SendOfflineAsync(peer, Encoding.UTF8.GetBytes(text), ct);

            public Task<string> SendOfflineAsync(string recipientPubKeyHex, ReadOnlyMemory<byte> payload, CancellationToken ct = default) =>
                QueueOfflineAsync(recipientPubKeyHex, payload, null, ct);

            public async Task<string> QueueOfflineAsync(string recipientPubKeyHex, ReadOnlyMemory<byte> payload, MailboxSendOptions? options = null, CancellationToken ct = default)
            {
                if (_quicPunch.Mailbox == null)
                    throw new InvalidOperationException("Nostr Mailbox is not initialized on this QuicPunch instance.");
                if (string.IsNullOrWhiteSpace(recipientPubKeyHex) || recipientPubKeyHex.Length != 64 || !recipientPubKeyHex.All(Uri.IsHexDigit))
                    throw new ArgumentException("Recipient Nostr public key must contain 64 hexadecimal characters.", nameof(recipientPubKeyHex));
                if (payload.Length is 0 or > NostrMailbox.MaxApplicationPayloadBytes)
                    throw new ArgumentOutOfRangeException(nameof(payload), $"Offline payload must contain 1 to {NostrMailbox.MaxApplicationPayloadBytes} bytes.");

                options ??= new MailboxSendOptions();
                if (string.IsNullOrWhiteSpace(options.ContentType) || options.ContentType.Length > 128)
                    throw new ArgumentException("Mailbox content type must contain 1 to 128 characters.", nameof(options));
                if (options.TimeToLive is { } ttl && ttl <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(options), "Mailbox time-to-live must be positive.");
                if (options.CorrelationId?.Length > 256)
                    throw new ArgumentException("Mailbox correlation id cannot exceed 256 characters.", nameof(options));
                string messageId = Guid.CreateVersion7().ToString("D");
                DateTimeOffset now = DateTimeOffset.UtcNow;
                PeerInfo? peer = _quicPunch.AvailablePeers.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.NostrPubKey, recipientPubKeyHex, StringComparison.OrdinalIgnoreCase));
                EnsureWanMailboxRecipient(recipientPubKeyHex, peer);
                string senderName = _quicPunch.CurrentPeer?.Name ?? "";
                var stored = new MailboxStoredMessage(
                    messageId,
                    ProtocolId,
                    MailboxMessageDirection.Outgoing,
                    MailboxDeliveryState.Queued,
                    peer?.Id,
                    _quicPunch.Mailbox.PublicKeyHex,
                    recipientPubKeyHex.ToLowerInvariant(),
                    senderName,
                    payload.ToArray(),
                    options.ContentType,
                    now,
                    now,
                    options.TimeToLive.HasValue ? now + options.TimeToLive.Value : null,
                    IsGlobal: false,
                    AttemptCount: 0,
                    LastAttemptAt: null,
                    LastError: null,
                    RelayEventId: null,
                    CorrelationId: options.CorrelationId);

                bool added = await Store.TryAddAsync(stored, ct).ConfigureAwait(false);
                if (!added && !string.IsNullOrWhiteSpace(options.CorrelationId))
                {
                    var existing = await Store.QueryAsync(new MailboxHistoryQuery(ProtocolId, Direction: MailboxMessageDirection.Outgoing, Limit: 1000), ct).ConfigureAwait(false);
                    return existing.FirstOrDefault(item => item.CorrelationId == options.CorrelationId)?.MessageId ?? messageId;
                }

                await TryPublishAsync(stored, ct).ConfigureAwait(false);
                return messageId;
            }

            public Task<string> SendOfflineAsync(string recipientPubKeyHex, string text, CancellationToken ct = default)
                => SendOfflineAsync(recipientPubKeyHex, Encoding.UTF8.GetBytes(text), ct);

            public async Task<string> SendGlobalAsync(string senderName, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
            {
                if (_quicPunch.Mailbox == null)
                    throw new InvalidOperationException("Nostr Mailbox is not initialized on this QuicPunch instance.");

                return await _quicPunch.Mailbox.SendGlobalMessageAsync(
                    senderName,
                    payload,
                    ProtocolId,
                    ct).ConfigureAwait(false);
            }

            public Task<string> SendGlobalAsync(string senderName, string text, CancellationToken ct = default)
                => SendGlobalAsync(senderName, Encoding.UTF8.GetBytes(text), ct);

            public IReadOnlyList<OfflineMailboxMessage> GetInbox(Guid? peerId = null)
            {
                var history = GetHistoryAsync(new MailboxHistoryQuery(
                    ProtocolId,
                    peerId,
                    MailboxMessageDirection.Incoming,
                    Limit: 500)).GetAwaiter().GetResult();
                return history.Select(item => new OfflineMailboxMessage(
                    item.ProtocolId,
                    item.SenderPubKeyHex,
                    item.SenderName,
                    item.PeerId.HasValue && _quicPunch.AvailablePeers.TryGetValue(item.PeerId.Value, out var peer) ? peer : null,
                    item.Payload,
                    item.CreatedAt,
                    item.MessageId,
                    item.IsGlobal)).ToArray();
            }

            public Task<IReadOnlyList<MailboxStoredMessage>> GetHistoryAsync(MailboxHistoryQuery? query = null, CancellationToken ct = default)
            {
                query ??= new MailboxHistoryQuery(ProtocolId);
                if (query.ProtocolId != ProtocolId) throw new ArgumentException("The history query protocol does not match this channel.", nameof(query));
                return Store.QueryAsync(query, ct);
            }

            public async Task<int> RetryPendingAsync(CancellationToken ct = default)
            {
                var queued = await Store.QueryAsync(new MailboxHistoryQuery(ProtocolId, Direction: MailboxMessageDirection.Outgoing, State: MailboxDeliveryState.Queued, Limit: 1000), ct).ConfigureAwait(false);
                var failed = await Store.QueryAsync(new MailboxHistoryQuery(ProtocolId, Direction: MailboxMessageDirection.Outgoing, State: MailboxDeliveryState.Failed, Limit: 1000), ct).ConfigureAwait(false);
                int published = 0;
                foreach (MailboxStoredMessage message in queued.Concat(failed).OrderBy(item => item.CreatedAt))
                {
                    if (message.ExpiresAt <= DateTimeOffset.UtcNow) continue;
                    int delaySeconds = Math.Min(300, 1 << Math.Min(message.AttemptCount, 8));
                    if (message.LastAttemptAt.HasValue && DateTimeOffset.UtcNow - message.LastAttemptAt.Value < TimeSpan.FromSeconds(delaySeconds)) continue;
                    if (await TryPublishAsync(message, ct).ConfigureAwait(false)) published++;
                }
                return published;
            }

            private async Task<bool> TryPublishAsync(MailboxStoredMessage message, CancellationToken ct)
            {
                if (_quicPunch.IsRevokedSavedNostrContact(message.RecipientPubKeyHex)) return false;
                if (!_publishing.TryAdd(message.MessageId, 0)) return false;
                try
                {
                    EnsureWanMailboxRecipient(message.RecipientPubKeyHex);
                    string relayEventId = await _quicPunch.Mailbox.SendMessageAsync(
                        message.RecipientPubKeyHex,
                        message.Payload,
                        ProtocolId,
                        message.SenderName,
                        message.MessageId,
                        contentType: message.ContentType,
                        expiresAt: message.ExpiresAt,
                        cancellationToken: ct).ConfigureAwait(false);
                    await Store.UpdateDeliveryAsync(message.MessageId, MailboxDeliveryState.RelayAccepted, relayEventId, cancellationToken: ct).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await Store.UpdateDeliveryAsync(message.MessageId, MailboxDeliveryState.Failed, error: ex.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return false;
                }
                finally
                {
                    _publishing.TryRemove(message.MessageId, out _);
                }
            }

            internal async Task<bool> RecordIncomingAsync(PeerInfo peer, string senderPubKey, string senderName, ReadOnlyMemory<byte> payload, string contentType, DateTimeOffset timestamp, string msgId, bool isGlobal)
            {
                var stored = new MailboxStoredMessage(
                    msgId,
                    ProtocolId,
                    MailboxMessageDirection.Incoming,
                    MailboxDeliveryState.Delivered,
                    peer.Id,
                    senderPubKey,
                    _quicPunch.Mailbox.PublicKeyHex,
                    senderName,
                    payload.ToArray(),
                    contentType,
                    timestamp,
                    DateTimeOffset.UtcNow,
                    null,
                    isGlobal,
                    0,
                    null,
                    null,
                    null,
                    null);
                if (!await Store.TryAddAsync(stored).ConfigureAwait(false)) return false;

                if (isGlobal)
                {
                    OnGlobalMessageReceived?.Invoke(senderPubKey, senderName, payload, timestamp, msgId);
                }
                else
                {
                    OnMessageReceived?.Invoke(peer, payload, timestamp, msgId);
                }
                return true;
            }
        }

        public event Action<PeerInfo>? OnPeerAvailable;
        internal void RaisePeerAvailable(PeerInfo peerInfo)
        {
            OnPeerAvailable?.Invoke(peerInfo);
        }

        private readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), (QuicConnection Connection, Stream Stream)> _activeProtocolSessions = new();
        private readonly ConcurrentDictionary<Guid, Multiplexer.QuicPeerMultiplexer> _peerMultiplexers = new();

        public void RegisterPeerMultiplexer(Guid peerId, Multiplexer.QuicPeerMultiplexer mux)
        {
            if (!IsStarted || LifecycleToken.IsCancellationRequested || (AvailablePeers.TryGetValue(peerId, out var transportPeer) && !IsPeerTransportEnabled(transportPeer))) { _ = mux.DisposeAsync(); return; }
            if (_peerMultiplexers.TryGetValue(peerId, out var previous) && !ReferenceEquals(previous, mux)) _ = previous.DisposeAsync();
            _peerMultiplexers[peerId] = mux;
            if (!ReferenceEquals(previous, mux) && AvailablePeers.TryGetValue(peerId, out var historyPeer) && historyPeer.TryGetCertificateHash(out var historyHash))
            {
                try { PeerStore?.RecordConnection(historyHash, historyPeer.ActiveTransport == TransportType.Tor ? null : historyPeer.QuicEndPoint?.Address ?? historyPeer.ActiveEndPoint?.Address); }
                catch (Exception ex) { QuicPunchLog.Info($"[CONTACT HISTORY] Could not save connection: {ex.Message}"); }
            }
            _ = mux.Completion.ContinueWith(_ => UnregisterPeerMultiplexer(peerId, mux), TaskScheduler.Default);
            if (!IsStarted || LifecycleToken.IsCancellationRequested) { UnregisterPeerMultiplexer(peerId, mux); _ = mux.DisposeAsync(); }
        }
        public bool UnregisterPeerMultiplexer(Guid peerId, Multiplexer.QuicPeerMultiplexer mux) => _peerMultiplexers.TryRemove(new KeyValuePair<Guid, Multiplexer.QuicPeerMultiplexer>(peerId, mux));
        public bool TryGetPeerMultiplexer(Guid peerId, out Multiplexer.QuicPeerMultiplexer? mux) => _peerMultiplexers.TryGetValue(peerId, out mux);
        public bool IsPeerMultiplexerActiveOrConnecting(Guid peerId)
        {
            if (TryGetPeerMultiplexer(peerId, out var mux) && mux != null && !mux.IsDisposed)
                return true;
            return _connectingMultiplexers.ContainsKey(peerId);
        }

        public async Task<bool> RegisterProtocolSessionAsync(Guid peerId, Guid protocolId, QuicConnection connection, Stream stream, long generation = 0)
        {
            if (!AvailablePeers.TryGetValue(peerId, out var applicationPeer) || !IsTrustedPeer(applicationPeer))
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
                _ = connection.DatagramChannel;
                connection.OnCategorizedDatagramReceived += (peer, dgram) => protoHandler.OnDatagramReceived(connection, peer, dgram);
                connection.OnFrameReceived += (peer, cat, frame, isKey) => protoHandler.OnFrameReceived(connection, peer, cat, frame, isKey);
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
                        try { await session.Connection.CloseAsync(0).ConfigureAwait(false); } catch { }
                        try { await session.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                        try { await session.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
            }
        }

        public async Task CloseAllPeerSessionsAsync(Guid peerId)
        {
            RevokeRecovery(peerId);
            if (_peerMultiplexers.TryRemove(peerId, out var mux))
            {
                try { await mux.DisposeAsync().ConfigureAwait(false); } catch { }
            }

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
                _ = DecisionTcs.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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
        /// Queries and aggregates point-in-time performance telemetry and protocol metadata for all active QUIC protocol sessions and direct tunnels.
        /// Also updates each associated <see cref="PeerInfo.LastTelemetry"/> reference with fresh statistics.
        /// </summary>
        /// <returns>A read-only collection of <see cref="QuicSessionTelemetryInfo"/> representing active sessions.</returns>
        public IReadOnlyList<QuicSessionTelemetryInfo> GetActiveSessionsTelemetry()
        {
            var results = new List<QuicSessionTelemetryInfo>();
            var peersWithActiveAppSessions = new HashSet<Guid>();

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
                        peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(telemetry.RttMs, 1)));
                    }

                    string? protocolName = null;
                    if (ProtocolHandlers.TryGetValue(protocolId, out var handler))
                    {
                        protocolName = handler.ProtocolName;
                    }

                    peersWithActiveAppSessions.Add(peerId);
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

            // Include physical QUIC multiplexers (base peer-to-peer tunnels)
            foreach (var kvp in _peerMultiplexers)
            {
                var peerId = kvp.Key;
                var mux = kvp.Value;
                if (mux == null || mux.IsDisposed) continue;

                if (mux.TryGetTelemetry(out var telemetry))
                {
                    AvailablePeers.TryGetValue(peerId, out var peer);
                    if (peer != null)
                    {
                        peer.LastTelemetry = telemetry;
                        peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(telemetry.RttMs, 1)));
                    }

                    if (!peersWithActiveAppSessions.Contains(peerId))
                    {
                        results.Add(new QuicSessionTelemetryInfo
                        {
                            PeerId = peerId,
                            PeerName = peer?.Name ?? "Peer",
                            ProtocolId = Guid.Empty,
                            ProtocolName = "Direct QUIC Tunnel",
                            TransportType = mux.TransportType,
                            Telemetry = telemetry,
                            SampleTimeUtc = DateTime.UtcNow
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Queries the latest real-time telemetry from an active connection or multiplexer associated with the specified peer,
        /// or returns the last known cached telemetry if the connection has completed.
        /// </summary>
        /// <param name="peerId">The unique identifier of the peer.</param>
        /// <param name="telemetry">When this method returns, contains the queried or cached <see cref="QuicConnectionTelemetry"/>, or <c>null</c> if unavailable.</param>
        /// <returns><c>true</c> if telemetry is available; otherwise, <c>false</c>.</returns>
        public bool TryGetPeerTelemetry(Guid peerId, out QuicConnectionTelemetry? telemetry)
        {
            telemetry = null;

            // 1. Prefer physical QUIC multiplexer for authoritative transport-level metrics
            if (TryGetPeerMultiplexer(peerId, out var mux) && mux != null && !mux.IsDisposed)
            {
                if (mux.TryGetTelemetry(out var muxTelemetry))
                {
                    telemetry = muxTelemetry;
                    if (AvailablePeers.TryGetValue(peerId, out var peer))
                    {
                        peer.LastTelemetry = muxTelemetry;
                        peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(muxTelemetry.RttMs, 1)));
                    }
                    return true;
                }
            }

            // 2. Active application subprotocol session
            var sessionKvp = _activeProtocolSessions.FirstOrDefault(k => k.Key.PeerId == peerId);
            if (sessionKvp.Value.Connection != null && sessionKvp.Value.Connection.TryGetTelemetry(out var liveTelemetry))
            {
                telemetry = liveTelemetry;
                if (AvailablePeers.TryGetValue(peerId, out var peer))
                {
                    peer.LastTelemetry = liveTelemetry;
                    peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(liveTelemetry.RttMs, 1)));
                }
                return true;
            }

            // 3. Fall back to cached telemetry if available
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
        internal readonly object HandshakeCoordinationLock = new();

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

        internal IncomingHandshakeSession? GetOrAddIncomingHandshakeSession(Guid guid, Guid peerId, Guid protocolId, TransportType transport = TransportType.Wan)
        {
            lock (_incomingHandshakeSessionsLock)
            {
                if (IncomingHandshakeSessions.TryGetValue(guid, out var existing))
                    return existing.PeerId == peerId && existing.ProtocolId == protocolId && existing.Transport == transport ? existing : null;

                int capacity = Math.Max(1, MaxIncomingHandshakeSessions);
                if (IncomingHandshakeSessions.Count >= capacity)
                    PruneExpiredIncomingHandshakeSessions();
                while (IncomingHandshakeSessions.Count >= capacity)
                    EvictOldestIncomingHandshakeSession();

                var newSession = new IncomingHandshakeSession(guid, peerId, protocolId) { Transport = transport };
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
            if (!peer.HasCertificate || !peer.IsSessionReady) throw new InvalidOperationException("Peer identity must be authenticated before preparing QUIC.");
            byte[] packet = PacketBuilder.BuildQuicReadyPacket(this, connectionGuid, listeningPort, transport);
            IReadOnlyList<IPEndPoint> targets = QuicPunchConnection.BuildProbeTargets(peer, null, 0);
            foreach (IPEndPoint target in targets)
            {
                await SendResponseAsync(packet, target, transport, peer.TorChannel).ConfigureAwait(false);
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

            if (!peer.VerifySignature(buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature))
            {
                QuicPunchLog.Info($"[QUIC READY] Received invalid signature from {remoteEndPoint}");
                return;
            }

            if (LifecycleState != QuicPunchLifecycleState.Started ||
                (transport == TransportType.Wan && !Equals(peer.ActiveEndPoint, remoteEndPoint)))
                return;

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
            Guid protocolHandler, PeerInfo peer, ushort localPort, IReadOnlyList<CandidateEndpoint>? candidates = null, CancellationToken cancellationToken = default,
            Func<CancellationToken, Task<(ushort Port, IReadOnlyList<CandidateEndpoint> Candidates)>>? prepareCandidates = null)
        {
            EnsureStarted();
            var connectionGuid = Guid.NewGuid();
            var transport = peer.ActiveTransport;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken, TransportLifetime(transport));
            var negotiationKey = (peer.Id, protocolHandler);
            var outbound = new OutboundNegotiation(connectionGuid, linkedCts);

            while (true)
            {
                OutboundNegotiation? existingNegotiation;
                lock (HandshakeCoordinationLock)
                {
                    if (IncomingHandshakeSessions.Values.Any(s => s.PeerId == peer.Id && s.ProtocolId == protocolHandler &&
                        s.Transport == transport && _activeIncomingWorkers.ContainsKey(s.ConnectionGuid)))
                    {
                        WriteLine($"[Handshake] Joining incoming connection already being prepared for {peer.Name}.");
                        return (new HandshakeDecision(false, null, null), connectionGuid);
                    }
                    if (_activeOutboundNegotiations.TryAdd(negotiationKey, outbound)) break;
                    _activeOutboundNegotiations.TryGetValue(negotiationKey, out existingNegotiation);
                }
                if (existingNegotiation != null)
                {
                    QuicPunchLog.Info($"[Handshake Negotiation] Outbound negotiation for {peer.Name} ({negotiationKey}) already in flight. Awaiting existing negotiation.");
                    return await existingNegotiation.DecisionTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                // Reserve our role before STUN/router preparation so incoming
                // requests can arbitrate glare while the socket is being prepared.
                if (prepareCandidates != null)
                {
                    var prepared = await prepareCandidates(linkedCts.Token).ConfigureAwait(false);
                    localPort = prepared.Port;
                    candidates = prepared.Candidates;
                }
                linkedCts.Token.ThrowIfCancellationRequested();
                var payload = GenerateHandshakePayload(HandShakeType.Request, localPort, protocolHandler, connectionGuid, candidates, transport);
                var epForRequest = peer.ActiveEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                var decisionTask = _manager.WaitForDecisionAsync(new HandshakeRequest(connectionGuid, protocolHandler, epForRequest, peer.Id, peer.CertHash, transport), TimeSpan.FromSeconds(25), false, linkedCts.Token);
                int attempts = 0;

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

                    int intervalMs = attempts++ < 5 ? 150 : 500;
                    await Task.WhenAny(decisionTask, Task.Delay(intervalMs, linkedCts.Token)).ConfigureAwait(false);
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
                    Exception declinedEx = decision.FailureReason == "timeout"
                        ? new TimeoutException($"No authenticated handshake response from {peer.Name} within 25s (UDP signaling endpoint: {peer.ActiveEndPoint}).")
                        : decision.FailureReason == "cancelled"
                            ? new OperationCanceledException("Handshake negotiation cancelled.", linkedCts.Token)
                            : new Exception("Handshake explicitly declined or unsupported by peer.");
                    outbound.DecisionTcs.TrySetException(declinedEx);
                    throw declinedEx;
                }

                WriteLine("Peer accepted :D");
                var acceptResult = (decision, connectionGuid);
                outbound.DecisionTcs.TrySetResult(acceptResult);
                return acceptResult;
            }
            catch (OperationCanceledException) when (outbound.IsYielded && !LifecycleToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                WriteLine("[Handshake Glare] Socket preparation yielded to remote authoritative request.");
                var yieldedResult = (new HandshakeDecision(false, null, null), connectionGuid);
                outbound.DecisionTcs.TrySetResult(yieldedResult);
                return yieldedResult;
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
                var gatherTask = SimpleStunClient.GatherCandidatesAsync(nudp, boundPort, StunServerEndpoints, TimeSpan.FromMilliseconds(1000), cancellationToken);
                var mappingTask = MapTunnelAsync(boundPort, cancellationToken);
                await Task.WhenAll(gatherTask, mappingTask).ConfigureAwait(false);
                var candidates = gatherTask.Result;
                var mapped = mappingTask.Result;
                if (mapped is { Success: true, ExternalIp: not null, ExternalPort: > 0 and <= 65535 })
                {
                    var endpoint = new IPEndPoint(mapped.ExternalIp, mapped.ExternalPort);
                    if (!candidates.Any(c => c.EndPoint.Equals(endpoint)))
                        candidates.Add(new CandidateEndpoint(endpoint, CandidateType.ServerReflexive, 1694498800));
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

        private readonly ConcurrentDictionary<Guid, Task<Multiplexer.QuicPeerMultiplexer>> _connectingMultiplexers = new();

        public async Task<Multiplexer.QuicPeerMultiplexer> EnsureMultiplexerConnectedAsync(PeerInfo peer, ushort localPort = 0, CancellationToken cancellationToken = default)
        {
            EnsureStarted();
            ArgumentNullException.ThrowIfNull(peer);
            if (peer.ActiveTransport != TransportType.Tor && QuicPunchConnection.AndroidQuicUnavailableReason is { } unavailableReason)
                throw new PlatformNotSupportedException(unavailableReason);

            if (TryGetPeerMultiplexer(peer.Id, out var existingMux) && existingMux != null && !existingMux.IsDisposed)
            {
                return existingMux;
            }

            TaskCompletionSource<Multiplexer.QuicPeerMultiplexer>? myTcs = null;
            Task<Multiplexer.QuicPeerMultiplexer>? taskToAwait = null;

            while (true)
            {
                if (TryGetPeerMultiplexer(peer.Id, out existingMux) && existingMux != null && !existingMux.IsDisposed)
                {
                    return existingMux;
                }

                if (_connectingMultiplexers.TryGetValue(peer.Id, out var existingTask))
                {
                    taskToAwait = existingTask;
                    break;
                }

                if (myTcs == null)
                {
                    myTcs = new TaskCompletionSource<Multiplexer.QuicPeerMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = myTcs.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }

                if (_connectingMultiplexers.TryAdd(peer.Id, myTcs.Task))
                {
                    break;
                }
            }

            if (taskToAwait != null)
            {
                return await taskToAwait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            long transportGeneration = LifecycleGeneration;
            bool transportReserved = false;
            try
            {
                transportReserved = TryReservePeerTransport(peer.Id, transportGeneration);
                if (!transportReserved) throw new InvalidOperationException("Peer transport capacity reached.");
                peer = await ResolveAuthenticatedPeerAsync(peer, cancellationToken).ConfigureAwait(false);
                peer.LastQuicConnectionAttemptUtc = DateTime.UtcNow;
                peer.LastQuicConnectionError = null;
                var mux = await ConnectMultiplexerCoreAsync(peer, localPort, cancellationToken).ConfigureAwait(false);
                myTcs!.TrySetResult(mux);
                return mux;
            }
            catch (Exception ex)
            {
                peer.LastQuicConnectionError = ex.Message;
                myTcs!.TrySetException(ex);
                throw;
            }
            finally
            {
                _connectingMultiplexers.TryRemove(new KeyValuePair<Guid, Task<Multiplexer.QuicPeerMultiplexer>>(peer.Id, myTcs!.Task));
                if (transportReserved) ReleasePeerTransport(peer.Id, transportGeneration);
            }
        }

        private async Task<PeerInfo> ResolveAuthenticatedPeerAsync(PeerInfo peer, CancellationToken cancellationToken)
        {
            var known = ResolveAvailablePeer(peer) ?? peer;
            if (known.HasCertificate && known.IsSessionReady && known.ActiveEndPoint != null) return known;

            // A trusted token/mailbox identity is not an authenticated UDP control
            // path. Complete Hello first; never spend the QUIC negotiation budget
            // sending requests to a placeholder with no verification key.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (!ActiveInterrogations.Values.Any(s => PeerTargetsMatch(s.Peer, peer)))
                    await PeerInterrogation(peer, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    known = ResolveAvailablePeer(peer);
                    if (known != null && known.HasCertificate && known.ActiveEndPoint != null)
                        return known;
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!LifecycleToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No authenticated Hello from {peer.Name} within 15s. Check the peer's UDP control endpoints.");
            }
        }

        private async Task<Multiplexer.QuicPeerMultiplexer> ConnectMultiplexerCoreAsync(PeerInfo peer, ushort localPort, CancellationToken cancellationToken)
        {
            if (TryGetPeerMultiplexer(peer.Id, out var existing) && existing != null && !existing.IsDisposed)
            {
                return existing;
            }

            if (peer.ActiveTransport == TransportType.Tor && peer.TorChannel != null)
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken, TransportLifetime(peer.ActiveTransport));

                // Tor already gives us a reliable authenticated control lane, but the remote side still
                // needs an explicit signal to instantiate the one physical QuicPeerMultiplexer that will
                // own all virtual application sessions.  The previous Tor fast-path skipped this bootstrap
                // completely, so the first QUIC-like stream was queued in TorPeerTransportHub with nobody
                // calling AcceptInboundStreamAsync on the remote side.
                var (decision, _) = await NegotiateConnection(Guid.Empty, peer, 0, null, linkedCts.Token).ConfigureAwait(false);
                if (!decision.Accepted)
                {
                    // A simultaneous bootstrap may yield to the peer's authoritative request.  In that
                    // case the incoming handshake worker owns construction of this side's multiplexer.
                    long waitStarted = Stopwatch.GetTimestamp();
                    while (Stopwatch.GetElapsedTime(waitStarted) < TimeSpan.FromSeconds(10))
                    {
                        if (TryGetPeerMultiplexer(peer.Id, out var glareMux) && glareMux != null && !glareMux.IsDisposed)
                            return glareMux;

                        await Task.Delay(25, linkedCts.Token).ConfigureAwait(false);
                    }

                    throw new InvalidOperationException($"Tor multiplexer negotiation yielded for peer {peer.Name}, but the incoming multiplexer was not established.");
                }

                if (TryGetPeerMultiplexer(peer.Id, out var negotiatedMux) && negotiatedMux != null && !negotiatedMux.IsDisposed)
                    return negotiatedMux;

                // Multiplexer roles are derived from the identity of the active transport.  Tor has its
                // own certificate/PeerInfo, so using CurrentPeer (WAN) here can make both endpoints choose
                // the same role and either both open or both wait for the control stream.
                bool isServer = QuicPunchConnection.AmIServer(GetCurrentPeer(TransportType.Tor), peer);
                var quicConn = peer.TorChannel.CreateQuicConnection();
                var mux = new Multiplexer.QuicPeerMultiplexer(
                    quicConn,
                    peer,
                    isServer,
                    protocolLookup: id => ProtocolHandlers.TryGetValue(id, out var h) ? h : null,
                    accessCheck: IsTrustedPeer,
                    sessionAccessCheck: (p, id, ct) => AuthorizeIncomingSessionAsync(p, id, TransportType.Tor, ct),
                    sessionRegisteredCallback: (protoId, conn, str) => RegisterProtocolSessionAsync(peer.Id, protoId, conn, str, _lifecycleGeneration),
                    sessionUnregisteredCallback: (protoId, conn) => UnregisterProtocolSessionAsync(peer.Id, protoId, conn),
                    recoveryAuthorization: AuthorizeRecoverableSessionAsync,
                    recoveryFinished: FinishRecoverableSession,
                    sessionStateChanged: RaiseSessionState,
                    transportOptions: PeerTransport,
                    recoveryHandlerFinished: RecoveryHandlerFinished);

                try
                {
                    // StartAsync now owns control-stream establishment.  The logical QUIC client opens it;
                    // the logical QUIC server accepts it, exactly like the native transport path.
                    await mux.StartAsync(linkedCts.Token).ConfigureAwait(false);
                    RegisterPeerMultiplexer(peer.Id, mux);
                    return mux;
                }
                catch
                {
                    try { await mux.DisposeAsync().ConfigureAwait(false); } catch { }
                    throw;
                }
            }

            UdpClient? nudp = null;
            try
            {
                var (decision, connectionGuid) = await NegotiateConnection(Guid.Empty, peer, localPort, null, cancellationToken,
                    async preparationToken =>
                    {
                        var gathered = await CreateBoundSocketAndGatherCandidatesAsync(localPort, $"[QUIC MULTIPLEXER] Candidates for connection with {peer.Name}", preparationToken).ConfigureAwait(false);
                        nudp = gathered.Socket;
                        return (gathered.BoundPort, gathered.Candidates);
                    }).ConfigureAwait(false);
                if (!decision.Accepted)
                {
                    nudp?.Dispose();
                    using var glareTimeout = CancellationTokenSource.CreateLinkedTokenSource(LifecycleToken, cancellationToken);
                    glareTimeout.CancelAfter(TimeSpan.FromSeconds(25));
                    while (true)
                    {
                        if (TryGetPeerMultiplexer(peer.Id, out var glareMux) && glareMux != null && !glareMux.IsDisposed)
                            return glareMux;
                        if (HasRecoveringSession(peer.Id) && !HasActiveIncomingWorker(peer.Id))
                            throw new IOException("The old remote tunnel is being checked; retry the recovery handshake.");
                        await Task.Delay(25, glareTimeout.Token).ConfigureAwait(false);
                    }
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationSource.Token, cancellationToken, TransportLifetime(peer.ActiveTransport));
                var connection = await QuicPunchConnection.InitQuicConnectionCore(this, CurrentPeer, nudp!, peer, decision.Candidates, (ushort)decision.Port!, connectionGuid, CertManager.PeerCertificate!, linkedCts.Token).ConfigureAwait(false);

                if (connection.Connection == null || connection.Stream == null)
                {
                    throw new InvalidOperationException($"Failed to establish physical QUIC tunnel with peer {peer.Name}.");
                }

                bool isServer = QuicPunchConnection.AmIServer(CurrentPeer, peer);
                var mux = new Multiplexer.QuicPeerMultiplexer(
                    connection.Connection,
                    peer,
                    isServer,
                    protocolLookup: id => ProtocolHandlers.TryGetValue(id, out var h) ? h : null,
                    accessCheck: IsTrustedPeer,
                    sessionAccessCheck: (p, id, ct) => AuthorizeIncomingSessionAsync(p, id, TransportType.Wan, ct),
                    initialControlStream: connection.Stream,
                    sessionRegisteredCallback: (protoId, conn, str) => RegisterProtocolSessionAsync(peer.Id, protoId, conn, str, _lifecycleGeneration),
                    sessionUnregisteredCallback: (protoId, conn) => UnregisterProtocolSessionAsync(peer.Id, protoId, conn),
                    recoveryAuthorization: AuthorizeRecoverableSessionAsync,
                    recoveryFinished: FinishRecoverableSession,
                    sessionStateChanged: RaiseSessionState,
                    transportOptions: PeerTransport,
                    recoveryHandlerFinished: RecoveryHandlerFinished);

                await mux.StartAsync(linkedCts.Token).ConfigureAwait(false);
                RegisterPeerMultiplexer(peer.Id, mux);
                return mux;
            }
            catch
            {
                nudp?.Dispose();
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
                    _ = myTcs.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
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
            IProtocolHandler handler, PeerInfo peer, ushort localPort,
            TaskCompletionSource<object?> flightTcs, CancellationToken cancellationToken)
        {
            try { await RunProtocolSessionAsync(handler, peer, localPort, flightTcs, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) { flightTcs.TrySetException(ex); throw; }
        }

        public class ActiveInterrogationSession
        {
            public string Id { get; } = Guid.NewGuid().ToString();
            public PeerInfo Peer { get; }
            public DateTime StartTime { get; } = DateTime.Now;
            public CancellationTokenSource Cts { get; }
            public bool OwnsPeer { get; }
            public bool IsAutomaticContact { get; }

            public ActiveInterrogationSession(PeerInfo peer, CancellationTokenSource cts, bool ownsPeer = false, bool automaticContact = false)
            {
                Peer = peer;
                Cts = cts;
                OwnsPeer = ownsPeer;
                IsAutomaticContact = automaticContact;
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

        private static bool PeerTargetsMatch(PeerInfo a, PeerInfo b) => Utilities.PeerTargetsMatch(a, b);
        private static bool EndpointMatchesPeer(IPEndPoint endpoint, PeerInfo peer) => Utilities.EndpointMatchesPeer(endpoint, peer);

        private PeerInfo? ResolveAvailablePeer(PeerInfo candidate)
        {
            if (candidate.TryGetId(out var peerId) && AvailablePeers.TryGetValue(peerId, out var byId))
                return byId;

            if (candidate.TryGetCertificateHash(out var certHash) && TryGetPeerByCertHash(certHash, out var byCert))
                return byCert;

            return AvailablePeers.Values.FirstOrDefault(p => PeerTargetsMatch(p, candidate));
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

        private async Task PeerInterrogationCore(PeerInfo peer, CancellationToken cancellationToken, bool ownsPeer, bool useDiscoveryBudget, bool automaticContact = false)
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
                        await ConnectTorAsync(peer.OnionAddress, peer.PortArray?.Length > 0 ? peer.PortArray[0] : (ushort)443, cancellationToken, peer).ConfigureAwait(false);
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
                if (!automaticContact) lcts.CancelAfter(TimeSpan.FromSeconds(useDiscoveryBudget ? 30 : 45));
                var session = new ActiveInterrogationSession(peer, lcts, ownsPeer, automaticContact);

                var existingKeys = ActiveInterrogations
                    .Where(kv =>
                        PeerTargetsMatch(kv.Value.Peer, peer) ||
                        (kv.Value.Peer.Addresses != null && peer.Addresses != null && kv.Value.Peer.Addresses.Any(a => !IPAddress.IsLoopback(a) && peer.Addresses.Contains(a)) && Utilities.PortAdvertisementsOverlap(kv.Value.Peer.ConnectionFlags, kv.Value.Peer.PortArray, peer.ConnectionFlags, peer.PortArray)) ||
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

            if (!isLanDiscovery && remoteEndPoint is IPEndPoint ipEp && ReputationFilter != null && ReputationFilter.IsBlocked(ipEp.Address))
            {
                ReputationFilter.RecordDrop();
                return;
            }

            var span = buffer.AsSpan(0, length);
            if (!span.Slice(0, MagicHeader.Length).SequenceEqual(MagicHeader))
                return;

            byte messageType = span[MagicHeader.Length];

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

                    case (byte)MessageType.FinalHandshake:
                        // Silently consume hole-punch burst packets arriving on the discovery socket
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

                        if (ReputationFilter != null && ReputationFilter.IsBlocked(remoteEp.Address))
                        {
                            ReputationFilter.RecordDrop();
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
            QuicPunchLog.Info($"[AcceptTorLoopAsync] Starting loop for node {TorCurrentPeer.Name}...");
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
            var schedule = new PortProbeSchedule();
            bool dynamicPorts = peer.ConnectionFlags.PortMode != PortMode.Single && IsTrustedPeer(peer);

            bool includePassword = PasswordHash != null;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (dynamicPorts) schedule.NextRound();
                    PeerInfo? availablePeer = ResolveAvailablePeer(peer);
                    bool peerResponded = availablePeer != null;

                    bool hasFreshCandidate = false;
                    if (peerResponded && availablePeer!.ActiveEndPoint != null)
                    {
                        if (peer.ActiveEndPoint != null && !peer.ActiveEndPoint.Equals(availablePeer.ActiveEndPoint))
                        {
                            hasFreshCandidate = true;
                        }
                        else if (!Utilities.EndpointMatchesPeer(availablePeer.ActiveEndPoint, peer))
                        {
                            hasFreshCandidate = true;
                        }
                    }

                    if (peerResponded && availablePeer!.ActiveEndPoint != null)
                    {
                        var helloPayload = GenerateHelloPayload(MessageType.Hello, includePassword, targetPeer: availablePeer);
                        // Keep the last authenticated path alive while probing the
                        // fresh rendezvous candidate. Identity is unchanged; only a
                        // signed Hello may promote a new endpoint.
                        await udp.SendAsync(helloPayload, availablePeer.ActiveEndPoint).ConfigureAwait(false);

                        if (hasFreshCandidate)
                        {
                            await udp.BigSendAsync(helloPayload, peer, dynamicPorts ? schedule : null, token).ConfigureAwait(false);
                        }

                        if (!hasFreshCandidate && availablePeer.IsSessionReady)
                        {
                            QuicPunchLog.Info($"Peer {peer} interrogation resolved and session ready.");
                            break;
                        }
                    }
                    else
                    {
                        var payload = GenerateHelloPayload(MessageType.Interrogation, true, targetPeer: peer);
                        await udp.BigSendAsync(payload, peer, dynamicPorts ? schedule : null, token).ConfigureAwait(false);
                    }

                    tries++;

                    int delayMs;
                    delayMs = AutomaticContactRetryDelayMilliseconds(tries);

                    QuicPunchLog.Info($"Send hello packet to {peer} that responded {peerResponded} at {PreciseTime.GetCorrectTime():HH:mm:ss.fff} time til next {TimeSpan.FromMilliseconds(delayMs).TotalSeconds:F1}s");

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

                ushort[]? ports = CurrentPeer.PortArray;
                if (ports is not { Length: > 0 } || ports.Any(port => port == 0))
                {
                    QuicPunchLog.Info("[TOKEN] WAN endpoint is not ready; no token is available yet.");
                    return "";
                }

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
