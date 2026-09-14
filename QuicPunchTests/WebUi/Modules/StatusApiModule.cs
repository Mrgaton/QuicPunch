using System.Net;
using System.Security.Cryptography;
using System.Web;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunchTests.Protocols;
using QuicPunchTests.Settings;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class StatusApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly VirtualLanHandler _lanHandler;
    private readonly VoiceCallHandler _voiceHandler;
    private readonly AppPreferencesStore _preferences;
    private readonly PeerApiModule _peerModule;
    private readonly ChatApiModule _chatModule;

    public StatusApiModule(
        QuicPunch.QuicPunch qcc,
        ChatHandler chatHandler,
        VirtualLanHandler lanHandler,
        VoiceCallHandler voiceHandler,
        AppPreferencesStore preferences,
        PeerApiModule peerModule,
        ChatApiModule chatModule)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _lanHandler = lanHandler;
        _voiceHandler = voiceHandler;
        _preferences = preferences;
        _peerModule = peerModule;
        _chatModule = chatModule;
    }

    public object GetStatusObject(bool includeChat = false, bool includeClipboard = false)
    {
        string wanToken = _qcc.IsWanStarted ? (_qcc.GetWanToken() ?? "") : "";
        string torToken = _qcc.GetTorToken() ?? "";
        AppPreferences prefs = _preferences.Snapshot();
        var saved = _qcc.PeerStore?.SavedPeers?.ToList() ?? new List<PeerStore.SavedPeer>();
        var savedByHash = new Dictionary<string, PeerStore.SavedPeer>(StringComparer.Ordinal);
        foreach (var item in saved) savedByHash[Convert.ToBase64String(item.CertHash)] = item;

        var activeSessionTelemetry = _qcc.GetActiveSessionsTelemetry();
        var quicTelemetry = activeSessionTelemetry.Select(s => new
        {
            peerId = s.PeerId.ToString(),
            peerName = s.PeerName ?? "Peer",
            protocolId = s.ProtocolId.ToString(),
            protocolName = s.ProtocolName ?? s.ProtocolId.ToString(),
            transportType = s.TransportType.ToString(),
            telemetry = MapTelemetry(s.Telemetry),
            sampleTime = s.SampleTimeUtc.ToString("o")
        }).ToList();

        var peers = _qcc.AvailablePeers.Values
            .Where(peer =>
            {
                if (!_qcc.TryGetPeerMultiplexer(peer.Id, out var m) || m == null || m.IsDisposed)
                {
                    if (!peer.IsConnectedAndResponsive(TimeSpan.FromMinutes(1)))
                    {
                        _ = Task.Run(() => _qcc.DisconnectPeer(peer.Id));
                        return false;
                    }
                }
                return true;
            })
            .Select(peer =>
        {
            string certHash = peer.TryGetCertificateHash(out var hash) ? Convert.ToBase64String(hash) : "";
            string canonId = Utilities.ToCanonicalPeerId(hash);
            savedByHash.TryGetValue(certHash, out var savedPeer);
            bool trusted = _qcc.IsTrustedPeer(peer);

            bool isQuicConnected = false;
            QuicConnectionTelemetry? liveTelem = null;
            if (_qcc.TryGetPeerMultiplexer(peer.Id, out var mux) && mux != null && !mux.IsDisposed)
            {
                isQuicConnected = true;
                if (mux.TryGetTelemetry(out var muxTelem))
                {
                    liveTelem = muxTelem;
                    peer.LastTelemetry = muxTelem;
                    peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(muxTelem.RttMs, 1)));
                    peer.LastPingResponseUtc = DateTime.UtcNow;
                    peer.LastSeen = DateTime.UtcNow;
                }
            }
            else if (peer.LastTelemetry != null)
            {
                liveTelem = peer.LastTelemetry;
            }

            int lastSeenSec = peer.LastSeen > DateTime.MinValue ? (int)Math.Max(0, (DateTime.UtcNow - peer.LastSeen).TotalSeconds) : 99999;
            int? unresponsiveSec = isQuicConnected
                ? 0
                : (peer.LastPingResponseUtc.HasValue
                    ? (int)Math.Max(0, (DateTime.UtcNow - peer.LastPingResponseUtc.Value).TotalSeconds)
                    : (lastSeenSec < 99999 ? lastSeenSec : (int?)null));

            double? pingMs = liveTelem != null
                ? Math.Round(liveTelem.RttMs, 1)
                : (peer.Ping.HasValue ? Math.Round(peer.Ping.Value.TotalMilliseconds, 1) : null);

            bool isResponsive = isQuicConnected || (pingMs.HasValue && unresponsiveSec.HasValue && unresponsiveSec.Value <= 10);
            string displayName = !string.IsNullOrWhiteSpace(savedPeer?.Name) ? savedPeer.Name : (peer.Name ?? "Peer");

            return new
            {
                id = peer.Id.ToString(),
                canonicalId = canonId,
                name = displayName,
                savedName = savedPeer?.Name,
                peerName = peer.Name,
                ping = pingMs,
                hasPing = isResponsive,
                pingSource = isQuicConnected ? "QUIC RTT" : "UDP Ping",
                isQuicConnected,
                unresponsiveSeconds = unresponsiveSec ?? 99999,
                endpoint = peer.ActiveEndPoint?.ToString() ?? "",
                addresses = peer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                ports = peer.PortArray ?? Array.Empty<ushort>(),
                portMode = peer.ConnectionFlags?.PortMode.ToString() ?? "Single",
                networkType = peer.NetworkType.ToString(),
                isTor = peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(peer.OnionAddress),
                onionAddress = peer.OnionAddress ?? "",
                certHash,
                nostrPubKey = peer.NostrPubKey ?? savedPeer?.NostrPubKey ?? "",
                isTrusted = trusted,
                isSaved = savedPeer != null,
                autoConnectOnStartup = savedPeer?.AutoConnect ?? false,
                isAutoAccepted = trusted && _qcc.IsPeerAutoAccepted(peer.Id),
                lastSeenSecondsAgo = lastSeenSec,
                telemetry = MapTelemetry(liveTelem ?? peer.LastTelemetry)
            };
        }).OrderByDescending(p => p.isTrusted).ThenBy(p => p.name).ToList();

        var savedPeers = saved.Select(item => new
        {
            canonicalId = Utilities.ToCanonicalPeerId(item.CertHash),
            name = item.Name ?? "",
            certHash = Convert.ToBase64String(item.CertHash),
            nostrPubKey = item.NostrPubKey ?? "",
            autoConnect = item.AutoConnect,
            onionAddress = item.OnionAddress ?? "",
            addresses = item.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
            ports = item.PortArray ?? Array.Empty<ushort>(),
            portMode = item.ConnectionFlags?.PortMode.ToString() ?? "Single",
            networkType = item.NetworkType.ToString()
        }).ToList();

        var lanPeers = _lanHandler.ActivePeers.Values.Select(item => new
        {
            peerId = item.Peer.Id.ToString(),
            peerName = item.Peer.Name ?? "Peer",
            virtualIp = item.RemoteIp.ToString(),
            connectedAt = item.ConnectedAt.ToString("HH:mm:ss"),
            rxPackets = item.RxPackets,
            txPackets = item.TxPackets,
            rxBytes = item.RxBytes,
            txBytes = item.TxBytes
        }).ToList();

        object[] chatMessages = includeChat
            ? _chatModule.ChatMessages.TakeLast(200).Select(message => (object)new
            {
                peerId = message.PeerId,
                msgId = message.MsgId,
                sender = message.Sender,
                message = message.Message,
                time = message.Timestamp.ToString("HH:mm:ss"),
                isMe = message.IsMe,
                isConfirmed = message.IsConfirmed
            }).ToArray()
            : Array.Empty<object>();

        object[] clipboardItems = includeClipboard
            ? _peerModule.ClipboardItems.TakeLast(50).Select(item => (object)new
            {
                peerId = item.PeerId,
                peerName = item.PeerName,
                text = item.Text,
                time = item.Timestamp.ToString("HH:mm:ss"),
                isMe = item.IsMe
            }).ToArray()
            : Array.Empty<object>();

        var petitions = _peerModule.PendingPetitions.Values.Select(item => new
        {
            requestId = item.RequestId.ToString(),
            peerId = item.PeerId.ToString(),
            peerName = item.PeerName,
            protocolId = item.ProtocolId.ToString(),
            protocolName = item.ProtocolName
        }).ToList();

        var interrogations = _qcc.ActiveInterrogations.Values.Select(item =>
        {
            item.Peer.TryGetCertificateHash(out var hash);
            string certHashB64 = hash != null ? Convert.ToBase64String(hash) : "";
            string canonId = Utilities.ToCanonicalPeerId(hash);

            string pName = item.Peer.Name ?? "";
            if (string.IsNullOrWhiteSpace(pName) || pName == "Peer" || pName == "Saved Peer" || pName == "Unknown" || pName == "Discovered Peer")
            {
                if (hash != null && _qcc.PeerStore != null && _qcc.PeerStore.TryGet(hash, out var sp) && !string.IsNullOrWhiteSpace(sp?.Name))
                    pName = sp.Name;
            }

            return new
            {
                id = item.Id,
                sessionId = item.Id,
                canonicalId = canonId,
                peerId = item.Peer.Id != Guid.Empty ? item.Peer.Id.ToString() : canonId,
                certHash = certHashB64,
                peerName = !string.IsNullOrWhiteSpace(pName) ? pName : "Unknown",
                addresses = item.Peer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                ports = item.Peer.PortArray ?? Array.Empty<ushort>(),
                portMode = item.Peer.ConnectionFlags?.PortMode.ToString() ?? "Single",
                startTime = item.StartTime.ToString("HH:mm:ss")
            };
        }).ToList();

        var discovery = new
        {
            enabled = _qcc.Discovery.WanNostrDiscoveryEnabled,
            desiredEnabled = prefs.WanNostrDiscoveryEnabled,
            running = _qcc.Discovery.WanNostrDiscovery?.IsRunning ?? false,
            connectedRelays = _qcc.Discovery.WanNostrDiscovery?.ConnectedRelayCount ?? 0,
            relayCount = _qcc.Discovery.NostrRelays?.Length ?? QuicPunch.NostrDiscovery.DefaultRelays.Length,

            wan = new
            {
                enabled = _qcc.Discovery.WanNostrDiscoveryEnabled,
                desiredEnabled = prefs.WanNostrDiscoveryEnabled,
                running = _qcc.Discovery.WanNostrDiscovery?.IsRunning ?? false,
                connectedRelays = _qcc.Discovery.WanNostrDiscovery?.ConnectedRelayCount ?? 0,
                relayCount = _qcc.Discovery.NostrRelays?.Length ?? QuicPunch.NostrDiscovery.DefaultRelays.Length,
                serviceRunning = _qcc.IsWanStarted,
                serviceDesiredEnabled = prefs.WanEnabled
            },

            tor = new
            {
                enabled = _qcc.Discovery.TorNostrDiscoveryEnabled,
                desiredEnabled = prefs.TorNostrDiscoveryEnabled,
                running = _qcc.Discovery.TorNostrDiscovery?.IsRunning ?? false,
                connectedRelays = _qcc.Discovery.TorNostrDiscovery?.ConnectedRelayCount ?? 0,
                relayCount = _qcc.Discovery.TorNostrRelays?.Length ?? _qcc.Discovery.NostrRelays?.Length ?? QuicPunch.NostrDiscovery.DefaultRelays.Length,
                serviceRunning = _qcc.IsTorStarted,
                serviceDesiredEnabled = prefs.TorEnabled
            }
        };

        var wan = new
        {
            desiredEnabled = prefs.WanEnabled,
            isStarted = _qcc.IsWanStarted,
            port = _qcc.LocalDiscoveryPort,
            lastError = _qcc.WanLastError ?? ""
        };

        var tor = new
        {
            desiredEnabled = prefs.TorEnabled,
            isStarted = _qcc.IsTorStarted,
            onionAddress = _qcc.TorOnionAddress ?? "",
            bootstrapProgress = _qcc.TorBootstrapProgress,
            bootstrapStatus = _qcc.TorBootstrapStatus,
            activeTransport = _qcc.TorActiveTransport.ToString(),
            transportMode = prefs.TorTransportMode,
            virtualPort = _qcc.TorHub?.VirtualPort ?? prefs.TorVirtualPort,
            socksPort = _qcc.TorManager?.SocksPort ?? 0,
            lastError = _qcc.TorLastError ?? ""
        };

        bool isRunningAsAdmin = LanApiModule.IsRunningAsAdmin();
        bool requiresAdmin = !isRunningAsAdmin && (
            _lanHandler.AdapterStatus.Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
            (_lanHandler.LastError ?? "").Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
            (_lanHandler.AdapterStatus == "Error" && !_lanHandler.IsActive));

        var lan = new
        {
            desiredEnabled = prefs.LanEnabled,
            active = _lanHandler.IsActive,
            adapterName = _lanHandler.AdapterName,
            adapterStatus = _lanHandler.AdapterStatus,
            lastError = _lanHandler.LastError ?? "",
            isAdmin = isRunningAsAdmin,
            requiresAdmin,
            autoAssign = _lanHandler.AutoAssignEnabled,
            localIp = _lanHandler.LocalIp.ToString(),
            subnetMask = _lanHandler.SubnetMask,
            mtu = _lanHandler.Mtu,
            activePeers = lanPeers,
            totalRxPackets = _lanHandler.TotalRxPackets,
            totalTxPackets = _lanHandler.TotalTxPackets,
            totalRxBytes = _lanHandler.TotalRxBytes,
            totalTxBytes = _lanHandler.TotalTxBytes
        };

        var apps = new
        {
            chat = new { active = ChatHandler.ActiveChats.Count, protocolId = _chatHandler.ProtocolId.ToString() },
            voice = new { active = VoiceCallHandler.ActiveCalls.Count, protocolId = _voiceHandler.ProtocolId.ToString() },
            lan = new { active = _lanHandler.ActivePeers.Count, protocolId = _lanHandler.ProtocolId.ToString() },
            clipboard = new { active = _peerModule.ClipboardItems.Count, protocolId = "" }
        };

        var stunHits = _qcc.GetStunHitsSnapshot();
        var exactMappingsList = new List<object>();
        if (stunHits.Count > 0)
        {
            foreach (var kv in stunHits.OrderByDescending(k => k.Value).ThenBy(k => Utilities.IpToUint(k.Key.Address)).ThenBy(k => k.Key.Port))
            {
                exactMappingsList.Add(new { address = kv.Key.Address.ToString(), port = kv.Key.Port, endpoint = kv.Key.ToString(), hits = kv.Value });
            }
        }

        var stun = new
        {
            publicAddresses = _qcc.CurrentPeer?.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
            exactMappings = exactMappingsList.ToArray(),
            ports = _qcc.CurrentPeer?.PortArray ?? Array.Empty<ushort>(),
            portMode = _qcc.CurrentPeer?.ConnectionFlags?.PortMode.ToString() ?? "Single",
            localAddresses = Utilities.GetValidLocalIPAddresses().Select(a => a.ToString()).ToArray(),
            serverCount = _qcc.StunServerEndpoints?.Count ?? 0,
            networkType = _qcc.CurrentPeer?.NetworkType.ToString() ?? "Unknown"
        };

        _qcc.Discovery.PruneStaleDiscoveredPeers();
        var activePeerCertHashes = new HashSet<string>(
            _qcc.AvailablePeers.Values.Select(p => p.TryGetCertificateHash(out var h) ? Convert.ToBase64String(h) : "").Where(s => !string.IsNullOrEmpty(s)),
            StringComparer.Ordinal);
        var localWanHashB64 = _qcc.LocalWanCertHash != null ? Convert.ToBase64String(_qcc.LocalWanCertHash) : "";
        var localTorHashB64 = _qcc.LocalTorCertHash != null ? Convert.ToBase64String(_qcc.LocalTorCertHash) : "";
        var wanNostrPub = _qcc.WanNostrPublicKeyHex;
        var torNostrPub = _qcc.TorNostrPublicKeyHex;
        var localIps = Utilities.GetValidLocalIPAddresses();
        var localName = _qcc.CurrentPeer?.Name;
        var localPeerAddrs = _qcc.CurrentPeer?.Addresses;
        var boundPort = _qcc.LocalDiscoveryPort;

        var discoveredPeers = _qcc.Discovery.DiscoveredPeers.Values
            .Where(dp =>
            {
                if (activePeerCertHashes.Contains(dp.CertHashBase64)) return false;
                if (!string.IsNullOrEmpty(localWanHashB64) && dp.CertHashBase64 == localWanHashB64) return false;
                if (!string.IsNullOrEmpty(localTorHashB64) && dp.CertHashBase64 == localTorHashB64) return false;

                if (!string.IsNullOrEmpty(dp.NostrPubKey))
                {
                    if (!string.IsNullOrEmpty(wanNostrPub) && string.Equals(dp.NostrPubKey, wanNostrPub, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!string.IsNullOrEmpty(torNostrPub) && string.Equals(dp.NostrPubKey, torNostrPub, StringComparison.OrdinalIgnoreCase)) return false;
                }

                if (dp.NetworkType != QuicPunch.QuicPunch.NetworkType.Tor)
                {
                    var parsed = dp.Addresses?.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).Where(a => a != null && !IPAddress.IsLoopback(a)).Cast<IPAddress>().ToList() ?? new List<IPAddress>();

                    // Check: Contains both this machine's local private IP AND public WAN IP for self/unnamed ghost
                    if (parsed.Count > 0 && localPeerAddrs != null && localPeerAddrs.Length > 0)
                    {
                        bool hasLocalIp = parsed.Any(a => localIps.Contains(a));
                        var publicIps = localPeerAddrs.Where(a => !localIps.Contains(a)).ToList();
                        bool hasPublicIp = publicIps.Count > 0 && parsed.Any(a => publicIps.Contains(a));
                        bool isGhostOrSameName = string.IsNullOrWhiteSpace(dp.Name) || dp.Name == "Discovered Peer" ||
                            (!string.IsNullOrWhiteSpace(localName) && string.Equals(localName, dp.Name, StringComparison.OrdinalIgnoreCase));
                        if (hasLocalIp && hasPublicIp && isGhostOrSameName) return false;
                    }

                    bool nameMatches = !string.IsNullOrWhiteSpace(localName) && !string.IsNullOrWhiteSpace(dp.Name) &&
                        string.Equals(localName, dp.Name, StringComparison.OrdinalIgnoreCase);

                    if (nameMatches && parsed.Count > 0)
                    {
                        if (parsed.Any(a => localIps.Contains(a))) return false;
                        if (localPeerAddrs != null && parsed.Any(a => localPeerAddrs.Contains(a))) return false;
                    }

                    if (parsed.Count > 0 && parsed.All(a => localIps.Contains(a)))
                    {
                        if (boundPort > 0 && dp.PortArray != null && dp.PortArray.Contains((ushort)boundPort))
                            return false;
                    }
                }
                else
                {
                    var localTorPeer = _qcc.TorCurrentPeer;
                    if (!string.IsNullOrEmpty(localTorPeer?.OnionAddress) && !string.IsNullOrEmpty(dp.OnionAddress) &&
                        string.Equals(localTorPeer.OnionAddress, dp.OnionAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            })
            .Select(dp =>
            {
                PeerStore.SavedPeer? savedPeer = null;
                if (dp.CertHash != null && _qcc.PeerStore != null)
                {
                    _qcc.PeerStore.TryGet(dp.CertHash, out savedPeer);
                }
                if (savedPeer == null)
                {
                    savedByHash.TryGetValue(dp.CertHashBase64, out savedPeer);
                }
                if (savedPeer == null && !string.IsNullOrEmpty(dp.NostrPubKey) && _qcc.PeerStore != null)
                {
                    _qcc.PeerStore.TryGetByNostrPubKey(dp.NostrPubKey, out savedPeer);
                }

                string displayName = !string.IsNullOrWhiteSpace(savedPeer?.Name)
                    ? savedPeer.Name
                    : (!string.IsNullOrWhiteSpace(dp.Name) && dp.Name != "Discovered Peer" ? dp.Name : "Discovered Peer");

                if (displayName == "Discovered Peer")
                {
                    var avail = _qcc.AvailablePeers.Values.FirstOrDefault(p => dp.CertHash != null && p.TryGetCertificateHash(out var h) && CryptographicOperations.FixedTimeEquals(h, dp.CertHash));
                    if (avail != null && !string.IsNullOrWhiteSpace(avail.Name) && avail.Name != "Peer" && avail.Name != "Discovered Peer")
                        displayName = avail.Name;
                }

                if (displayName == "Discovered Peer")
                {
                    var act = _qcc.ActiveInterrogations.Values.FirstOrDefault(s =>
                        dp.CertHash != null && s.Peer.TryGetCertificateHash(out var h) && CryptographicOperations.FixedTimeEquals(h, dp.CertHash)
                    );
                    if (act != null && !string.IsNullOrWhiteSpace(act.Peer.Name) && act.Peer.Name != "Saved Peer" && act.Peer.Name != "Peer" && act.Peer.Name != "Unknown" && act.Peer.Name != "Discovered Peer")
                        displayName = act.Peer.Name;
                }

                bool endpointChanged = false;
                if (savedPeer != null)
                {
                    var savedIps = new HashSet<string>((savedPeer.Addresses ?? Array.Empty<IPAddress>()).Select(a => a.ToString()), StringComparer.OrdinalIgnoreCase);
                    var dpIps = new HashSet<string>(dp.Addresses ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    endpointChanged = !savedIps.SetEquals(dpIps) ||
                                      !(savedPeer.PortArray ?? Array.Empty<ushort>()).SequenceEqual(dp.PortArray ?? Array.Empty<ushort>()) ||
                                      (!string.IsNullOrEmpty(dp.OnionAddress) && !string.Equals(dp.OnionAddress, savedPeer.OnionAddress, StringComparison.OrdinalIgnoreCase)) ||
                                      (dp.CertHash != null && !CryptographicOperations.FixedTimeEquals(dp.CertHash, savedPeer.CertHash));
                }

                return new
                {
                    id = dp.Id,
                    canonicalId = Utilities.ToCanonicalPeerId(dp.CertHash),
                    name = displayName,
                    discoveredName = dp.Name,
                    savedName = savedPeer?.Name,
                    isSaved = savedPeer != null,
                    endpointChanged,
                    autoConnect = savedPeer?.AutoConnect ?? false,
                    token = dp.Token,
                    certHash = dp.CertHashBase64,
                    nostrPubKey = dp.NostrPubKey ?? "",
                    addresses = dp.Addresses,
                    onionAddress = dp.OnionAddress,
                    ports = dp.PortArray ?? Array.Empty<ushort>(),
                    portMode = dp.ConnectionFlags?.PortMode.ToString() ?? (dp.PortArray == null || dp.PortArray.Length <= 1 ? "Single" : (dp.PortArray.Length > 8 ? "Range" : "Multiple")),
                    networkType = dp.NetworkType.ToString(),
                    isTor = dp.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(dp.OnionAddress),
                    source = dp.Source,
                    isCertVerified = dp.IsCertVerified,
                    certPublicKey = dp.CertPublicKeyBase64,
                    discoveredAt = dp.DiscoveredAt.ToString("HH:mm:ss"),
                    lastSeenSecondsAgo = (int)Math.Max(0, (DateTime.UtcNow - dp.LastSeen).TotalSeconds)
                };
            }).OrderByDescending(p => p.isSaved).ThenBy(p => p.name).ToList();

        string quickUri = !string.IsNullOrWhiteSpace(wanToken) ? $"qp://{wanToken}" : "";
        return new
        {
            node = new
            {
                name = _qcc.CurrentPeer?.Name ?? "LocalNode",
                id = _qcc.CurrentPeer?.Id.ToString() ?? "",
                canonicalId = _qcc.CurrentPeer?.TryGetCertificateHash(out var myHash) == true ? Utilities.ToCanonicalPeerId(myHash) : "",
                listenerPort = _qcc.LocalDiscoveryPort,
                ports = _qcc.CurrentPeer?.PortArray ?? Array.Empty<ushort>(),
                portMode = _qcc.CurrentPeer?.ConnectionFlags?.PortMode.ToString() ?? "Single",
                networkType = _qcc.CurrentPeer?.NetworkType.ToString() ?? "Unknown",
                wanToken,
                torToken,
                quickUri,
                endpoints = _qcc.CurrentPeer?.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>()
            },
            stun,
            preferences = prefs,
            discovery,
            wan,
            tor,
            lan,
            apps,
            peers,
            discoveredPeers,
            savedPeers,
            chatMessages,
            clipboardItems,
            pendingPetitions = petitions,
            activeInterrogations = interrogations,
            notifications = WebUiServer.UserNotifications.TakeLast(20).Select(n => new
            {
                id = n.Id,
                type = n.Type,
                message = n.Message,
                time = n.Timestamp.ToString("HH:mm:ss")
            }).ToArray(),
            logs = WebUiServer.EventLogs.TakeLast(80).ToList(),
            autoAcceptAll = _qcc.AutoAcceptConnections,
            trustedPeerCount = peers.Count(p => p.isTrusted),
            untrustedPeerCount = peers.Count(p => !p.isTrusted),
            quicTelemetry,
            quicSessions = quicTelemetry,

            // Compatibility aliases retained for existing integrations.
            nodeName = _qcc.CurrentPeer?.Name ?? "LocalNode",
            nodeId = _qcc.CurrentPeer?.Id.ToString() ?? "",
            token = wanToken,
            wanToken,
            torToken,
            quickUri,
            listenerPort = _qcc.LocalDiscoveryPort,
            availablePeers = peers,
            peerDiscovery = discovery,
            wanStatus = wan,
            lanStatus = lan,
            torStatus = tor,
            activeChats = ChatHandler.ActiveChats.Values.Select(s => new { peerId = s.Peer.Id.ToString(), peerName = s.Peer.Name ?? "" }).ToList(),
            activeVoiceCalls = VoiceCallHandler.ActiveCalls.Values.Select(s => new { peerId = s.Peer.Id.ToString(), peerName = s.Peer.Name ?? "" }).ToList(),
            registeredProtocols = _qcc.ProtocolHandlers.Select(pair => new { id = pair.Key.ToString(), name = pair.Value.ProtocolName }).ToList()
        };
    }

    /// <summary>
    /// Projects a <see cref="QuicConnectionTelemetry"/> instance into a serialized transport DTO.
    /// </summary>
    /// <param name="t">The source telemetry instance, or <c>null</c>.</param>
    /// <returns>A structured anonymous object containing formatted telemetry properties, or <c>null</c>.</returns>
    public static object? MapTelemetry(QuicConnectionTelemetry? t)
    {
        if (t == null) return null;
        return new
        {
            rttMs = Math.Round(t.RttMs, 2),
            minRttMs = Math.Round(t.MinRttMs, 2),
            maxRttMs = Math.Round(t.MaxRttMs, 2),
            rttVarianceMs = Math.Round(t.RttVarianceMs, 2),
            pathMtu = t.PathMtu,
            sendTotalPackets = t.SendTotalPackets,
            sendRetransmittablePackets = t.SendRetransmittablePackets,
            sendSuspectedLostPackets = t.SendSuspectedLostPackets,
            sendSpuriousLostPackets = t.SendSpuriousLostPackets,
            sendTotalBytes = t.SendTotalBytes,
            sendTotalStreamBytes = t.SendTotalStreamBytes,
            sendCongestionCount = t.SendCongestionCount,
            sendPersistentCongestionCount = t.SendPersistentCongestionCount,
            sendEcnCongestionCount = t.SendEcnCongestionCount,
            sendCongestionWindow = t.SendCongestionWindow,
            recvTotalPackets = t.RecvTotalPackets,
            recvReorderedPackets = t.RecvReorderedPackets,
            recvDroppedPackets = t.RecvDroppedPackets,
            recvDuplicatePackets = t.RecvDuplicatePackets,
            recvTotalBytes = t.RecvTotalBytes,
            recvTotalStreamBytes = t.RecvTotalStreamBytes,
            recvDecryptionFailures = t.RecvDecryptionFailures,
            recvValidAckFrames = t.RecvValidAckFrames,
            keyUpdateCount = t.KeyUpdateCount,
            destCidUpdateCount = t.DestCidUpdateCount,
            handshakeHopLimitTtl = t.HandshakeHopLimitTtl,
            handshakeDurationMs = Math.Round(t.HandshakeDurationMs, 2),
            handshakeClientFlight1Bytes = t.HandshakeClientFlight1Bytes,
            handshakeServerFlight1Bytes = t.HandshakeServerFlight1Bytes,
            handshakeClientFlight2Bytes = t.HandshakeClientFlight2Bytes,
            congestionAlgorithm = t.CongestionAlgorithmName,
            nativeLocalEndpoint = t.NativeLocalEndPoint?.ToString() ?? "",
            nativeRemoteEndpoint = t.NativeRemoteEndPoint?.ToString() ?? "",
            cipherSuite = t.CipherSuite ?? "",
            alpn = t.Alpn ?? "",
            sslProtocol = t.SslProtocol ?? "",
            targetHostName = t.TargetHostName ?? "",
            packetLossRatio = Math.Round(t.PacketLossRatio, 4),
            packetLossPercentage = Math.Round(t.PacketLossRatio * 100, 2),
            timestamp = t.TimestampUtc.ToString("o")
        };
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/status" && req.HttpMethod == "GET")
        {
            string include = req.QueryString["include"] ?? "";
            bool includeChat = include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("chat", StringComparer.OrdinalIgnoreCase);
            bool includeClipboard = include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("clipboard", StringComparer.OrdinalIgnoreCase);
            await WebUiContext.WriteJsonAsync(resp, GetStatusObject(includeChat, includeClipboard)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/telemetry" && req.HttpMethod == "GET")
        {
            var activeSessionTelemetry = _qcc.GetActiveSessionsTelemetry();
            var telemetryList = activeSessionTelemetry.Select(s => new
            {
                peerId = s.PeerId.ToString(),
                peerName = s.PeerName ?? "Peer",
                protocolId = s.ProtocolId.ToString(),
                protocolName = s.ProtocolName ?? s.ProtocolId.ToString(),
                transportType = s.TransportType.ToString(),
                telemetry = MapTelemetry(s.Telemetry),
                sampleTime = s.SampleTimeUtc.ToString("o")
            }).ToList();

            var peerTelemetry = _qcc.AvailablePeers.Values
                .Where(p => p.LastTelemetry != null)
                .Select(p => new
                {
                    peerId = p.Id.ToString(),
                    peerName = p.Name ?? "Peer",
                    endpoint = p.ActiveEndPoint?.ToString() ?? "",
                    telemetry = MapTelemetry(p.LastTelemetry)
                }).ToList();

            await WebUiContext.WriteJsonAsync(resp, new
            {
                success = true,
                timestamp = DateTime.UtcNow.ToString("o"),
                sessionCount = telemetryList.Count,
                sessions = telemetryList,
                peers = peerTelemetry
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/quic/congestion-control" && req.HttpMethod == "POST")
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            string body = await reader.ReadToEndAsync().ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;

            string peerIdStr = root.TryGetProperty("peerId", out var pProp) ? pProp.GetString() ?? "" : "";
            string protocolIdStr = root.TryGetProperty("protocolId", out var protoProp) ? protoProp.GetString() ?? "" : "";
            string algoStr = root.TryGetProperty("algorithm", out var algoProp) ? algoProp.GetString() ?? "" : "";

            if (!Guid.TryParse(peerIdStr, out var peerId))
            {
                resp.StatusCode = 400;
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Invalid peer identifier." }).ConfigureAwait(false);
                return true;
            }

            QuicCongestionAlgorithm targetAlgorithm = string.Equals(algoStr, "bbr", StringComparison.OrdinalIgnoreCase)
                ? QuicCongestionAlgorithm.Bbr
                : QuicCongestionAlgorithm.Cubic;

            bool switched;
            if (Guid.TryParse(protocolIdStr, out var protocolId) && protocolId != Guid.Empty)
            {
                switched = _qcc.TrySetSessionCongestionControl(peerId, protocolId, targetAlgorithm);
            }
            else
            {
                switched = _qcc.TrySetPeerCongestionControl(peerId, targetAlgorithm);
            }

            await WebUiContext.WriteJsonAsync(resp, new
            {
                success = switched,
                algorithm = targetAlgorithm.ToString(),
                peerId = peerId.ToString(),
                protocolId = protocolIdStr
            }).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
