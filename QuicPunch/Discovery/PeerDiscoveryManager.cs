using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;
using static QuicPunch.QuicPunchStructures;
using static QuicPunch.QuicPunch;

namespace QuicPunch.Discovery
{
    /// <summary>
    /// Autonomous coordinator managing Nostr decentralized signaling (WAN and Tor),
    /// local LAN multicast discovery beaconing, and the discovered peer table.
    /// </summary>
    public sealed class PeerDiscoveryManager : IDisposable
    {
        public const int DefaultLanDiscoveryPort = 7227;
        public const string DefaultLanDiscoveryMulticast = "239.255.72.27";

        private const int MaxNostrCandidateCount = 256;
        private static readonly TimeSpan NostrCandidateCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan NostrCandidateTtl = TimeSpan.FromMinutes(3);

        private readonly IPeerDiscoveryContext _context;
        private readonly object _nostrCandidateLock = new();
        private readonly Dictionary<string, long> _nostrCandidates = new(StringComparer.Ordinal);

        private readonly SemaphoreSlim _nostrLifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _torNostrLifecycleLock = new(1, 1);
        private readonly SemaphoreSlim _lanLifecycleLock = new(1, 1);

        private UdpClient? _lanDiscoveryUdp;
        private Task? _lanDiscoveryLoopTask;
        private bool _isDisposed;

        public ConcurrentDictionary<string, DiscoveredPeerInfo> DiscoveredPeers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool WanNostrDiscoveryEnabled { get; set; } = false;
        public bool TorNostrDiscoveryEnabled { get; set; } = false;

        public bool NostrDiscoveryEnabled
        {
            get => WanNostrDiscoveryEnabled;
            set => WanNostrDiscoveryEnabled = value;
        }

        public NostrDiscovery? WanNostrDiscovery { get; private set; }
        public NostrDiscovery? TorNostrDiscovery { get; private set; }
        public NostrDiscovery? NostrDiscovery => WanNostrDiscovery;

        public string[]? NostrRelays { get; set; }
        public string[]? TorNostrRelays { get; set; }

        public int LanDiscoveryPort { get; set; } = DefaultLanDiscoveryPort;

        public event EventHandler<DiscoveredPeerInfo>? PeerDiscovered;
        public event EventHandler<DiscoveredPeerInfo>? PeerUpdated;

        public PeerDiscoveryManager(IPeerDiscoveryContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        #region Discovered Peer Registry

        internal bool IsSelfOrLocalMachine(PeerInfo peer, bool isTor, string? nostrAuthorPubKey = null)
        {
            if (!string.IsNullOrEmpty(nostrAuthorPubKey))
            {
                if (!string.IsNullOrEmpty(_context.WanNostrPublicKeyHex) && string.Equals(nostrAuthorPubKey, _context.WanNostrPublicKeyHex, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(_context.TorNostrPublicKeyHex) && string.Equals(nostrAuthorPubKey, _context.TorNostrPublicKeyHex, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (peer.TryGetCertificateHash(out var peerHash))
            {
                if (_context.LocalWanCertHash != null && CryptographicOperations.FixedTimeEquals(peerHash, _context.LocalWanCertHash))
                    return true;
                if (_context.LocalTorCertHash != null && CryptographicOperations.FixedTimeEquals(peerHash, _context.LocalTorCertHash))
                    return true;
            }

            if (!isTor)
            {
                var localIps = Utilities.GetValidLocalIPAddresses();
                var localPeer = _context.LocalWanPeer;

                // 1. If peer's port matches our local bound port and shares any IP address:
                if (_context.LocalBoundPort > 0 && peer.PortArray != null && peer.PortArray.Contains((ushort)_context.LocalBoundPort))
                {
                    if (peer.Addresses != null && (localIps.Any(a => peer.Addresses.Contains(a)) || (localPeer?.Addresses != null && peer.Addresses.Intersect(localPeer.Addresses).Any())))
                        return true;
                }

                // 2. If node name matches and it shares any IP address:
                bool nameMatches = !string.IsNullOrWhiteSpace(localPeer?.Name) && !string.IsNullOrWhiteSpace(peer.Name) &&
                    string.Equals(localPeer.Name, peer.Name, StringComparison.OrdinalIgnoreCase);

                if (nameMatches)
                {
                    if (peer.Addresses != null && localIps.Any(a => peer.Addresses.Contains(a)))
                        return true;

                    if (peer.Addresses != null && localPeer?.Addresses != null && peer.Addresses.Intersect(localPeer.Addresses).Any())
                        return true;
                }

                // 3. If all peer addresses are local/loopback IPs and port matches our local bound port:
                if (peer.Addresses != null && peer.Addresses.Length > 0 && peer.Addresses.All(a => localIps.Contains(a) || IPAddress.IsLoopback(a)))
                {
                    if (_context.LocalBoundPort > 0 && peer.PortArray != null && peer.PortArray.Contains((ushort)_context.LocalBoundPort))
                        return true;
                }
            }
            else
            {
                var localTorPeer = _context.LocalTorPeer;
                if (!string.IsNullOrEmpty(localTorPeer?.OnionAddress) && !string.IsNullOrEmpty(peer.OnionAddress) &&
                    string.Equals(localTorPeer.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void RecordDiscoveredPeer(PeerInfo peer, string token, string source = "Nostr")
        {
            if (peer == null || !peer.TryGetCertificateHash(out var hash))
                return;

            if (IsSelfOrLocalMachine(peer, peer.NetworkType == NetworkType.Tor, peer.NostrPubKey))
                return;

            string certHashB64 = Convert.ToBase64String(hash);
            string hexKey = Convert.ToHexString(hash);

            string resolvedName = !string.IsNullOrWhiteSpace(peer.Name) ? peer.Name : "";
            if (string.IsNullOrWhiteSpace(resolvedName) && _context.PeerStore != null &&
                _context.PeerStore.TryGet(hash, out var sp) && !string.IsNullOrWhiteSpace(sp?.Name))
            {
                resolvedName = sp.Name;
            }

            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                if (_context.TryGetActivePeer(hash, out var avail) && !string.IsNullOrWhiteSpace(avail?.Name))
                    resolvedName = avail.Name;
            }

            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                var act = _context.GetActiveInterrogations()
                    .FirstOrDefault(s => s.Peer.TryGetCertificateHash(out var h) && CryptographicOperations.FixedTimeEquals(h, hash));
                if (act != null && !string.IsNullOrWhiteSpace(act.Peer.Name) &&
                    act.Peer.Name != "Saved Peer" && act.Peer.Name != "Peer" && act.Peer.Name != "Unknown")
                {
                    resolvedName = act.Peer.Name;
                }
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
                PortArray = peer.PortArray,
                NetworkType = peer.NetworkType,
                ConnectionFlags = peer.ConnectionFlags != null ? new ConnectionFlags(peer.ConnectionFlags.RawValue) : new ConnectionFlags(),
                NostrPubKey = peer.NostrPubKey,
                Source = source,
                DiscoveredAt = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                IsCertVerified = peer.IsCertVerified,
                CertPublicKey = peer.CertPublicKey,
                CertPublicKeyBase64 = peer.CertPublicKey != null ? Convert.ToBase64String(peer.CertPublicKey) : null
            };

            bool isNew = false;
            DiscoveredPeers.AddOrUpdate(hexKey,
                _ =>
                {
                    isNew = true;
                    return info;
                },
                (_, existing) =>
                {
                    existing.Token = token;
                    existing.LastSeen = DateTime.UtcNow;
                    if (!string.IsNullOrWhiteSpace(resolvedName) && resolvedName != "Discovered Peer")
                        existing.Name = resolvedName;
                    else if (!string.IsNullOrWhiteSpace(peer.Name) && peer.Name != "Discovered Peer")
                        existing.Name = peer.Name;

                    if (peer.Addresses != null && peer.Addresses.Length > 0)
                        existing.Addresses = peer.Addresses.Select(a => a.ToString()).ToArray();

                    if (!string.IsNullOrWhiteSpace(peer.OnionAddress))
                        existing.OnionAddress = peer.OnionAddress;

                    if (!string.IsNullOrWhiteSpace(peer.NostrPubKey))
                        existing.NostrPubKey = peer.NostrPubKey;

                    existing.PortArray = peer.PortArray;
                    existing.NetworkType = peer.NetworkType;
                    if (peer.ConnectionFlags != null)
                        existing.ConnectionFlags = new ConnectionFlags(peer.ConnectionFlags.RawValue);
                    existing.Source = source;
                    existing.IsCertVerified = peer.IsCertVerified;

                    if (peer.CertPublicKey != null)
                    {
                        existing.CertPublicKey = peer.CertPublicKey;
                        existing.CertPublicKeyBase64 = Convert.ToBase64String(peer.CertPublicKey);
                    }

                    return existing;
                });

            if (isNew)
                PeerDiscovered?.Invoke(this, info);
            else
                PeerUpdated?.Invoke(this, info);

            PruneStaleDiscoveredPeers();
        }

        public void PruneStaleDiscoveredPeers(TimeSpan? maxAge = null)
        {
            var age = maxAge ?? TimeSpan.FromMinutes(5);
            var cutoff = DateTime.UtcNow - age;
            var localWanHash = _context.LocalWanCertHash;
            var localTorHash = _context.LocalTorCertHash;
            var localIps = Utilities.GetValidLocalIPAddresses();
            var localName = _context.LocalWanPeer?.Name;
            var localPeerAddrs = _context.LocalWanPeer?.Addresses;
            var wanNostrPub = _context.WanNostrPublicKeyHex;
            var torNostrPub = _context.TorNostrPublicKeyHex;
            var boundPort = _context.LocalBoundPort;

            foreach (var kv in DiscoveredPeers)
            {
                bool isSelf = false;
                if (!string.IsNullOrEmpty(kv.Value.NostrPubKey))
                {
                    if (!string.IsNullOrEmpty(wanNostrPub) && string.Equals(kv.Value.NostrPubKey, wanNostrPub, StringComparison.OrdinalIgnoreCase)) isSelf = true;
                    if (!string.IsNullOrEmpty(torNostrPub) && string.Equals(kv.Value.NostrPubKey, torNostrPub, StringComparison.OrdinalIgnoreCase)) isSelf = true;
                }

                if (!isSelf && kv.Value.CertHash != null)
                {
                    if (localWanHash != null && CryptographicOperations.FixedTimeEquals(kv.Value.CertHash, localWanHash)) isSelf = true;
                    if (localTorHash != null && CryptographicOperations.FixedTimeEquals(kv.Value.CertHash, localTorHash)) isSelf = true;
                }

                if (!isSelf)
                {
                    if (kv.Value.NetworkType != NetworkType.Tor)
                    {
                        var parsed = kv.Value.Addresses?.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).Where(a => a != null && !IPAddress.IsLoopback(a)).Cast<IPAddress>().ToList() ?? new List<IPAddress>();

                        if (boundPort > 0 && kv.Value.PortArray != null && kv.Value.PortArray.Contains((ushort)boundPort))
                        {
                            if (parsed.Any(a => localIps.Contains(a)) || (localPeerAddrs != null && parsed.Any(a => localPeerAddrs.Contains(a))))
                                isSelf = true;
                        }

                        if (!isSelf)
                        {
                            bool nameMatches = !string.IsNullOrWhiteSpace(localName) && !string.IsNullOrWhiteSpace(kv.Value.Name) &&
                                string.Equals(localName, kv.Value.Name, StringComparison.OrdinalIgnoreCase);

                            if (nameMatches && parsed.Count > 0)
                            {
                                if (parsed.Any(a => localIps.Contains(a)) || (localPeerAddrs != null && parsed.Any(a => localPeerAddrs.Contains(a))))
                                    isSelf = true;
                            }
                        }

                        if (!isSelf && parsed.Count > 0 && parsed.All(a => localIps.Contains(a)))
                        {
                            if (boundPort > 0 && kv.Value.PortArray != null && kv.Value.PortArray.Contains((ushort)boundPort))
                                isSelf = true;
                        }
                    }
                    else
                    {
                        var localTorPeer = _context.LocalTorPeer;
                        if (!string.IsNullOrEmpty(localTorPeer?.OnionAddress) && !string.IsNullOrEmpty(kv.Value.OnionAddress) &&
                            string.Equals(localTorPeer.OnionAddress, kv.Value.OnionAddress, StringComparison.OrdinalIgnoreCase))
                        {
                            isSelf = true;
                        }
                    }
                }

                if (isSelf || kv.Value.LastSeen < cutoff)
                {
                    DiscoveredPeers.TryRemove(kv.Key, out _);
                }
            }
        }

        #endregion

        #region Nostr Discovery Lifecycle & Dynamic Controls

        public Task SetPeerDiscoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            SetWanPeerDiscoveryEnabledAsync(enabled, cancellationToken);

        public async Task SetWanPeerDiscoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            WanNostrDiscoveryEnabled = enabled;

            if (!_context.IsStarted)
                return;

            await _nostrLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (enabled)
                {
                    await StartWanNostrDiscoveryCoreAsync(_context.LifecycleToken).ConfigureAwait(false);
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
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            TorNostrDiscoveryEnabled = enabled;

            if (!_context.IsStarted || !_context.IsTorStarted)
                return;

            await _torNostrLifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (enabled)
                {
                    await StartTorNostrDiscoveryCoreAsync(_context.LifecycleToken).ConfigureAwait(false);
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

        public async Task StartWanNostrDiscoveryAsync(CancellationToken token)
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
            if (!WanNostrDiscoveryEnabled || token.IsCancellationRequested || !_context.IsStarted)
                return;

            if (WanNostrDiscovery is { IsRunning: true })
                return;

            await StopWanNostrDiscoveryCoreAsync().ConfigureAwait(false);

            byte[]? poolId = _context.PoolId is { Length: 20 } p ? p : null;
            var discovery = new NostrDiscovery(
                poolId,
                GetWanNostrDiscoveryPayload,
                scope: "wan",
                nostrPrivateKey: _context.WanNostrPrivateKey);

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

        public async Task StopWanNostrDiscoveryAsync()
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

        public async Task RestartWanNostrDiscoveryAsync(CancellationToken token)
        {
            await _nostrLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StopWanNostrDiscoveryCoreAsync().ConfigureAwait(false);
                if (WanNostrDiscoveryEnabled && _context.IsStarted && !token.IsCancellationRequested)
                    await StartWanNostrDiscoveryCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _nostrLifecycleLock.Release();
            }
        }

        public async Task StartTorNostrDiscoveryAsync(CancellationToken token)
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
            if (!TorNostrDiscoveryEnabled || token.IsCancellationRequested || !_context.IsStarted || !_context.IsTorStarted)
                return;

            if (TorNostrDiscovery is { IsRunning: true })
                return;

            await StopTorNostrDiscoveryCoreAsync().ConfigureAwait(false);

            byte[]? poolId = _context.PoolId is { Length: 20 } p ? p : null;
            var discovery = new NostrDiscovery(
                poolId,
                GetTorNostrDiscoveryPayload,
                scope: "tor",
                proxyProvider: _context.GetTorSocksProxy,
                nostrPrivateKey: _context.TorNostrPrivateKey);

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

        public async Task StopTorNostrDiscoveryAsync()
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

        public async Task RestartTorNostrDiscoveryAsync(CancellationToken token)
        {
            await _torNostrLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await StopTorNostrDiscoveryCoreAsync().ConfigureAwait(false);
                if (TorNostrDiscoveryEnabled && _context.IsStarted && _context.IsTorStarted && !token.IsCancellationRequested)
                    await StartTorNostrDiscoveryCoreAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _torNostrLifecycleLock.Release();
            }
        }

        public async Task StartNostrDiscoveryAsync(CancellationToken token)
        {
            await StartWanNostrDiscoveryAsync(token).ConfigureAwait(false);
            if (_context.IsTorStarted)
                await StartTorNostrDiscoveryAsync(token).ConfigureAwait(false);
        }

        public async Task StopNostrDiscoveryAsync()
        {
            await StopWanNostrDiscoveryAsync().ConfigureAwait(false);
            await StopTorNostrDiscoveryAsync().ConfigureAwait(false);
        }

        public async Task StopAllAsync()
        {
            try { await StopNostrDiscoveryAsync().ConfigureAwait(false); } catch { }
            try { await StopLanDiscoveryAsync().ConfigureAwait(false); } catch { }
        }

        public async Task RestartNostrDiscoveryAsync(CancellationToken token)
        {
            await RestartWanNostrDiscoveryAsync(token).ConfigureAwait(false);
            if (_context.IsTorStarted)
                await RestartTorNostrDiscoveryAsync(token).ConfigureAwait(false);
        }

        public async Task PublishNostrDiscoveryAsync(CancellationToken token)
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

        #endregion

        #region Nostr Token Generation & Inbound Processing

        private string? GetWanNostrDiscoveryPayload()
        {
            try
            {
                string? token = _context.GetWanToken();
                if (string.IsNullOrEmpty(token)) return null;

                byte[]? certPubKey = _context.WanCertPublicKey;
                if (certPubKey == null || certPubKey.Length == 0)
                    return token;

                var cert = _context.WanCertificate;
                if (cert == null) return token;

                byte[] sig = Utilities.SignToken(token, cert);

                return JsonSerializer.Serialize(new
                {
                    wan = token,
                    name = _context.LocalWanPeer?.Name,
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
                string? token = _context.GetTorToken();
                if (string.IsNullOrEmpty(token)) return null;

                var torCert = _context.TorCertificate;
                if (torCert == null) return null;

                byte[]? certPubKey = _context.TorCertPublicKey;
                if (certPubKey == null || certPubKey.Length == 0)
                    return token;

                byte[] sig = Utilities.SignToken(token, torCert);

                return JsonSerializer.Serialize(new
                {
                    tor = token,
                    name = _context.LocalTorPeer?.Name,
                    certPubKey = Convert.ToBase64String(certPubKey),
                    sig = Convert.ToBase64String(sig)
                });
            }
            catch { return null; }
        }

        public void ProcessWanNostrEndpointToken(string token, string? nostrPubKeyHex = null, byte[]? certPubKey = null, byte[]? signature = null) =>
            ProcessNostrEndpointTokenCore(token, nostrPubKeyHex, certPubKey, signature, isTor: false);

        public void ProcessTorNostrEndpointToken(string token, string? nostrPubKeyHex = null, byte[]? certPubKey = null, byte[]? signature = null) =>
            ProcessNostrEndpointTokenCore(token, nostrPubKeyHex, certPubKey, signature, isTor: true);

        private void OnWanNostrTokenDiscovered(string token) =>
            OnWanNostrEventDiscovered(string.Empty, token);

        public void OnWanNostrEventDiscovered(string nostrPubKeyHex, string token)
        {
            if (!WanNostrDiscoveryEnabled || !_context.IsStarted || string.IsNullOrWhiteSpace(token))
                return;

            string trimmed = token.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    byte[]? certPubKeyBytes = null;
                    byte[]? sigBytes = null;
                    string? name = null;

                    if (document.RootElement.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        name = nameEl.GetString();
                    }

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
                            ProcessNostrEndpointTokenCore(wan, nostrPubKeyHex, certPubKeyBytes, sigBytes, isTor: false, advertisedName: name);
                            return;
                        }
                    }
                }
                catch (JsonException) { return; }
            }

            ProcessNostrEndpointTokenCore(trimmed, nostrPubKeyHex, null, null, isTor: false);
        }

        private void OnTorNostrTokenDiscovered(string token) =>
            OnTorNostrEventDiscovered(string.Empty, token);

        public void OnTorNostrEventDiscovered(string nostrPubKeyHex, string token)
        {
            if (!TorNostrDiscoveryEnabled || !_context.IsStarted || string.IsNullOrWhiteSpace(token))
                return;

            string trimmed = token.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    byte[]? certPubKeyBytes = null;
                    byte[]? sigBytes = null;
                    string? name = null;

                    if (document.RootElement.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        name = nameEl.GetString();
                    }

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
                            ProcessNostrEndpointTokenCore(tor, nostrPubKeyHex, certPubKeyBytes, sigBytes, isTor: true, advertisedName: name);
                            return;
                        }
                    }
                }
                catch (JsonException) { return; }
            }

            ProcessNostrEndpointTokenCore(trimmed, nostrPubKeyHex, null, null, isTor: true);
        }

        private void ProcessNostrEndpointTokenCore(string token, string? nostrPubKeyHex, byte[]? certPubKey, byte[]? signature, bool isTor, string? advertisedName = null)
        {
            string tag = isTor ? "[NOSTR-TOR]" : "[NOSTR-WAN]";
            try
            {
                var peer = Utilities.DecodeEndpointToken(token);
                if (isTor ? peer.NetworkType != NetworkType.Tor : peer.NetworkType == NetworkType.Tor)
                {
                    peer.Dispose();
                    return;
                }

                if (!peer.TryGetCertificateHash(out var peerHash))
                {
                    peer.Dispose();
                    return;
                }

                if (!string.IsNullOrWhiteSpace(advertisedName) && string.IsNullOrWhiteSpace(peer.Name))
                {
                    peer.Name = advertisedName;
                }

                if (!string.IsNullOrEmpty(nostrPubKeyHex))
                {
                    peer.NostrPubKey = nostrPubKeyHex;
                }

                if (IsSelfOrLocalMachine(peer, isTor, nostrPubKeyHex))
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

                // If this peer is already saved in PeerStore, verify the Nostr author key before applying cooldowns
                if (_context.PeerStore != null && _context.PeerStore.TryGet(peerHash, out var savedPeer) && savedPeer != null)
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

                // Only update endpoints for peers explicitly saved in PeerStore (exact certHash match)
                string? nostrKeyToSave = nostrPubKeyHex;
                PeerStore.SavedPeer? savedPeerToUpdate = null;
                if (_context.PeerStore != null && _context.PeerStore.TryGet(peerHash, out savedPeerToUpdate) && savedPeerToUpdate != null)
                {
                    if (!string.IsNullOrWhiteSpace(savedPeerToUpdate.Name))
                        peer.Name = savedPeerToUpdate.Name;
                    nostrKeyToSave = savedPeerToUpdate.NostrPubKey ?? nostrPubKeyHex;
                    _context.PeerStore.AddOrUpdate(peer, autoConnect: savedPeerToUpdate.AutoConnect, save: true, nostrPubKey: nostrKeyToSave);
                }

                bool endpointsChanged = false;
                if (DiscoveredPeers.TryGetValue(hexKey, out var existingDp))
                {
                    if (!isTor)
                    {
                        var existingIps = new HashSet<string>(existingDp.Addresses ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                        var newIps = new HashSet<string>(peer.Addresses?.Select(a => a.ToString()) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                        endpointsChanged = !existingIps.SetEquals(newIps) || !(existingDp.PortArray ?? []).SequenceEqual(peer.PortArray ?? []);
                    }
                    else
                    {
                        endpointsChanged = !string.Equals(existingDp.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase) ||
                                           !(existingDp.PortArray ?? []).SequenceEqual(peer.PortArray ?? []);
                    }
                }

                var activeInterrogations = _context.GetActiveInterrogations()
                    .Where(session =>
                        (session.Peer.TryGetCertificateHash(out var sHash) && CryptographicOperations.FixedTimeEquals(sHash, peerHash)) ||
                        (savedPeerToUpdate != null && session.Peer.TryGetCertificateHash(out var sHash2) && CryptographicOperations.FixedTimeEquals(sHash2, savedPeerToUpdate.CertHash)) ||
                        (isTor && !string.IsNullOrEmpty(session.Peer.OnionAddress) && string.Equals(session.Peer.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase)) ||
                        Utilities.PeerTargetsMatch(session.Peer, peer))
                    .ToList();

                bool wasConnecting = activeInterrogations.Count > 0;
                bool interrogationEndpointsDiffer = false;
                if (wasConnecting)
                {
                    foreach (var s in activeInterrogations)
                    {
                        if (!isTor)
                        {
                            var sIps = new HashSet<IPAddress>(s.Peer.Addresses ?? Array.Empty<IPAddress>());
                            var newIps = new HashSet<IPAddress>(peer.Addresses ?? Array.Empty<IPAddress>());
                            if (!sIps.SetEquals(newIps) || !(s.Peer.PortArray ?? []).SequenceEqual(peer.PortArray ?? []))
                            {
                                interrogationEndpointsDiffer = true;
                                endpointsChanged = true;
                            }
                        }
                        else
                        {
                            if (!string.Equals(s.Peer.OnionAddress, peer.OnionAddress, StringComparison.OrdinalIgnoreCase) ||
                                !(s.Peer.PortArray ?? []).SequenceEqual(peer.PortArray ?? []))
                            {
                                interrogationEndpointsDiffer = true;
                                endpointsChanged = true;
                            }
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

                if (!_context.TryGetActivePeer(peerHash, out var activePeer) && savedPeerToUpdate?.CertHash != null)
                {
                    _context.TryGetActivePeer(savedPeerToUpdate.CertHash, out activePeer);
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
                    if (peer.PortArray != null)
                        activePeer.PortArray = peer.PortArray;
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
                if (wasConnecting && interrogationEndpointsDiffer)
                {
                    foreach (var s in activeInterrogations)
                    {
                        _context.CancelInterrogation(s.Id);
                    }
                }

                bool isConnectedAndHealthy = activePeer != null && activePeer.IsConnectedAndResponsive();

                // Reconnect policy:
                // Only initiate a connection if:
                // 1) Peer was connecting and changed endpoints (interrogationEndpointsDiffer == true), OR
                // 2) Peer is disconnected and NOT already connecting (!wasConnecting), and auto-connect is enabled.
                bool shouldReconnect = (wasConnecting && interrogationEndpointsDiffer) ||
                                       (!isConnectedAndHealthy && !wasConnecting && (
                                           _context.AutoConnectOnDiscovery ||
                                           (savedPeerToUpdate != null && savedPeerToUpdate.AutoConnect)
                                       ));

                if (shouldReconnect && _context.IsStarted)
                {
                    if (!isTor)
                    {
                        string reason = wasConnecting && interrogationEndpointsDiffer
                            ? "changed endpoints while connecting. Initiating connection with verified new endpoints..."
                            : "is disconnected with auto-connect enabled. Initiating connection...";
                        QuicPunchLog.Info($"{tag} Peer {peer.Name ?? hexKey[..8]} {reason}");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _context.InterrogatePeerAsync(peer, _context.LifecycleToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                QuicPunchLog.Info($"{tag} Interrogation failed for {peer.Name}: {ex.Message}");
                            }
                        });
                    }
                    else if (_context.IsTorStarted && !string.IsNullOrEmpty(peer.OnionAddress))
                    {
                        string reason = wasConnecting && interrogationEndpointsDiffer
                            ? "changed onion/port while connecting. Initiating Tor connection with verified new endpoints..."
                            : "is disconnected with auto-connect enabled. Initiating Tor connection...";
                        QuicPunchLog.Info($"{tag} Peer {peer.Name ?? hexKey[..8]} {reason}");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _context.ConnectTorAsync(peer.OnionAddress, peer.PortArray?.Length > 0 ? peer.PortArray[0] : (ushort)443, _context.LifecycleToken).ConfigureAwait(false);
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

        #endregion

        #region LAN Multicast Discovery Subsystem

        public async Task StartLanDiscoveryAsync(CancellationToken token)
        {
            ThrowIfDisposed();
            await _lanLifecycleLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_lanDiscoveryUdp != null)
                    return;

                try
                {
                    _lanDiscoveryUdp = new UdpClient();
                    _context.ConfigureUdpSocket(_lanDiscoveryUdp);
                    _lanDiscoveryUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    // SO_REUSEPORT (0x0200 on Linux, 15 on some Windows sockets)
                    try { _lanDiscoveryUdp.Client.SetSocketOption(SocketOptionLevel.Socket, (SocketOptionName)15, true); } catch { }

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
                    _lanDiscoveryLoopTask = Task.Run(() => ReceiveLanDiscoveryLoopAsync(lanDiscoveryUdp, token), token);
                    QuicPunchLog.Info($"[LAN DISCOVERY] Listening on common port {LanDiscoveryPort} and multicast group {DefaultLanDiscoveryMulticast}");
                }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[LAN DISCOVERY] Notice: Could not bind dedicated LAN discovery socket on port {LanDiscoveryPort}: {ex.Message}");
                }
            }
            finally
            {
                _lanLifecycleLock.Release();
            }
        }

        public async Task StopLanDiscoveryAsync()
        {
            await _lanLifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var oldUdp = _lanDiscoveryUdp;
                _lanDiscoveryUdp = null;

                if (oldUdp != null)
                {
                    try { oldUdp.Close(); oldUdp.Dispose(); } catch { }
                }

                if (_lanDiscoveryLoopTask != null)
                {
                    try { await _lanDiscoveryLoopTask.ConfigureAwait(false); } catch { }
                    _lanDiscoveryLoopTask = null;
                }
            }
            finally
            {
                _lanLifecycleLock.Release();
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
                            await _context.ProcessIncomingLanPacketAsync(receiveBuffer, bytesRead, result.RemoteEndPoint).ConfigureAwait(false);
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

        public async Task SendLocalLanDiscoveryAsync()
        {
            try
            {
                var payload = _context.GenerateLanBeaconPayload();
                var udp = _context.WanUdpClient;
                if (udp != null)
                {
                    udp.EnableBroadcast = true;
                    udp.MulticastLoopback = true;
                    var mcastEp = new IPEndPoint(IPAddress.Parse(DefaultLanDiscoveryMulticast), LanDiscoveryPort);
                    try { await udp.SendAsync(payload, mcastEp).ConfigureAwait(false); } catch { }

                    try
                    {
                        using var loopSender = new UdpClient();
                        _context.ConfigureUdpSocket(loopSender);
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

        #endregion

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(PeerDiscoveryManager));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { WanNostrDiscovery?.Dispose(); WanNostrDiscovery = null; } catch { }
            try { TorNostrDiscovery?.Dispose(); TorNostrDiscovery = null; } catch { }

            try
            {
                _lanDiscoveryUdp?.Close();
                _lanDiscoveryUdp?.Dispose();
                _lanDiscoveryUdp = null;
            }
            catch { }

            _nostrLifecycleLock.Dispose();
            _torNostrLifecycleLock.Dispose();
            _lanLifecycleLock.Dispose();
        }
    }
}
