using System.Net;
using QuicNet.Serialization;
using System.Web;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.Networking;

namespace QuicNet.WebUi.Modules;

internal sealed class StatusApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly IVirtualLanHandler? _lanHandler;
    private readonly VoiceCallHandler _voiceHandler;
    private readonly AppPreferencesStore _preferences;
    private readonly PeerApiModule _peerModule;
    private readonly ChatApiModule _chatModule;
    private readonly ProfileAvatarStore _avatars;
    private readonly LocalIpGeolocation _geolocation;
    private readonly StatusPeerProjection _peerProjection;
    private readonly QuicNet.Presence.UserPresenceService _presence;

    public StatusApiModule(
        QuicPunch.QuicPunch qcc,
        ChatHandler chatHandler,
        IVirtualLanHandler? lanHandler,
        VoiceCallHandler voiceHandler,
        AppPreferencesStore preferences,
        PeerApiModule peerModule,
        ChatApiModule chatModule,
        ProfileAvatarStore avatars,
        LocalIpGeolocation geolocation, QuicNet.Presence.UserPresenceService presence)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _lanHandler = lanHandler;
        _voiceHandler = voiceHandler;
        _preferences = preferences;
        _peerModule = peerModule;
        _chatModule = chatModule;
        _avatars = avatars;
        _geolocation = geolocation;
        _peerProjection = new(qcc, lanHandler, geolocation);
        _presence = presence;
    }

    public WebUiStatusSnapshot GetStatusObject(bool includeChat = false, bool includeClipboard = false)
    {
        string wanToken = "";
        if (_qcc.IsWanStarted)
        {
            try { wanToken = _qcc.GetWanToken() ?? ""; }
            catch (InvalidOperationException) { /* No public endpoint while offline. */ }
        }
        string torToken = _qcc.GetTorToken() ?? "";
        AppPreferences prefs = _preferences.Snapshot();
        var saved = _qcc.PeerStore?.SavedPeers?.ToList() ?? new List<PeerStore.SavedPeer>();
        var savedByHash = new Dictionary<string, PeerStore.SavedPeer>(StringComparer.Ordinal);
        foreach (var item in saved) savedByHash[Convert.ToBase64String(item.CertHash)] = item;

        var activeSessionTelemetry = _qcc.GetActiveSessionsTelemetry();
        var quicTelemetry = activeSessionTelemetry.Select(s => new global::QuicNet.Serialization.StatusApiModuleMessage1(s.PeerId.ToString(), s.PeerName ?? "Peer", s.ProtocolId.ToString(), s.ProtocolName ?? s.ProtocolId.ToString(), s.TransportType.ToString(), MapTelemetry(s.Telemetry), s.SampleTimeUtc.ToString("o"))).ToList();

        var peers = _peerProjection.GetAvailable(savedByHash);
        var savedPeers = _peerProjection.GetSaved(saved);

        var lanPeers = _lanHandler?.GetActivePeerSummaries().ToList() ?? new List<object>();

        object[] chatMessages = includeChat
            ? _chatModule.ChatMessages.TakeLast(200).Select(message => (object)new global::QuicNet.Serialization.StatusApiModuleMessage4(message.PeerId, message.MsgId, message.Sender, message.Message, message.Timestamp.ToString("HH:mm:ss"), message.IsMe, message.IsConfirmed, message.ExpiresAtUtc.HasValue
                    ? new DateTimeOffset(message.ExpiresAtUtc.Value.ToUniversalTime()).ToUnixTimeMilliseconds() : (long?)null) { timestampMs = new DateTimeOffset(message.Timestamp).ToUnixTimeMilliseconds(), deliveryState = message.DeliveryState }).ToArray()
            : Array.Empty<object>();

        object[] clipboardItems = includeClipboard
            ? _peerModule.ClipboardItems.TakeLast(50).Select(item => (object)new global::QuicNet.Serialization.StatusApiModuleMessage5(item.PeerId, item.PeerName, item.Text, item.Timestamp.ToString("HH:mm:ss"), item.IsMe)).ToArray()
            : Array.Empty<object>();

        var petitions = _peerModule.PendingPetitions.Values.Select(item => new global::QuicNet.Serialization.StatusApiModuleMessage6(item.RequestId.ToString(), item.PeerId.ToString(), item.PeerName, item.ProtocolId.ToString(), item.ProtocolName)).ToList();

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

            return new global::QuicNet.Serialization.StatusApiModuleMessage7(item.Id, item.Id, canonId, item.Peer.Id != Guid.Empty ? item.Peer.Id.ToString() : canonId, certHashB64, !string.IsNullOrWhiteSpace(pName) ? pName : "Unknown", item.Peer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(), item.Peer.PortArray ?? Array.Empty<ushort>(), item.Peer.ConnectionFlags?.PortMode.ToString() ?? "Single", item.StartTime.ToString("HH:mm:ss"));
        }).ToList();

        var discovery = new global::QuicNet.Serialization.StatusApiModuleMessage8(_qcc.Discovery.WanNostrDiscoveryEnabled, prefs.WanNostrDiscoveryEnabled, _qcc.Discovery.WanNostrDiscovery?.IsRunning ?? false, _qcc.Discovery.WanNostrDiscovery?.ConnectedRelayCount ?? 0, _qcc.Discovery.NostrRelays?.Length ?? QuicPunch.NostrDiscovery.DefaultRelays.Length, new global::QuicNet.Serialization.StatusApiModuleMessage9(_qcc.Discovery.WanNostrDiscoveryEnabled, prefs.WanNostrDiscoveryEnabled, _qcc.Discovery.WanNostrDiscovery?.IsRunning ?? false, _qcc.Discovery.WanNostrDiscovery?.ConnectedRelayCount ?? 0, _qcc.Discovery.NostrRelays?.Length ?? QuicPunch.NostrDiscovery.DefaultRelays.Length, _qcc.IsWanStarted, prefs.WanEnabled) { lastAnnouncedUtc = _qcc.Discovery.WanNostrDiscovery?.LastAnnouncedUtc, lastAcknowledgedUtc = _qcc.Discovery.WanNostrDiscovery?.LastAcknowledgedUtc }, new global::QuicNet.Serialization.StatusApiModuleMessage9(_qcc.Discovery.TorNostrDiscoveryEnabled, prefs.TorNostrDiscoveryEnabled, _qcc.Discovery.TorNostrDiscovery?.IsRunning ?? false, _qcc.Discovery.TorNostrDiscovery?.ConnectedRelayCount ?? 0, _qcc.Discovery.TorNostrRelays?.Length ?? _qcc.Discovery.NostrRelays?.Length ?? QuicPunch.NostrDiscovery.DefaultRelays.Length, _qcc.IsTorStarted, prefs.TorEnabled) { lastAnnouncedUtc = _qcc.Discovery.TorNostrDiscovery?.LastAnnouncedUtc, lastAcknowledgedUtc = _qcc.Discovery.TorNostrDiscovery?.LastAcknowledgedUtc });

        var wan = new global::QuicNet.Serialization.StatusApiModuleMessage10(prefs.WanEnabled, _qcc.IsWanStarted, _qcc.LocalDiscoveryPort, _qcc.WanLastError ?? "");

        var tor = new global::QuicNet.Serialization.StatusApiModuleMessage11(prefs.TorEnabled, _qcc.IsTorStarted, _qcc.TorOnionAddress ?? "", _qcc.TorBootstrapProgress, _qcc.TorBootstrapStatus, _qcc.TorActiveTransport.ToString(), prefs.TorTransportMode, _qcc.TorHub?.VirtualPort ?? prefs.TorVirtualPort, _qcc.TorManager?.SocksPort ?? 0, _qcc.TorLastError ?? "");

        bool isRunningAsAdmin = LanApiModule.IsRunningAsAdmin();
        bool requiresAdmin = !isRunningAsAdmin && _lanHandler?.CanStartWithoutElevation != true;

        var lan = _lanHandler == null ? (object)new global::QuicNet.Serialization.StatusApiModuleMessage12(false, false, "None", "Fuera de alcance", "", false, false, false, "Disabled", "127.0.0.1", "255.255.255.0", 0, lanPeers, 0L, 0L, 0L, 0L) : new global::QuicNet.Serialization.StatusApiModuleMessage13(_lanHandler.Control?.Configuration.Enabled ?? prefs.LanEnabled, _lanHandler.Control?.Configuration.Mode ?? "simple", _lanHandler.Control?.Configuration.Revision ?? 0, _lanHandler.Control?.ConfigurationError, _lanHandler.Control?.OrchestrationStatus, _lanHandler.CanStartWithoutElevation, _lanHandler.IsActive, _lanHandler.AdapterName, _lanHandler.AdapterStatus, _lanHandler.LastError ?? "", isRunningAsAdmin, requiresAdmin, _lanHandler.AutoAssignEnabled, _lanHandler.AddressMode, _lanHandler.LocalIp.ToString(), _lanHandler.SubnetMask, _lanHandler.Mtu, lanPeers, _lanHandler.TotalRxPackets, _lanHandler.TotalTxPackets, _lanHandler.TotalRxBytes, _lanHandler.TotalTxBytes);

        var apps = new global::QuicNet.Serialization.StatusApiModuleMessage14(new global::QuicNet.Serialization.StatusApiModuleMessage15(ChatHandler.ActiveChats.Count, _chatHandler.ProtocolId.ToString()), new global::QuicNet.Serialization.StatusApiModuleMessage15(VoiceCallHandler.ActiveCalls.Count, _voiceHandler.ProtocolId.ToString()), new global::QuicNet.Serialization.StatusApiModuleMessage15(_lanHandler?.ActivePeerCount ?? 0, _lanHandler?.ProtocolId.ToString() ?? ""), new global::QuicNet.Serialization.StatusApiModuleMessage15(_peerModule.ClipboardItems.Count, ""));

        var stunHits = _qcc.GetStunHitsSnapshot();
        var exactMappingsList = new List<object>();
        if (stunHits.Count > 0)
        {
            foreach (var kv in stunHits.OrderByDescending(k => k.Value).ThenBy(k => Utilities.IpToUint(k.Key.Address)).ThenBy(k => k.Key.Port))
            {
                exactMappingsList.Add(new global::QuicNet.Serialization.StatusApiModuleMessage16(kv.Key.Address.ToString(), kv.Key.Port, kv.Key.ToString(), kv.Value));
            }
        }

        var localIps = Utilities.GetValidLocalIPAddresses();
        var stun = new global::QuicNet.Serialization.StatusApiModuleMessage17(_qcc.CurrentPeer?.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(), exactMappingsList.ToArray(), _qcc.CurrentPeer?.PortArray ?? Array.Empty<ushort>(), _qcc.CurrentPeer?.ConnectionFlags?.PortMode.ToString() ?? "Single", localIps.Select(a => a.ToString()).ToArray(), _qcc.StunServerEndpoints?.Count ?? 0, _qcc.CurrentPeer?.NetworkType.ToString() ?? "Unknown");

        var discoveredPeers = _peerProjection.GetDiscovered(savedByHash, localIps);

        string quickUri = !string.IsNullOrWhiteSpace(wanToken) ? $"qp://{wanToken}" : "";
        return new WebUiStatusSnapshot(
            new global::QuicNet.Serialization.StatusApiModuleMessage20(_qcc.CurrentPeer?.Name ?? "LocalNode", _qcc.CurrentPeer?.Id.ToString() ?? "", _qcc.CurrentPeer?.TryGetCertificateHash(out var myHash) == true ? Utilities.ToCanonicalPeerId(myHash) : "", _qcc.LocalDiscoveryPort, _qcc.CurrentPeer?.PortArray ?? Array.Empty<ushort>(), _qcc.CurrentPeer?.ConnectionFlags?.PortMode.ToString() ?? "Single", _qcc.CurrentPeer?.NetworkType.ToString() ?? "Unknown", wanToken, torToken, quickUri, _qcc.CurrentPeer?.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>()),
            stun,
            prefs,
            discovery,
            new global::QuicNet.Serialization.StatusApiModuleMessage21(_qcc.Mailbox?.IsRunning ?? false, _qcc.Mailbox?.ConnectedRelayCount ?? 0, _qcc.Mailbox?.PublicKeyHex ?? ""),
            wan,
            tor,
            lan,
            apps,
            peers,
            peers.Where(p => p.isQuicConnected).ToList(),
            discoveredPeers,
            savedPeers,
            chatMessages,
            new global::QuicNet.Serialization.StatusApiModuleMessage22(_chatHandler.NostrMessagingEnabled),
            _chatModule.DisappearingSeconds,
            clipboardItems,
            petitions,
            interrogations,
            WebUiServer.UserNotifications.TakeLast(20).Select(n => new global::QuicNet.Serialization.StatusApiModuleMessage23(n.Id, n.Type, n.Message, n.Timestamp.ToString("HH:mm:ss"))).ToArray(),
            WebUiServer.EventLogs.TakeLast(80).ToList(),
            peers.Count(p => p.isTrusted),
            peers.Count(p => !p.isTrusted),
            quicTelemetry,
            quicTelemetry,
            _qcc.CurrentPeer?.Name ?? "LocalNode",
            _qcc.CurrentPeer?.Id.ToString() ?? "",
            wanToken,
            wanToken,
            torToken,
            quickUri,
            _qcc.LocalDiscoveryPort,
            peers,
            discovery,
            wan,
            lan,
            tor,
            ChatHandler.ActiveChats.Values.Select(s => new global::QuicNet.Serialization.StatusApiModuleMessage24(s.Peer.Id.ToString(), s.Peer.Name ?? "")).ToList(),
            VoiceCallHandler.ActiveCalls.Values.Select(s => new global::QuicNet.Serialization.StatusApiModuleMessage24(s.Peer.Id.ToString(), s.Peer.Name ?? "")).ToList(),
            _qcc.ProtocolHandlers.Select(pair => new global::QuicNet.Serialization.StatusApiModuleMessage25(pair.Key.ToString(), pair.Value.ProtocolName)).ToList())
        {
            presence = _presence.Snapshot(),
            peerPresence = _presence.RemoteSnapshot(),
            geolocation = _geolocation.Status,
            avatars = _avatars.Versions(_qcc.CurrentPeer!.Id),
            chatActivity = _chatModule.ConversationActivity,
            connectionMode = _peerModule.ConnectionMode
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
        return new global::QuicNet.Serialization.StatusApiModuleMessage26(Math.Round(t.RttMs, 2), Math.Round(t.MinRttMs, 2), Math.Round(t.MaxRttMs, 2), Math.Round(t.RttVarianceMs, 2), t.PathMtu, t.SendTotalPackets, t.SendRetransmittablePackets, t.SendSuspectedLostPackets, t.SendSpuriousLostPackets, t.SendTotalBytes, t.SendTotalStreamBytes, t.SendCongestionCount, t.SendPersistentCongestionCount, t.SendEcnCongestionCount, t.SendCongestionWindow, t.RecvTotalPackets, t.RecvReorderedPackets, t.RecvDroppedPackets, t.RecvDuplicatePackets, t.RecvTotalBytes, t.RecvTotalStreamBytes, t.RecvDecryptionFailures, t.RecvValidAckFrames, t.KeyUpdateCount, t.DestCidUpdateCount, t.HandshakeHopLimitTtl, Math.Round(t.HandshakeDurationMs, 2), t.HandshakeClientFlight1Bytes, t.HandshakeServerFlight1Bytes, t.HandshakeClientFlight2Bytes, t.CongestionAlgorithmName, t.NativeLocalEndPoint?.ToString() ?? "", t.NativeRemoteEndPoint?.ToString() ?? "", t.CipherSuite ?? "", t.NegotiatedTlsGroup, t.NegotiatedTlsGroupName, t.IsPostQuantum, t.Alpn ?? "", t.SslProtocol ?? "", t.TargetHostName ?? "", Math.Round(t.PacketLossRatio, 4), Math.Round(t.PacketLossRatio * 100, 2), t.TimestampUtc.ToString("o")) { xdpRequested = t.XdpRequested };
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
            var telemetryList = activeSessionTelemetry.Select(s => new global::QuicNet.Serialization.StatusApiModuleMessage1(s.PeerId.ToString(), s.PeerName ?? "Peer", s.ProtocolId.ToString(), s.ProtocolName ?? s.ProtocolId.ToString(), s.TransportType.ToString(), MapTelemetry(s.Telemetry), s.SampleTimeUtc.ToString("o"))).ToList();

            var peerTelemetry = _qcc.AvailablePeers.Values
                .Where(p => p.LastTelemetry != null)
                .Select(p => new global::QuicNet.Serialization.StatusApiModuleMessage27(p.Id.ToString(), p.Name ?? "Peer", p.ActiveEndPoint?.ToString() ?? "", MapTelemetry(p.LastTelemetry))).ToList();

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.StatusApiModuleMessage28(true, DateTime.UtcNow.ToString("o"), telemetryList.Count, telemetryList, peerTelemetry)).ConfigureAwait(false);
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
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.StatusApiModuleMessage29(false, "Invalid peer identifier.")).ConfigureAwait(false);
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

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.StatusApiModuleMessage30(switched, targetAlgorithm.ToString(), peerId.ToString(), protocolIdStr)).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
