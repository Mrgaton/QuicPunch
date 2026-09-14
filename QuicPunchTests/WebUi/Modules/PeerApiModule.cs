using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunchTests.Protocols;
using QuicPunchTests.Settings;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class PeerApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly VirtualLanHandler _lanHandler;
    private readonly VoiceCallHandler _voiceHandler;
    private readonly SpeedTestHandler _speedTestHandler;
    private readonly AppPreferencesStore _preferences;
    private readonly WebUiWebSocketHub _hub;
    private readonly CancellationTokenSource _cts;

    public ConcurrentDictionary<Guid, WebUiServer.PendingPetitionItem> PendingPetitions { get; } = new();
    public ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> InFlightConnections { get; } = new();
    public ConcurrentQueue<WebUiServer.ClipboardItem> ClipboardItems { get; } = new();

    public PeerApiModule(
        QuicPunch.QuicPunch qcc,
        ChatHandler chatHandler,
        VirtualLanHandler lanHandler,
        VoiceCallHandler voiceHandler,
        SpeedTestHandler speedTestHandler,
        AppPreferencesStore preferences,
        WebUiWebSocketHub hub,
        CancellationTokenSource cts)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _lanHandler = lanHandler;
        _voiceHandler = voiceHandler;
        _speedTestHandler = speedTestHandler;
        _preferences = preferences;
        _hub = hub;
        _cts = cts;

        _qcc.Manager.HandshakeRequested += HandleHandshakeRequestAsync;
        _qcc.OnPeerAvailable += peer =>
        {
            WebUiServer.LogEvent($"[DISCOVERY] Peer available: {peer.Name} ({peer.Id})");
            _hub.Broadcast("peer_available", new { peerId = peer.Id.ToString(), name = peer.Name });
        };
        _qcc.OnPeerDisconnected += peer =>
        {
            WebUiServer.LogEvent($"[NETWORK] Peer {peer.Name ?? "Unknown"} disconnected");
            _hub.Broadcast("peer_network_disconnected", new { peerId = peer.Id.ToString(), name = peer.Name });
        };
    }

    public Task<HandshakeDecision> HandleHandshakeRequestAsync(HandshakeRequest request, CancellationToken ct)
    {
        PeerInfo? peer = ResolvePeer(request.PeerId, request.CertHash, request.RemoteEndPoint);
        string protocolName = _qcc.ProtocolHandlers.TryGetValue(request.ProtocolId, out var handler) ? handler.ProtocolName : "Connection";
        string peerName = peer?.Name ?? (request.PeerId != Guid.Empty ? $"Peer-{request.PeerId.ToString()[..6]}" : request.RemoteEndPoint.ToString());
        Guid peerId = peer?.Id ?? request.PeerId;

        if (peer == null || !_qcc.IsTrustedPeer(peer))
        {
            WebUiServer.LogEvent($"[SECURITY] Blocked {protocolName} request from untrusted peer {peerName}");
            return Task.FromResult(new HandshakeDecision(false, null, CancellationToken.None));
        }

        if (PendingPetitions.TryGetValue(request.Id, out var existing))
            return existing.Tcs.Task;

        var samePeer = PendingPetitions.Values.FirstOrDefault(v => v.PeerId == peerId && v.ProtocolId == request.ProtocolId);
        if (samePeer != null)
            return samePeer.Tcs.Task;

        var tcs = new TaskCompletionSource<HandshakeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingPetitions[request.Id] = new WebUiServer.PendingPetitionItem(request.Id, request.ProtocolId, protocolName, peerName, peerId, tcs);
        WebUiServer.LogEvent($"[PETITION] {protocolName} request from {peerName}");
        _hub.Broadcast("petition_received", new
        {
            requestId = request.Id.ToString(),
            peerId = peerId.ToString(),
            peerName,
            protocolId = request.ProtocolId.ToString(),
            protocolName
        });

        ct.Register(() =>
        {
            if (PendingPetitions.TryRemove(request.Id, out var expired))
                expired.Tcs.TrySetResult(new HandshakeDecision(false, null, CancellationToken.None));
        });
        return tcs.Task;
    }

    public PeerInfo? ResolvePeer(Guid peerId, byte[]? certHash, IPEndPoint remoteEndPoint)
    {
        if (peerId != Guid.Empty && _qcc.AvailablePeers.TryGetValue(peerId, out var byId)) return byId;
        if (certHash is { Length: > 0 })
        {
            var byHash = _qcc.AvailablePeers.Values.FirstOrDefault(p => p.TryGetCertificateHash(out var hash) && CryptographicOperations.FixedTimeEquals(hash, certHash));
            if (byHash != null) return byHash;
        }
        return _qcc.AvailablePeers.Values.FirstOrDefault(p => p.ActiveEndPoint?.Equals(remoteEndPoint) == true);
    }

    public void ClearPeerPetitions(Guid peerId, Guid? protocolId = null)
    {
        foreach (var key in PendingPetitions.Where(pair => pair.Value.PeerId == peerId && (!protocolId.HasValue || pair.Value.ProtocolId == protocolId.Value)).Select(pair => pair.Key).ToList())
            PendingPetitions.TryRemove(key, out _);
    }

    public string ResolveProtocolName(Guid protocolId)
    {
        if (protocolId == _chatHandler.ProtocolId) return "Direct Chat";
        if (protocolId == _voiceHandler.ProtocolId) return "Voice Studio";
        if (protocolId == _lanHandler.ProtocolId) return "LAN Bridge";
        return _qcc.ProtocolHandlers.TryGetValue(protocolId, out var handler) ? handler.ProtocolName : "Application";
    }

    public Guid ResolveProtocolId(string value)
    {
        if (Guid.TryParse(value, out var parsed)) return parsed;
        return value.Trim().ToLowerInvariant() switch
        {
            "chat" or "direct chat" => _chatHandler.ProtocolId,
            "voice" or "call" or "voice studio" => _voiceHandler.ProtocolId,
            "lan" or "vpn" or "lan bridge" or "friendslan" => _lanHandler.ProtocolId,
            "speedtest" or "speed test" or "benchmark" => _speedTestHandler.ProtocolId,
            _ => throw new InvalidDataException("Unknown application protocol.")
        };
    }

    public (bool Reused, bool InProgress) StartProtocolConnection(PeerInfo peer, Guid protocolId)
    {
        bool connected = protocolId == _chatHandler.ProtocolId && ChatHandler.ActiveChats.ContainsKey(peer.Id)
            || protocolId == _voiceHandler.ProtocolId && VoiceCallHandler.ActiveCalls.ContainsKey(peer.Id)
            || protocolId == _lanHandler.ProtocolId && _lanHandler.ActivePeers.Values.Any(p => p.Peer.Id == peer.Id)
            || protocolId == _speedTestHandler.ProtocolId && SpeedTestHandler.ActiveSessions.ContainsKey(peer.Id)
            || _qcc.HasActiveProtocolSession(peer.Id, protocolId);
        if (connected) return (true, false);

        var key = (peer.Id, protocolId);
        if (InFlightConnections.ContainsKey(key) || _qcc.IsConnectionInFlight(peer.Id, protocolId))
            return (false, true);

        Guid attemptId = Guid.NewGuid();
        if (!InFlightConnections.TryAdd(key, attemptId))
            return (false, true);

        ClearPeerPetitions(peer.Id, protocolId);
        _ = Task.Run(async () =>
        {
            try
            {
                await _qcc.InitQuicConnection(protocolId, peer, 0, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                string protoName = ResolveProtocolName(protocolId);
                string peerName = peer.Name ?? "Peer";
                string err = $"Failed to connect {protoName} with {peerName}: {ex.Message}";
                WebUiServer.LogEvent($"[APP] {err}");
                WebUiServer.AddNotification("error", err);
            }
            finally
            {
                InFlightConnections.TryRemove(new KeyValuePair<(Guid PeerId, Guid ProtocolId), Guid>(key, attemptId));
            }
        });
        return (false, false);
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/discovery" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool enabled = doc.RootElement.GetProperty("enabled").GetBoolean();
            string type = doc.RootElement.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? (typeEl.GetString() ?? "wan").ToLowerInvariant()
                : "wan";

            if (type == "tor")
            {
                await _qcc.Discovery.SetTorPeerDiscoveryEnabledAsync(enabled, _cts.Token).ConfigureAwait(false);
                _preferences.Update(p => p.TorNostrDiscoveryEnabled = enabled);
                WebUiServer.LogEvent($"[DISCOVERY] Tor Nostr discovery {(enabled ? "enabled" : "disabled")}");
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new { success = true, type = "tor", enabled, running = _qcc.Discovery.TorNostrDiscovery?.IsRunning ?? false }).ConfigureAwait(false);
            }
            else
            {
                await _qcc.Discovery.SetWanPeerDiscoveryEnabledAsync(enabled, _cts.Token).ConfigureAwait(false);
                _preferences.Update(p => p.WanNostrDiscoveryEnabled = enabled);
                WebUiServer.LogEvent($"[DISCOVERY] WAN Nostr discovery {(enabled ? "enabled" : "disabled")}");
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new { success = true, type = "wan", enabled, running = _qcc.Discovery.WanNostrDiscovery?.IsRunning ?? false }).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/peer/trust" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool trust = !doc.RootElement.TryGetProperty("trust", out var trustEl) || trustEl.GetBoolean();
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer) || !peer.TryGetCertificateHash(out var certHash))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Authenticated peer not found." }, 404).ConfigureAwait(false);
                return true;
            }

            if (trust)
            {
                _qcc.TrustPeer(certHash);
                WebUiServer.LogEvent($"[TRUST] Trusted {peer.Name}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _qcc.EnsureMultiplexerConnectedAsync(peer).ConfigureAwait(false);
                        WebUiServer.BroadcastStatusUpdate();
                    }
                    catch (Exception ex)
                    {
                        WebUiServer.LogEvent($"[QUIC CONNECT] Notice: could not immediately connect QUIC to {peer.Name}: {ex.Message}");
                    }
                });
            }
            else
            {
                if (_qcc.PeerStore != null && _qcc.PeerStore.TryGet(certHash, out _))
                {
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Remove the peer from Saved Peers before untrusting it." }, 409).ConfigureAwait(false);
                    return true;
                }
                _qcc.SetPeerAutoAccept(peer.Id, false);
                _qcc.UntrustPeer(certHash);
                WebUiServer.LogEvent($"[TRUST] Removed trust from {peer.Name}");
            }
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, trusted = trust }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/peer/connect" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Peer not found." }, 404).ConfigureAwait(false);
                return true;
            }

            try
            {
                await _qcc.EnsureMultiplexerConnectedAsync(peer, cancellationToken: _cts.Token).ConfigureAwait(false);
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new { success = true, connected = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = ex.Message }, 500).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/save-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool save = !doc.RootElement.TryGetProperty("save", out var saveEl) || saveEl.GetBoolean();
            bool autoConnect = !doc.RootElement.TryGetProperty("autoConnect", out var autoEl) || autoEl.GetBoolean();
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer) || !peer.TryGetCertificateHash(out var hash))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Peer not found." }, 404).ConfigureAwait(false);
                return true;
            }
            if (save)
            {
                if (!_qcc.IsTrustedPeer(peer))
                {
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Trust this peer before saving it." }, 403).ConfigureAwait(false);
                    return true;
                }
                bool saved = _qcc.SavePeer(peer, autoConnect);
                await WebUiContext.WriteJsonAsync(resp, new { success = saved, saved, autoConnect }, saved ? 200 : 500).ConfigureAwait(false);
            }
            else
            {
                bool removed = _qcc.RemoveSavedPeer(hash);
                await WebUiContext.WriteJsonAsync(resp, new { success = removed, saved = false }, removed ? 200 : 404).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/saved-peer-update" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] certHash = WebUiContext.ParseCertHash(certHashStr);
            if (certHash.Length != 32) throw new InvalidDataException("Certificate hash must be exactly 32 bytes.");

            string name = doc.RootElement.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() ?? "" : "";
            string onion = doc.RootElement.TryGetProperty("onionAddress", out var oEl) ? oEl.GetString()?.Trim() ?? "" : "";
            List<ushort> ports = new();
            if (doc.RootElement.TryGetProperty("ports", out var pEl))
            {
                if (pEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pEl.EnumerateArray())
                    {
                        if (item.TryGetInt32(out var pVal) && pVal >= 1 && pVal <= 65535)
                            ports.Add((ushort)pVal);
                    }
                }
                else if (pEl.ValueKind == JsonValueKind.String)
                {
                    var str = pEl.GetString() ?? "";
                    foreach (var part in str.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (part.Contains('-'))
                        {
                            var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            if (rangeParts.Length == 2 && ushort.TryParse(rangeParts[0], out var start) && ushort.TryParse(rangeParts[1], out var end) && start >= 1 && end >= start)
                            {
                                for (int p = start; p <= end; p++) ports.Add((ushort)p);
                            }
                        }
                        else if (ushort.TryParse(part, out var singlePort) && singlePort >= 1)
                        {
                            ports.Add(singlePort);
                        }
                    }
                }
            }
            ports = ports.Distinct().OrderBy(p => p).ToList();
            if (ports.Count == 0)
                ports.Add(443);

            bool autoConnect = !doc.RootElement.TryGetProperty("autoConnect", out var acEl) || acEl.GetBoolean();
            string netTypeStr = doc.RootElement.TryGetProperty("networkType", out var ntEl) ? ntEl.GetString() ?? "" : "";

            var addressesRaw = doc.RootElement.TryGetProperty("addresses", out var aEl) ? aEl.GetString() ?? "" : "";
            var addresses = addressesRaw
                .Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
                .Where(ip => ip != null && Utilities.IsValidPeerAddress(ip))
                .Select(ip => ip!)
                .Distinct()
                .ToList();

            if (addresses.Count == 0 && !string.IsNullOrEmpty(onion))
            {
                addresses.Add(IPAddress.Loopback);
            }

            QuicPunch.QuicPunch.NetworkType networkType = !string.IsNullOrEmpty(onion)
                ? QuicPunch.QuicPunch.NetworkType.Tor
                : QuicPunch.QuicPunch.NetworkType.Static;

            if (_qcc.PeerStore == null)
                throw new InvalidOperationException("PeerStore is not initialized.");

            _qcc.TrustPeer(certHash);
            bool saved = _qcc.PeerStore.AddOrUpdate(
                addresses: addresses,
                portArray: ports.ToArray(),
                certificate: certHash,
                ecdhPublicKey: null,
                name: string.IsNullOrEmpty(name) ? null : name,
                onionAddress: string.IsNullOrEmpty(onion) ? null : onion,
                autoConnect: autoConnect,
                save: true,
                networkType: networkType);

            WebUiServer.LogEvent($"[PEER STORE] Saved peer updated: {name} ({Convert.ToBase64String(certHash)})");
            await WebUiContext.WriteJsonAsync(resp, new { success = true, saved }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-saved-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] certHash = WebUiContext.ParseCertHash(certHashStr);
            if (_qcc.PeerStore == null || !_qcc.PeerStore.TryGet(certHash, out var sp) || sp == null)
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Saved peer not found." }, 404).ConfigureAwait(false);
                return true;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!string.IsNullOrEmpty(sp.OnionAddress) && _qcc.IsTorStarted)
                    {
                        WebUiServer.LogEvent($"[PEER] Connecting via Tor to saved peer {sp.Name ?? sp.OnionAddress}...");
                        ushort torPort = sp.PortArray.Length > 0 ? sp.PortArray[0] : (ushort)443;
                        await _qcc.ConnectTorAsync(sp.OnionAddress, torPort, _cts.Token).ConfigureAwait(false);
                    }
                    if (sp.Addresses != null && sp.Addresses.Length > 0 && sp.Addresses.Any(a => !IPAddress.IsLoopback(a)))
                    {
                        var peerInfo = QuicPunch.QuicPunch.CreatePeerInfoFromSavedPeer(sp);
                        WebUiServer.LogEvent($"[PEER] Interrogating saved WAN peer {sp.Name ?? string.Join(", ", sp.Addresses.Select(a => a.ToString()))}...");
                        await _qcc.PeerInterrogation(peerInfo, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { WebUiServer.LogEvent($"[CONNECT SAVED PEER] Error: {ex.Message}"); }
            });

            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/toggle-saved-peer-autoconnect" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] certHash = WebUiContext.ParseCertHash(certHashStr);
            bool autoConnect = doc.RootElement.GetProperty("autoConnect").GetBoolean();

            if (_qcc.PeerStore == null) throw new InvalidOperationException("PeerStore is not initialized.");
            bool toggled = _qcc.PeerStore.ToggleAutoConnect(certHash, autoConnect, save: true);
            await WebUiContext.WriteJsonAsync(resp, new { success = toggled, autoConnect }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/saved-peer-delete" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] certHash = WebUiContext.ParseCertHash(certHashStr);

            bool removed = _qcc.RemoveSavedPeer(certHash);
            WebUiServer.LogEvent($"[PEER STORE] Deleted saved peer with hash {Convert.ToBase64String(certHash)}");
            await WebUiContext.WriteJsonAsync(resp, new { success = true, removed }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/saved-peers-clear-offline" && req.HttpMethod == "POST")
        {
            if (_qcc.PeerStore == null) throw new InvalidOperationException("PeerStore is not initialized.");
            int removedCount = 0;
            var savedPeers = _qcc.PeerStore.SavedPeers.ToList();
            foreach (var sp in savedPeers)
            {
                bool isOnline = _qcc.AvailablePeers.Values.Any(p =>
                    p.TryGetCertificateHash(out var hash) &&
                    CryptographicOperations.FixedTimeEquals(hash, sp.CertHash) &&
                    p.IsConnectedAndResponsive(TimeSpan.FromMinutes(1)));

                if (!isOnline)
                {
                    if (_qcc.RemoveSavedPeer(sp.CertHash))
                        removedCount++;
                }
            }
            WebUiServer.LogEvent($"[PEER STORE] Cleared {removedCount} offline saved peer(s).");
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, removedCount }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/save-all-peers" && req.HttpMethod == "POST")
        {
            int count = 0;
            foreach (var peer in _qcc.AvailablePeers.Values)
                if (_qcc.IsTrustedPeer(peer) && _qcc.SavePeer(peer, true)) count++;
            await WebUiContext.WriteJsonAsync(resp, new { success = true, savedCount = count }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/settings/auto-accept" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool enabled = doc.RootElement.GetProperty("autoAcceptAll").GetBoolean();
            _qcc.SetAutoAcceptAll(enabled);
            _preferences.Update(p => p.AutoAcceptTrusted = enabled);
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, autoAcceptAll = enabled }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/peer/auto-accept" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool enabled = doc.RootElement.GetProperty("autoAccept").GetBoolean();
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Peer not found." }, 404).ConfigureAwait(false);
                return true;
            }
            if (enabled && !_qcc.IsTrustedPeer(peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Trust this peer before enabling auto-accept." }, 403).ConfigureAwait(false);
                return true;
            }
            _qcc.SetPeerAutoAccept(peerId, enabled);
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, peerId, autoAccept = enabled }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-token" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string token = doc.RootElement.GetProperty("token").GetString()?.Trim() ?? "";
            if (token.Length == 0) throw new InvalidDataException("Token is required.");

            string action = doc.RootElement.TryGetProperty("action", out var actEl) ? actEl.GetString()?.ToLowerInvariant() ?? "check" : "check";

            PeerInfo p;
            try
            {
                p = QuicPunch.Helpers.Utilities.DecodeEndpointToken(token);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Token format is invalid: {ex.Message}");
            }

            if (p.TryGetCertificateHash(out var certHash) && _qcc.PeerStore != null && _qcc.PeerStore.TryGet(certHash, out var savedPeer) && savedPeer != null)
            {
                var savedIps = savedPeer.Addresses?.Select(a => a.ToString()).OrderBy(x => x).ToList() ?? new List<string>();
                var tokenIps = p.Addresses?.Select(a => a.ToString()).OrderBy(x => x).ToList() ?? new List<string>();
                bool ipsDifferent = !savedIps.SequenceEqual(tokenIps, StringComparer.OrdinalIgnoreCase);
                bool portsDifferent = !(p.PortArray ?? Array.Empty<ushort>()).SequenceEqual(savedPeer.PortArray ?? Array.Empty<ushort>());
                bool onionDifferent = !string.Equals(p.OnionAddress ?? "", savedPeer.OnionAddress ?? "", StringComparison.OrdinalIgnoreCase);
                bool netTypeDifferent = p.NetworkType != savedPeer.NetworkType;
                bool hasDifferences = ipsDifferent || portsDifferent || onionDifferent || netTypeDifferent;

                if (hasDifferences && action == "check")
                {
                    await WebUiContext.WriteJsonAsync(resp, new
                    {
                        success = true,
                        requiresConfirmation = true,
                        token,
                        certHash = Convert.ToBase64String(certHash),
                        savedPeer = new
                        {
                            name = savedPeer.Name ?? "Peer",
                            certHash = Convert.ToBase64String(savedPeer.CertHash),
                            addresses = savedPeer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                            ports = savedPeer.PortArray ?? Array.Empty<ushort>(),
                            portMode = savedPeer.ConnectionFlags?.PortMode.ToString() ?? "Single",
                            onionAddress = savedPeer.OnionAddress ?? "",
                            networkType = savedPeer.NetworkType.ToString(),
                            autoConnect = savedPeer.AutoConnect
                        },
                        newTokenPeer = new
                        {
                            name = p.Name ?? "",
                            addresses = p.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                            ports = p.PortArray ?? Array.Empty<ushort>(),
                            portMode = p.ConnectionFlags?.PortMode.ToString() ?? "Single",
                            onionAddress = p.OnionAddress ?? "",
                            networkType = p.NetworkType.ToString()
                        }
                    }).ConfigureAwait(false);
                    return true;
                }

                if (action == "update_and_connect")
                {
                    bool autoConnect = doc.RootElement.TryGetProperty("autoConnect", out var acEl) ? acEl.GetBoolean() : savedPeer.AutoConnect;
                    string? customName = doc.RootElement.TryGetProperty("name", out var nEl) && !string.IsNullOrWhiteSpace(nEl.GetString())
                        ? nEl.GetString()
                        : (savedPeer.Name ?? p.Name);

                    var newAddrs = (p.Addresses != null && p.Addresses.Length > 0) ? p.Addresses : (savedPeer.Addresses ?? new[] { IPAddress.Loopback });
                    ushort[] newPorts = (p.PortArray != null && p.PortArray.Length > 0) ? p.PortArray : (savedPeer.PortArray ?? Array.Empty<ushort>());
                    string? newOnion = !string.IsNullOrEmpty(p.OnionAddress) ? p.OnionAddress : savedPeer.OnionAddress;
                    var newNt = p.NetworkType;
                    var newFlags = p.ConnectionFlags ?? savedPeer.ConnectionFlags;

                    _qcc.TrustPeer(certHash);
                    _qcc.PeerStore.AddOrUpdate(
                        addresses: newAddrs,
                        portArray: newPorts,
                        certificate: certHash,
                        ecdhPublicKey: p.EcdhPublicKey ?? savedPeer.EcdhPublicKey,
                        name: customName,
                        onionAddress: newOnion,
                        autoConnect: autoConnect,
                        save: true,
                        networkType: newNt,
                        connectionFlags: newFlags
                    );
                    WebUiServer.LogEvent($"[PEER STORE] Updated saved peer '{customName}' ({Convert.ToBase64String(certHash)}) from new token.");
                }
            }

            await _qcc.PeerInterrogation(token, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = true, requiresConfirmation = false, connected = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-discovered-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string token = doc.RootElement.TryGetProperty("token", out var tEl) ? tEl.GetString()?.Trim() ?? "" : "";
            string certHashStr = doc.RootElement.TryGetProperty("certHash", out var cEl) ? cEl.GetString()?.Trim() ?? "" : "";
            bool updateSaved = doc.RootElement.TryGetProperty("updateSavedPeer", out var usp) && usp.GetBoolean();

            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(certHashStr))
            {
                byte[] hash = WebUiContext.ParseCertHash(certHashStr);
                string hexKey = Convert.ToHexString(hash);
                if (_qcc.Discovery.DiscoveredPeers.TryGetValue(hexKey, out var dp))
                    token = dp.Token;
            }

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("Token or valid discovered peer hash is required.");

            if (updateSaved && !string.IsNullOrEmpty(certHashStr) && _qcc.PeerStore != null)
            {
                byte[] hash = WebUiContext.ParseCertHash(certHashStr);
                if (_qcc.PeerStore.TryGet(hash, out var savedPeer) && savedPeer != null)
                {
                    try
                    {
                        var p = QuicPunch.Helpers.Utilities.DecodeEndpointToken(token);
                        var newAddrs = (p.Addresses != null && p.Addresses.Length > 0) ? p.Addresses : (savedPeer.Addresses ?? new[] { IPAddress.Loopback });
                        ushort[] newPorts = (p.PortArray != null && p.PortArray.Length > 0) ? p.PortArray : (savedPeer.PortArray ?? Array.Empty<ushort>());
                        string? newOnion = !string.IsNullOrEmpty(p.OnionAddress) ? p.OnionAddress : savedPeer.OnionAddress;
                        var newNt = p.NetworkType;
                        var newFlags = p.ConnectionFlags ?? savedPeer.ConnectionFlags;

                        _qcc.TrustPeer(hash);
                        _qcc.PeerStore.AddOrUpdate(
                            addresses: newAddrs,
                            portArray: newPorts,
                            certificate: hash,
                            ecdhPublicKey: p.EcdhPublicKey ?? savedPeer.EcdhPublicKey,
                            name: savedPeer.Name,
                            onionAddress: newOnion,
                            autoConnect: savedPeer.AutoConnect,
                            save: true,
                            networkType: newNt,
                            connectionFlags: newFlags
                        );
                        WebUiServer.LogEvent($"[PEER STORE] Updated saved peer '{savedPeer.Name}' with newly discovered endpoints.");
                    }
                    catch { }
                }
            }

            WebUiServer.LogEvent("[DISCOVERY] Manually initiating connection to discovered peer...");
            _ = Task.Run(async () =>
            {
                try { await _qcc.PeerInterrogation(token, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { WebUiServer.LogEvent($"[CONNECT DISCOVERED] Interrogation failed: {ex.Message}"); }
            });

            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/dismiss-discovered-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] hash = WebUiContext.ParseCertHash(certHashStr);
            string hexKey = Convert.ToHexString(hash);
            _qcc.Discovery.DiscoveredPeers.TryRemove(hexKey, out _);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-all-discovered" && req.HttpMethod == "POST")
        {
            int count = 0;
            foreach (var dp in _qcc.Discovery.DiscoveredPeers.Values.ToArray())
            {
                if (!string.IsNullOrWhiteSpace(dp.Token))
                {
                    count++;
                    string tok = dp.Token;
                    _ = Task.Run(async () =>
                    {
                        try { await _qcc.PeerInterrogation(tok, _cts.Token).ConfigureAwait(false); } catch { }
                    });
                }
            }
            await WebUiContext.WriteJsonAsync(resp, new { success = true, count }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/disconnect-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            _qcc.DisconnectPeer(peerId);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            string protocol = doc.RootElement.TryGetProperty("protocol", out var pEl) ? pEl.GetString() ?? "chat" :
                (doc.RootElement.TryGetProperty("protocolId", out var idEl) ? idEl.GetString() ?? "chat" : "chat");
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Peer not found." }, 404).ConfigureAwait(false);
                return true;
            }
            if (!_qcc.IsTrustedPeer(peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Trust this peer before opening application protocols." }, 403).ConfigureAwait(false);
                return true;
            }
            Guid protocolId = ResolveProtocolId(protocol);
            if (protocolId == _lanHandler.ProtocolId && !_lanHandler.IsActive)
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Enable LAN Bridge before joining peers." }, 409).ConfigureAwait(false);
                return true;
            }
            var result = StartProtocolConnection(peer, protocolId);
            await WebUiContext.WriteJsonAsync(resp, new { success = true, result.Reused, result.InProgress }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/clipboard-send" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            string text = doc.RootElement.GetProperty("text").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Clipboard text cannot be empty.");
            if (text.Length > WebUiContext.MaxClipboardChars) text = text[..WebUiContext.MaxClipboardChars];

            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Peer not found." }, 404).ConfigureAwait(false);
                return true;
            }

            var item = new WebUiServer.ClipboardItem(peer.Id.ToString(), peer.Name ?? "Peer", text, DateTime.Now, true);
            ClipboardItems.Enqueue(item);
            while (ClipboardItems.Count > 500) ClipboardItems.TryDequeue(out _);

            WebUiServer.LogEvent($"[CLIPBOARD] Copied {text.Length} character(s) to clipboard for {peer.Name}");
            _hub.Broadcast("clipboard_item", item);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/respond-petition" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid requestId = WebUiContext.ParseGuid(doc.RootElement, "requestId");
            bool accept = doc.RootElement.TryGetProperty("accept", out var acceptEl) && acceptEl.GetBoolean();
            if (!PendingPetitions.TryRemove(requestId, out var petition))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Petition not found or expired." }, 404).ConfigureAwait(false);
                return true;
            }
            petition.Tcs.TrySetResult(new HandshakeDecision(accept, accept ? (ushort)0 : null, CancellationToken.None));
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/wan-start" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            int port = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
            if (port < 0 || port > ushort.MaxValue) throw new InvalidDataException("Invalid WAN listener port.");
            _preferences.Update(p => p.WanEnabled = true);
            if (!_qcc.IsWanStarted)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        WebUiServer.LogEvent("[WAN] Starting WAN UDP service...");
                        await _qcc.StartWanAsync((ushort)port, cancellationToken: _cts.Token).ConfigureAwait(false);
                        WebUiServer.LogEvent($"[WAN] WAN UDP service ready on port {_qcc.LocalDiscoveryPort}");
                        _hub.Broadcast("wan_started", new { port = _qcc.LocalDiscoveryPort });
                        WebUiServer.BroadcastStatusUpdate();
                    }
                    catch (Exception ex) { WebUiServer.LogEvent($"[WAN] Start failed: {ex.Message}"); WebUiServer.BroadcastStatusUpdate(); }
                });
            }
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, starting = !_qcc.IsWanStarted }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/wan-stop" && req.HttpMethod == "POST")
        {
            _preferences.Update(p => p.WanEnabled = false);
            await _qcc.StopWanAsync().ConfigureAwait(false);
            WebUiServer.LogEvent("[WAN] WAN UDP service stopped.");
            _hub.Broadcast("wan_stopped", new { });
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/tor-start" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            int port = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 0;
            if (port < 0 || port > ushort.MaxValue) throw new InvalidDataException("Invalid Tor virtual port.");
            int resolvedPort = port > 0 ? port : _preferences.Snapshot().TorVirtualPort;
            _preferences.Update(p => { p.TorEnabled = true; if (port > 0) p.TorVirtualPort = port; });
            if (!_qcc.IsTorStarted)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        WebUiServer.LogEvent("[TOR] Starting Tor hidden service...");
                        await _qcc.StartTorAsync(resolvedPort, cancellationToken: _cts.Token).ConfigureAwait(false);
                        WebUiServer.LogEvent($"[TOR] Hidden service ready at {_qcc.TorOnionAddress}");
                        _hub.Broadcast("tor_started", new { onion = _qcc.TorOnionAddress });
                        WebUiServer.BroadcastStatusUpdate();
                    }
                    catch (Exception ex) { WebUiServer.LogEvent($"[TOR] Start failed: {ex.Message}"); WebUiServer.BroadcastStatusUpdate(); }
                });
            }
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, starting = !_qcc.IsTorStarted }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/tor-stop" && req.HttpMethod == "POST")
        {
            _preferences.Update(p => p.TorEnabled = false);
            await _qcc.StopTorAsync().ConfigureAwait(false);
            WebUiServer.LogEvent("[TOR] Tor service stopped.");
            _hub.Broadcast("tor_stopped", new { });
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/tor-connect" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string onion = doc.RootElement.GetProperty("onion").GetString()?.Trim() ?? "";
            int port = doc.RootElement.TryGetProperty("port", out var portEl) ? portEl.GetInt32() : 443;
            if (onion.Length == 0) throw new InvalidDataException("Onion address is required.");
            if (!_qcc.IsTorStarted) throw new InvalidOperationException("Tor is not running.");
            if (onion.Contains(':'))
            {
                string[] parts = onion.Split(':', 2);
                onion = parts[0];
                if (int.TryParse(parts[1], out int parsed)) port = parsed;
            }
            if (port is < 1 or > ushort.MaxValue) throw new InvalidDataException("Port must be between 1 and 65535.");
            await _qcc.ConnectTorAsync(onion, (ushort)port, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/tor-newnym" && req.HttpMethod == "POST")
        {
            if (_qcc.TorManager == null) throw new InvalidOperationException("Tor is not running.");
            await _qcc.TorManager.RequestNewCircuitsAsync(_cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if ((path == "/api/change-listener-port" || path == "/api/change-port") && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            int port = doc.RootElement.GetProperty("port").GetInt32();
            if (port is <= 0 or > ushort.MaxValue) throw new InvalidDataException("Invalid listener port.");
            bool success = _qcc.RebindListenerPort((ushort)port);
            await WebUiContext.WriteJsonAsync(resp, new { success, port = _qcc.LocalDiscoveryPort }, success ? 200 : 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/cancel-interrogation" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string id = doc.RootElement.GetProperty("id").GetString() ?? "";
            bool success = _qcc.CancelInterrogation(id);
            await WebUiContext.WriteJsonAsync(resp, new { success }, success ? 200 : 404).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
