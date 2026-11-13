using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;

namespace QuicNet.WebUi.Modules;

internal sealed class PeerApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly IVirtualLanHandler? _lanHandler;
    private readonly VoiceCallHandler _voiceHandler;
    private readonly SpeedTestHandler _speedTestHandler;
    private readonly AppPreferencesStore _preferences;
    private readonly WebUiWebSocketHub _hub;
    private readonly CancellationTokenSource _cts;
    private readonly QuicNet.Networking.ConnectionModeController _connectionModes;
    private readonly SemaphoreSlim _torDiscoveryChanges = new(1, 1);

    public ConcurrentDictionary<Guid, WebUiServer.PendingPetitionItem> PendingPetitions { get; } = new();
    public ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> InFlightConnections { get; } = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _connectionCancellations = new();
    public ConcurrentQueue<WebUiServer.ClipboardItem> ClipboardItems { get; } = new();

    public PeerApiModule(
        QuicPunch.QuicPunch qcc,
        ChatHandler chatHandler,
        IVirtualLanHandler? lanHandler,
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
        _connectionModes = new(
            () => { var p = _preferences.Snapshot(); return (p.WanEnabled, p.TorEnabled); },
            (wan, tor) => { _preferences.Update(p => { p.WanEnabled = wan; p.TorEnabled = tor; }); _qcc.WanEnabled = wan; },
            async (enabled, port, ct) => {
                if (enabled) { await _qcc.StartWanAsync((ushort)port, ct); await _qcc.Mailbox.StartAsync(_qcc.Discovery.NostrRelays, _cts.Token); }
                else { await _qcc.Mailbox.StopAsync(); await _qcc.StopWanAsync(); }
            },
            async (enabled, port, ct) => {
                if (enabled) { await _qcc.StartTorAsync(port > 0 ? port : _preferences.Snapshot().TorVirtualPort, cancellationToken: ct); await _qcc.Mailbox.StartAsync(_qcc.Discovery.NostrRelays, _cts.Token); }
                else { await _qcc.Mailbox.StopAsync(); await _qcc.StopTorAsync(); }
            },
            () => WebUiServer.BroadcastStatusUpdate());

        _qcc.Manager.HandshakeRequested += HandleHandshakeRequestAsync;
        _qcc.OnPeerAvailable += peer =>
        {
            WebUiServer.LogEvent($"[DISCOVERY] Peer available: {peer.Name} ({peer.Id})");
            _hub.Broadcast("peer_available", new global::QuicNet.Serialization.PeerApiModuleMessage1(peer.Id.ToString(), peer.Name));
        };
        _qcc.OnPeerDisconnected += peer =>
        {
            WebUiServer.LogEvent($"[NETWORK] Peer {peer.Name ?? "Unknown"} disconnected");
            _hub.Broadcast("peer_network_disconnected", new global::QuicNet.Serialization.PeerApiModuleMessage1(peer.Id.ToString(), peer.Name));
        };
    }

    public Task<HandshakeDecision> HandleHandshakeRequestAsync(HandshakeRequest request, CancellationToken ct)
    {
        PeerInfo? peer = ResolvePeer(request.PeerId, request.CertHash, request.RemoteEndPoint);
        string protocolName = ResolveProtocolName(request.ProtocolId);
        string peerName = peer?.Name ?? (request.PeerId != Guid.Empty ? $"Peer-{request.PeerId.ToString()[..6]}" : request.RemoteEndPoint.ToString());
        Guid peerId = peer?.Id ?? request.PeerId;

        if (peer == null || !_qcc.IsTrustedPeer(peer))
        {
            WebUiServer.LogEvent($"[SECURITY] Blocked {protocolName} request from untrusted peer {peerName}");
            return Task.FromResult(new HandshakeDecision(false, null, CancellationToken.None));
        }
        if (_qcc.ProtocolHandlers.TryGetValue(request.ProtocolId, out var policyHandler) && policyHandler.IncomingSessionPolicy(peer) == false)
            return Task.FromResult(new HandshakeDecision(false, null, CancellationToken.None));

        if (request.CertHash is { Length: > 0 } certHash &&
            peer.TryGetCertificateHash(out var peerHash) &&
            CryptographicOperations.FixedTimeEquals(certHash, peerHash) &&
            _qcc.HasAutomaticProtocolPermission(peer, request.ProtocolId))
        {
            WebUiServer.LogEvent($"[PERMISSION] Automatically accepted {protocolName} from {peerName}");
            return Task.FromResult(new HandshakeDecision(true, 0, CancellationToken.None));
        }

        if (PendingPetitions.TryGetValue(request.Id, out var existing))
            return existing.Tcs.Task;

        var samePeer = PendingPetitions.Values.FirstOrDefault(v => v.PeerId == peerId && v.ProtocolId == request.ProtocolId);
        if (samePeer != null)
            return samePeer.Tcs.Task;

        var tcs = new TaskCompletionSource<HandshakeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingPetitions[request.Id] = new WebUiServer.PendingPetitionItem(request.Id, request.ProtocolId, protocolName, peerName, peerId, tcs);
        if (request.ProtocolId == _voiceHandler.ProtocolId)
        {
            WebUiServer.Instance?.Notifications?.RingIncomingCall(request.Id, Convert.ToHexString(peer.CertHash), tcs.Task);
            WebUiServer.Instance?.Notifications?.Publish("calls", peerName, "Llamada entrante", "/call.html",
                Convert.ToHexString(peer.CertHash), request.Id.ToString("N"), playSound: false);
        }
        WebUiServer.LogEvent($"[PETITION] {protocolName} request from {peerName}");
        bool showNotice = WebUiServer.Instance?.Notifications?.AllowsInApp(request.ProtocolId == _voiceHandler.ProtocolId ? "calls" : "system", Convert.ToHexString(peer.CertHash)) ?? true;
        _hub.Broadcast("petition_received", new global::QuicNet.Serialization.PeerApiModuleMessage2(request.Id.ToString(), peerId.ToString(), peerName, request.ProtocolId.ToString(), protocolName, showNotice));

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
            if (PendingPetitions.TryRemove(key, out var petition))
                petition.Tcs.TrySetResult(new HandshakeDecision(false, null, CancellationToken.None));
    }

    public string ResolveProtocolName(Guid protocolId)
    {
        return _qcc.ProtocolHandlers.TryGetValue(protocolId, out var handler) && !string.IsNullOrWhiteSpace(handler.ProtocolName)
            ? handler.ProtocolName : "Application";
    }

    public Guid ResolveProtocolId(string value)
    {
        if (Guid.TryParse(value, out var parsed)) return parsed;
        return value.Trim().ToLowerInvariant() switch
        {
            "chat" or "direct chat" => _chatHandler.ProtocolId,
            "voice" or "call" or "voice studio" => _voiceHandler.ProtocolId,
            "lan" or "vpn" or "lan bridge" or "friendslan" => _lanHandler?.ProtocolId ?? throw new InvalidDataException("LAN Bridge protocol is not available on this platform."),
            "speedtest" or "speed test" or "benchmark" => _speedTestHandler.ProtocolId,
            "virtual-disk" or "disco virtual" => QuicNet.VirtualDisk.VirtualDiskHandler.Id,
            _ => throw new InvalidDataException("Unknown application protocol.")
        };
    }

    public (bool Reused, bool InProgress) StartProtocolConnection(PeerInfo peer, Guid protocolId)
    {
        bool connected = protocolId == _chatHandler.ProtocolId && ChatHandler.ActiveChats.ContainsKey(peer.Id)
            || protocolId == _voiceHandler.ProtocolId && VoiceCallHandler.ActiveCalls.ContainsKey(peer.Id)
            || (_lanHandler != null && protocolId == _lanHandler.ProtocolId && _lanHandler.HasActivePeer(peer.Id))
            || protocolId == _speedTestHandler.ProtocolId && SpeedTestHandler.ActiveSessions.ContainsKey(peer.Id)
            || _qcc.HasActiveProtocolSession(peer.Id, protocolId);
        if (connected) return (true, false);

        var key = (peer.Id, protocolId);
        if (InFlightConnections.ContainsKey(key) || _qcc.IsConnectionInFlight(peer.Id, protocolId))
            return (false, true);

        Guid attemptId = Guid.NewGuid();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _connectionCancellations[attemptId] = cancellation;
        if (!InFlightConnections.TryAdd(key, attemptId))
        {
            _connectionCancellations.TryRemove(attemptId, out _); cancellation.Dispose();
            return (false, true);
        }

        ClearPeerPetitions(peer.Id, protocolId);
        _ = Task.Run(async () =>
        {
            try
            {
                QuicPunchLog.Info($"[APP] Starting {ResolveProtocolName(protocolId)} with {peer.Name} (attempt {attemptId}).");
                await _qcc.InitQuicConnection(protocolId, peer, 0, cancellation.Token).ConfigureAwait(false);
                _qcc.RecordPeerUsage(peer.Id);
            }
            catch (Exception ex)
            {
                bool cancelled = ex is OperationCanceledException && cancellation.IsCancellationRequested;
                if (!cancelled)
                {
                    string protoName = ResolveProtocolName(protocolId);
                    string peerName = peer.Name ?? "Peer";
                    string err = $"Failed to connect {protoName} with {peerName}: {ex.Message}";
                    QuicPunchLog.Info($"[APP] {err} (attempt {attemptId}).");
                    WebUiServer.LogEvent($"[APP] {err}");
                    WebUiServer.AddNotification("error", err);
                }
                if (protocolId == _voiceHandler.ProtocolId)
                    _hub.Broadcast("voice_signal", new global::QuicNet.Serialization.VoiceApiModuleMessage5(peer.Id.ToString(), cancelled ? "call-ended" : "call-failed"));
            }
            finally
            {
                InFlightConnections.TryRemove(new KeyValuePair<(Guid PeerId, Guid ProtocolId), Guid>(key, attemptId));
                _connectionCancellations.TryRemove(attemptId, out _); cancellation.Dispose();
            }
        });
        return (false, false);
    }

    internal void CancelPendingConnection(Guid peerId, Guid protocolId)
    {
        if (InFlightConnections.TryGetValue((peerId, protocolId), out var attempt) && _connectionCancellations.TryGetValue(attempt, out var cancellation))
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/peer/profile" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            byte[] hash = WebUiContext.ParseCertHash(doc.RootElement.GetProperty("certHash").GetString() ?? "");
            string value = doc.RootElement.GetProperty("profile").GetString() ?? "";
            var profile = value switch { "friend" => PeerProfile.Friend, "administrator" => PeerProfile.Administrator,
                _ => throw new InvalidDataException("Unknown peer profile.") };
            if (_qcc.PeerStore?.SetProfile(hash, profile) != true)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Save this peer first."), 404).ConfigureAwait(false);
                return true;
            }
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage4(true, value)).ConfigureAwait(false);
            return true;
        }
        if (path == "/api/peer/automatic-permission" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            byte[] certHash = WebUiContext.ParseCertHash(doc.RootElement.GetProperty("certHash").GetString() ?? "");
            string permission = doc.RootElement.GetProperty("permission").GetString() ?? "";
            bool allowed = doc.RootElement.GetProperty("allowed").GetBoolean();
            if (!PeerStore.SupportedAutomaticPermissions.Contains(permission)) throw new InvalidDataException("Unknown permission.");
            if (_qcc.PeerStore == null || !_qcc.PeerStore.TryGet(certHash, out _))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Save this peer before granting automatic permissions."), 404).ConfigureAwait(false);
                return true;
            }
            _qcc.PeerStore.SetAutomaticPermission(certHash, permission, allowed);
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage5(true, permission, allowed)).ConfigureAwait(false);
            return true;
        }
        if (path == "/api/discovery" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool enabled = doc.RootElement.GetProperty("enabled").GetBoolean();
            string type = doc.RootElement.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? (typeEl.GetString() ?? "wan").ToLowerInvariant()
                : "wan";

            if (type == "tor")
            {
                if (enabled && (!doc.RootElement.TryGetProperty("acknowledgeTorPrivacyRisk", out var risk) || risk.ValueKind != JsonValueKind.True))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false,
                        "Lee y acepta el aviso de privacidad antes de activar el descubrimiento Tor: tus anuncios pueden vincularse con tu identidad WAN y tu IP pública."), 409).ConfigureAwait(false);
                    return true;
                }
                await _torDiscoveryChanges.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    // Persist explicit consent before any announcement can be published.
                    _preferences.Update(p => { p.TorDiscoveryRiskAcknowledged = enabled; p.TorNostrDiscoveryEnabled = enabled; });
                    try { await _qcc.Discovery.SetTorPeerDiscoveryEnabledAsync(enabled, _cts.Token).ConfigureAwait(false); }
                    catch
                    {
                        _qcc.Discovery.TorNostrDiscoveryEnabled = false;
                        _preferences.Update(p => { p.TorNostrDiscoveryEnabled = false; p.TorDiscoveryRiskAcknowledged = false; });
                        await _qcc.Discovery.StopTorNostrDiscoveryAsync().ConfigureAwait(false);
                        throw;
                    }
                    WebUiServer.LogEvent($"[DISCOVERY] Tor Nostr discovery {(enabled ? "enabled with privacy acknowledgement" : "disabled")}");
                    WebUiServer.BroadcastStatusUpdate();
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage6(true, "tor", enabled, _qcc.Discovery.TorNostrDiscovery?.IsRunning ?? false)).ConfigureAwait(false);
                }
                finally { _torDiscoveryChanges.Release(); }
            }
            else
            {
                if (_qcc.IsWanStarted) await _qcc.Discovery.SetWanPeerDiscoveryEnabledAsync(enabled, _cts.Token).ConfigureAwait(false);
                else { _qcc.Discovery.WanNostrDiscoveryEnabled = enabled; await _qcc.Discovery.StopWanNostrDiscoveryAsync().ConfigureAwait(false); }
                _preferences.Update(p => p.WanNostrDiscoveryEnabled = enabled);
                WebUiServer.LogEvent($"[DISCOVERY] WAN Nostr discovery {(enabled ? "enabled" : "disabled")}");
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage6(true, "wan", enabled, _qcc.Discovery.WanNostrDiscovery?.IsRunning ?? false)).ConfigureAwait(false);
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
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Authenticated peer not found."), 404).ConfigureAwait(false);
                return true;
            }

            if (trust)
            {
                _qcc.TrustPeer(certHash);
                WebUiServer.LogEvent($"[TRUST] Trusted {peer.Name}");

            }
            else
            {
                // Declining trust acknowledges the identity without hiding or trusting the peer.
                string reviewedHash = Convert.ToBase64String(certHash);
                _preferences.Update(p => p.ReviewedUntrustedPeers = [.. p.ReviewedUntrustedPeers, reviewedHash]);
                _qcc.UntrustPeer(certHash);
                WebUiServer.LogEvent($"[TRUST] Removed trust from {peer.Name}");
            }
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage7(true, trust)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/peer/connect" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Peer not found."), 404).ConfigureAwait(false);
                return true;
            }

            try
            {
                await _qcc.EnsureMultiplexerConnectedAsync(peer, cancellationToken: _cts.Token).ConfigureAwait(false);
                WebUiServer.BroadcastStatusUpdate();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage8(true, true)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, ex.Message), 500).ConfigureAwait(false);
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
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Peer not found."), 404).ConfigureAwait(false);
                return true;
            }
            if (save)
            {
                if (!_qcc.IsTrustedPeer(peer))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Trust this peer before saving it."), 403).ConfigureAwait(false);
                    return true;
                }
                bool saved = _qcc.SavePeer(peer, autoConnect);
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage9(saved, saved, autoConnect), saved ? 200 : 500).ConfigureAwait(false);
            }
            else
            {
                bool removed = _qcc.RemoveSavedPeer(hash);
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage10(removed, false), removed ? 200 : 404).ConfigureAwait(false);
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
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage10(true, saved)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-saved-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] certHash = WebUiContext.ParseCertHash(certHashStr);
            if (_qcc.PeerStore == null || !_qcc.PeerStore.TryGet(certHash, out var sp) || sp == null)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Saved peer not found."), 404).ConfigureAwait(false);
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

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
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
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage12(toggled, autoConnect)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/saved-peer-delete" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] certHash = WebUiContext.ParseCertHash(certHashStr);

            bool removed = _qcc.RemoveSavedPeer(certHash);
            WebUiServer.LogEvent($"[PEER STORE] Deleted saved peer with hash {Convert.ToBase64String(certHash)}");
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage13(true, removed)).ConfigureAwait(false);
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
                    p.IsAvailableAndResponsive(TimeSpan.FromMinutes(1)));

                if (!isOnline)
                {
                    if (_qcc.RemoveSavedPeer(sp.CertHash))
                        removedCount++;
                }
            }
            WebUiServer.LogEvent($"[PEER STORE] Cleared {removedCount} offline saved peer(s).");
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage14(true, removedCount)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/save-all-peers" && req.HttpMethod == "POST")
        {
            int count = 0;
            foreach (var peer in _qcc.AvailablePeers.Values)
                if (_qcc.IsTrustedPeer(peer) && _qcc.TryGetPeerMultiplexer(peer.Id, out var mux) && mux != null && !mux.IsDisposed && _qcc.SavePeer(peer, true)) count++;
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage15(true, count)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-token" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string token = doc.RootElement.GetProperty("token").GetString()?.Trim() ?? "";
            if (token.Length == 0) throw new InvalidDataException("Token is required.");

            try
            {
                using var decoded = QuicPunch.Helpers.Utilities.DecodeEndpointToken(token);
                bool tor = decoded.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrWhiteSpace(decoded.OnionAddress);
                if (tor ? !_qcc.IsTorStarted : !_qcc.IsWanStarted)
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.ConnectionTokenTransportError(false,
                        tor ? "Este código utiliza Tor. Activa Tor para conectar." : "Este código utiliza WAN. Activa la conexión directa para conectar.", tor ? "tor" : "wan"), 409).ConfigureAwait(false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Token format is invalid: {ex.Message}");
            }

            // Advertise our signed endpoint as well, so rendezvous is bidirectional.
            _ = _qcc.Discovery.PublishNostrDiscoveryAsync(_cts.Token);
            // The token selects the endpoint; only the authenticated handshake may update a saved peer.
            await _qcc.FindPeerFromInvitationAsync(token, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage16(true, true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connect-discovered-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string token = doc.RootElement.TryGetProperty("token", out var tEl) ? tEl.GetString()?.Trim() ?? "" : "";
            string certHashStr = doc.RootElement.TryGetProperty("certHash", out var cEl) ? cEl.GetString()?.Trim() ?? "" : "";

            if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(certHashStr))
            {
                byte[] hash = WebUiContext.ParseCertHash(certHashStr);
                string hexKey = Convert.ToHexString(hash);
                if (_qcc.Discovery.DiscoveredPeers.TryGetValue(hexKey, out var dp))
                    token = dp.Token;
            }

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidDataException("Token or valid discovered peer hash is required.");

            WebUiServer.LogEvent("[DISCOVERY] Manually initiating connection to discovered peer...");
            _ = Task.Run(async () =>
            {
                try { await _qcc.PeerInterrogation(token, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { WebUiServer.LogEvent($"[CONNECT DISCOVERED] Interrogation failed: {ex.Message}"); }
            });

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/dismiss-discovered-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string certHashStr = doc.RootElement.GetProperty("certHash").GetString()?.Trim() ?? "";
            byte[] hash = WebUiContext.ParseCertHash(certHashStr);
            string hexKey = Convert.ToHexString(hash);
            _qcc.Discovery.DiscoveredPeers.TryRemove(hexKey, out _);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
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
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage17(true, count)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/disconnect-peer" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            _qcc.DisconnectPeer(peerId);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
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
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Peer not found."), 404).ConfigureAwait(false);
                return true;
            }
            if (!_qcc.IsTrustedPeer(peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Trust this peer before opening application protocols."), 403).ConfigureAwait(false);
                return true;
            }
            Guid protocolId = ResolveProtocolId(protocol);
            if (_lanHandler != null && protocolId == _lanHandler.ProtocolId &&
                (!_qcc.TryGetPeerMultiplexer(peer.Id, out var lanTransport) || lanTransport is not { IsDisposed: false } ||
                 lanTransport.GetRemoteProtocolAvailability(protocolId) != true))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Espera a que se establezca la conexión QUIC y el peer confirme que puede activar la LAN."), 409).ConfigureAwait(false);
                return true;
            }
            if (_lanHandler?.Control is { } control && protocolId == _lanHandler.ProtocolId)
            {
                var config = control.Configuration;
                var entry = config.Peers.FirstOrDefault(p => p.CertHash.Equals(Convert.ToHexString(peer.CertHash), StringComparison.OrdinalIgnoreCase));
                if (entry is { Enabled: false }) throw new InvalidDataException("Este peer tiene la VPN deshabilitada. Actívala en su configuración VPN.");
                if (entry != null) entry.Paused = false;
                config.Enabled = true;
                control.SaveConfiguration(config, config.Revision);
                if (!_lanHandler.IsActive)
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage18(false, !LanApiModule.IsRunningAsAdmin() && !_lanHandler.CanStartWithoutElevation, control.ConfigurationError ?? "El adaptador VPN aún no está disponible."), 503).ConfigureAwait(false);
                    return true;
                }
            }
            if (_lanHandler != null && protocolId == _lanHandler.ProtocolId && !_lanHandler.IsActive)
            {
                if (LanApiModule.IsRunningAsAdmin() || _lanHandler.CanStartWithoutElevation)
                {
                    try
                    {
                        if (_lanHandler.AutoAssignEnabled)
                            _lanHandler.ConfigureAutoAssignment(_qcc.CertManager.CertPublicHash, _qcc.PoolId);
                        _lanHandler.SetupTun();
                        _qcc.RegisterProtocol(_lanHandler);
                        _preferences.Update(p => p.LanEnabled = true);
                        WebUiServer.LogEvent($"[LAN] Puente LAN auto-iniciado para conectar con {peer.Name}.");
                        WebUiServer.BroadcastStatusUpdate();
                    }
                    catch (Exception ex)
                    {
                        await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, $"No se pudo iniciar el adaptador LAN: {ex.Message}"), 500).ConfigureAwait(false);
                        return true;
                    }
                }
                else
                {
                    _lanHandler.NotifyPrivilegeRequired();
                    WebUiServer.BroadcastStatusUpdate();
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage18(false, true, "Se requieren permisos de administrador para iniciar el adaptador de LAN virtual."), 403).ConfigureAwait(false);
                    return true;
                }
            }
            var result = StartProtocolConnection(peer, protocolId);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage19(true, result.Reused, result.InProgress)).ConfigureAwait(false);
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
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Peer not found."), 404).ConfigureAwait(false);
                return true;
            }

            var item = new WebUiServer.ClipboardItem(peer.Id.ToString(), peer.Name ?? "Peer", text, DateTime.Now, true);
            ClipboardItems.Enqueue(item);
            while (ClipboardItems.Count > 500) ClipboardItems.TryDequeue(out _);

            WebUiServer.LogEvent($"[CLIPBOARD] Copied {text.Length} character(s) to clipboard for {peer.Name}");
            _hub.Broadcast("clipboard_item", item);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/respond-petition" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid requestId = WebUiContext.ParseGuid(doc.RootElement, "requestId");
            bool accept = doc.RootElement.TryGetProperty("accept", out var acceptEl) && acceptEl.GetBoolean();
            if (!PendingPetitions.TryRemove(requestId, out var petition))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage3(false, "Petition not found or expired."), 404).ConfigureAwait(false);
                return true;
            }
            petition.Tcs.TrySetResult(new HandshakeDecision(accept, accept ? (ushort)0 : null, CancellationToken.None));
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/connection-mode" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string mode = doc.RootElement.GetProperty("mode").GetString() ?? "";
            await _connectionModes.SetModeAsync(mode, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/wan-start" or "/api/wan-stop" or "/api/tor-start" or "/api/tor-stop" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            int port = doc.RootElement.TryGetProperty("port", out var value) ? value.GetInt32() : 0;
            if (port < 0 || port > ushort.MaxValue) throw new InvalidDataException("Puerto no válido.");
            if (port > 0 && path == "/api/tor-start") _preferences.Update(p => p.TorVirtualPort = port);
            await _connectionModes.SetServiceAsync(path.Contains("/wan-") ? "wan" : "tor", path.EndsWith("-start"), port, _cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
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
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/tor-newnym" && req.HttpMethod == "POST")
        {
            if (_qcc.TorManager == null) throw new InvalidOperationException("Tor is not running.");
            await _qcc.TorManager.RequestNewCircuitsAsync(_cts.Token).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(true)).ConfigureAwait(false);
            return true;
        }

        if ((path == "/api/change-listener-port" || path == "/api/change-port") && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            int port = doc.RootElement.GetProperty("port").GetInt32();
            if (port is <= 0 or > ushort.MaxValue) throw new InvalidDataException("Invalid listener port.");
            bool success = _qcc.RebindListenerPort((ushort)port);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage24(success, _qcc.LocalDiscoveryPort), success ? 200 : 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/cancel-interrogation" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string id = doc.RootElement.GetProperty("id").GetString() ?? "";
            bool success = _qcc.CancelInterrogation(id);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.PeerApiModuleMessage11(success), success ? 200 : 404).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
