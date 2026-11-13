using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuicPunch;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using QuicNet.Settings;
using QuicNet.WebUi.Modules;
using QuicNet.Watch.Web;

namespace QuicNet.WebUi;

public sealed class WebUiServer
{
    private static WebUiServer? _instance;
    public static WebUiServer? Instance => _instance;

    private readonly QuicPunch.QuicPunch? _qcc;
    private readonly ChatHandler? _chatHandler;
    private readonly IVirtualLanHandler? _lanHandler;
    private readonly VoiceCallHandler? _voiceHandler;
    private readonly SpeedTestHandler? _speedTestHandler;
    private readonly AppPreferencesStore? _preferences;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _requestSlots = new(WebUiContext.MaxConcurrentHttpRequests, WebUiContext.MaxConcurrentHttpRequests);
    private readonly SemaphoreSlim _webSocketSlots = new(WebUiContext.MaxConcurrentWebSocketRequests, WebUiContext.MaxConcurrentWebSocketRequests);
    private readonly string _csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private readonly WebUiWebSocketHub _hub;
    private readonly ChatApiModule? _chatModule;
    private readonly VoiceApiModule? _voiceModule;
    private readonly LanApiModule? _lanModule;
    private readonly SpeedTestApiModule? _speedTestModule;
    private readonly PeerApiModule? _peerModule;
    private readonly StatusApiModule? _statusModule;
    private readonly QuicNet.Networking.LocalIpGeolocation? _geolocation;
    private readonly AppSettingsApiModule? _appSettingsModule;
    private readonly QuicNet.Presence.UserPresenceService? _presence;
    internal QuicNet.Notifications.AppNotificationService? Notifications { get; }
    public QuicNet.Notifications.IAppPlatform? AppPlatform { get => Notifications?.Platform; set { if (Notifications != null) Notifications.Platform = value; } }
    public IChatFileOpener? ChatFiles
    {
        get => _chatModule?.FileOpener;
        set { if (_chatModule != null) _chatModule.FileOpener = value; }
    }
    internal QuicNet.Video.IPlatformScreenCapture? PlatformScreenCapture { get; set; }
    private readonly LocalProfileApiModule? _profileModule;
    private readonly WatchWebServer? _watchWeb;
    private readonly VirtualDiskApiModule? _diskModule;
    public QuicNet.VirtualDisk.IVirtualDiskDocuments? DiskDocuments
    {
        get => _diskModule?.Service.Documents;
        set { if (_diskModule != null) _diskModule.Service.Documents = value; }
    }

    private HttpListener? _listener;
    private int _port;

    public NodeCapabilities Capabilities { get; set; }
    public Func<string, ushort, string, string>? ProbePingHandler { get; set; }
    private QuicNet.Updates.AutoUpdateService? _updates;
    public QuicNet.Updates.AutoUpdateService? Updates
    {
        get => _updates;
        set
        {
            if (_updates != null) _updates.StatusChanged -= _hub.NotifyUpdatesChanged;
            _updates = value;
            if (_updates != null) _updates.StatusChanged += _hub.NotifyUpdatesChanged;
            _hub.NotifyUpdatesChanged();
        }
    }

    private void ConfigureUpdateSocket()
    {
        _hub.GetUpdateSnapshot = () => Updates?.Status;
        _hub.ExecuteCommand = (command, data) => UpdatesApiModule.ExecuteAsync(Updates, command, data);
    }
    public string Url => $"http://127.0.0.1:{_port}/";

    public static ConcurrentQueue<string> EventLogs { get; } = new();
    public static ConcurrentQueue<UserNotification> UserNotifications { get; } = new();

    public static ConcurrentQueue<ChatMessage> ChatMessages => _instance?._chatModule?.ChatMessages ?? _emptyChatMessages;
    public static ConcurrentQueue<(string PeerId, string SignalType)> CallSignals => _instance?._voiceModule?.CallSignals ?? _emptyCallSignals;
    public static ConcurrentQueue<ClipboardItem> ClipboardItems => _instance?._peerModule?.ClipboardItems ?? _emptyClipboardItems;
    public static ConcurrentDictionary<Guid, PendingPetitionItem> PendingPetitions => _instance?._peerModule?.PendingPetitions ?? _emptyPetitions;
    public static ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> InFlightConnections => _instance?._peerModule?.InFlightConnections ?? _emptyInFlight;
    internal static void CancelPendingConnection(Guid peerId, Guid protocolId) => _instance?._peerModule?.CancelPendingConnection(peerId, protocolId);

    private static readonly ConcurrentQueue<ChatMessage> _emptyChatMessages = new();
    private static readonly ConcurrentQueue<(string PeerId, string SignalType)> _emptyCallSignals = new();
    private static readonly ConcurrentQueue<ClipboardItem> _emptyClipboardItems = new();
    private static readonly ConcurrentDictionary<Guid, PendingPetitionItem> _emptyPetitions = new();
    private static readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> _emptyInFlight = new();

    public record ChatMessage(string PeerId, string MsgId, string Sender, string Message, DateTime Timestamp, bool IsMe, bool IsConfirmed, DateTime? ExpiresAtUtc = null)
    {
        public string DeliveryState { get; init; } = "sent";
    }
    public record ClipboardItem(string PeerId, string PeerName, string Text, DateTime Timestamp, bool IsMe);
    public record PendingPetitionItem(Guid RequestId, Guid ProtocolId, string ProtocolName, string PeerName, Guid PeerId, TaskCompletionSource<HandshakeDecision> Tcs);
    public record UserNotification(string Id, string Type, string Message, DateTime Timestamp);

    public static void AddNotification(string type, string message)
    {
        var notification = new UserNotification(Guid.NewGuid().ToString("N"), type, message, DateTime.UtcNow);
        UserNotifications.Enqueue(notification);
        while (UserNotifications.Count > 60) UserNotifications.TryDequeue(out _);
        _instance?._hub.Broadcast("notification", notification);
        _instance?.Notifications?.Publish("system", "QuicNet", message);
    }

    public static void LogEvent(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        EventLogs.Enqueue(entry);
        while (EventLogs.Count > 150) EventLogs.TryDequeue(out _);
        _instance?._hub.Broadcast("log_event", new global::QuicNet.Serialization.WebUiServerMessage1(entry));
    }

    public static void ClearPeerPetitions(Guid peerId, Guid? protocolId = null) => _instance?._peerModule?.ClearPeerPetitions(peerId, protocolId);
    public static (bool Reused, bool InProgress) StartProtocolConnection(PeerInfo peer, Guid protocolId) => _instance?._peerModule?.StartProtocolConnection(peer, protocolId) ?? (false, false);

    // Full constructor (Desktop / Full Node)
    internal WebUiServer(
        QuicPunch.QuicPunch qcc,
        ChatHandler chatHandler,
        IVirtualLanHandler? lanHandler,
        VoiceCallHandler voiceHandler,
        SpeedTestHandler speedTestHandler,
        AppPreferencesStore preferences,
        CancellationTokenSource cts,
        WatchWebServer? watchWeb = null,
        int port = 5000,
        NodeCapabilities? capabilities = null)
    {
        _qcc = qcc;
        _chatHandler = chatHandler;
        _lanHandler = lanHandler;
        _voiceHandler = voiceHandler;
        _speedTestHandler = speedTestHandler;
        _preferences = preferences;
        _profileModule = new LocalProfileApiModule(qcc, preferences, chatHandler, cts.Token);
        _cts = cts;
        _watchWeb = watchWeb;
        _port = port;
        Capabilities = capabilities ?? NodeCapabilities.CreateDesktop(lanHandler != null);
        _diskModule = new(qcc, cts.Token);
        cts.Token.Register(_diskModule.Dispose);

        _hub = new WebUiWebSocketHub(_cts, shutdownWhenNoClients: Capabilities.Platform != "android");
        _hub.KeepRunning = () => _preferences.Snapshot().RunInBackground;
        Notifications = new(_preferences, notice => _hub.Broadcast("app_notice", notice));
        _diskModule.Service.FileReceived += receipt => Notifications.Publish("files", "Archivo recibido",
            Path.GetFileName(receipt.Path), "/virtual-disk.html?share=" + receipt.ShareId,
            Convert.ToHexString(Convert.FromBase64String(receipt.Actor)));
        _presence = new(qcc, preferences, () => Notifications.Platform, cts.Token);
        _appSettingsModule = new(_preferences, Notifications, _presence);
        _instance = this;
        _qcc.OnIncomingSessionRequested += (peer, protocol) =>
        {
            if (protocol == _voiceHandler.ProtocolId && _qcc.HasAutomaticProtocolPermission(peer, protocol))
                Notifications.Publish("calls", peer.Name ?? "Llamada", "Llamada entrante", "/call.html", Convert.ToHexString(peer.CertHash),
                    playSound: true);
        };
        _chatModule = new ChatApiModule(_qcc, _chatHandler, _hub);
        _voiceModule = new VoiceApiModule(_voiceHandler, _hub);
        _lanModule = new LanApiModule(_qcc, _lanHandler, _preferences);
        _speedTestModule = new SpeedTestApiModule(_qcc, _speedTestHandler, _hub, _cts);
        _peerModule = new PeerApiModule(_qcc, _chatHandler, _lanHandler, _voiceHandler, _speedTestHandler, _preferences, _hub, _cts);
        _geolocation = new();
        _statusModule = new StatusApiModule(_qcc, _chatHandler, _lanHandler, _voiceHandler, _preferences, _peerModule, _chatModule, _profileModule.Avatars, _geolocation, _presence);

        _hub.GetStatusSnapshot = () => _statusModule.GetStatusObject();
        ConfigureUpdateSocket();

        _qcc.OnPeerAvailable += peer =>
        {
            BroadcastStatusUpdate();
            if (!_qcc.IsTrustedPeer(peer) && !_preferences.Snapshot().ReviewedUntrustedPeers.Contains(Convert.ToBase64String(peer.CertHash)))
                Notifications.Publish("system", "Contacto por revisar", peer.Name ?? "Nuevo dispositivo", "/", Convert.ToHexString(peer.CertHash), "peer:" + peer.Id);
        };
        _qcc.OnPeerDisconnected += _ => BroadcastStatusUpdate();
        _qcc.Discovery.PeerDiscovered += (_, _) => BroadcastStatusUpdate();
        _qcc.Discovery.PeerUpdated += (_, _) => BroadcastStatusUpdate();
        if (_qcc.NatCoordinator != null)
        {
            _qcc.NatCoordinator.MappingChanged += (_, _) => { BroadcastStatusUpdate(); };
        }
        if (_qcc.PeerStore != null)
        {
            _qcc.PeerStore.PeerAdded += (_, _) => BroadcastStatusUpdate();
            _qcc.PeerStore.PeerModified += (_, _) => BroadcastStatusUpdate();
            _qcc.PeerStore.PeerRemoved += (_, _) => BroadcastStatusUpdate();
        }
        if (_lanHandler != null)
        {
            _lanHandler.StateChanged += () => BroadcastStatusUpdate();
        }
    }

    // Standalone Web UI constructor (Mobile / Probe)
    public WebUiServer(
        NodeCapabilities? capabilities = null,
        CancellationTokenSource? cts = null,
        int port = 5071)
    {
        _cts = cts ?? new CancellationTokenSource();
        _port = port;
        Capabilities = capabilities ?? NodeCapabilities.CreateAndroid();

        _hub = new WebUiWebSocketHub(_cts, shutdownWhenNoClients: Capabilities.Platform != "android");
        _instance = this;
        _hub.GetStatusSnapshot = GetFallbackStatusObject;
        ConfigureUpdateSocket();
    }

    public static void BroadcastStatusUpdate(int delayMs = 50)
    {
        _instance?.ScheduleStatusBroadcast(delayMs);
    }

    public void ScheduleStatusBroadcast(int delayMs = 50)
    {
        _hub.NotifyStatusChanged();
    }

    public int Port => _port;
    public string CsrfToken => _csrfToken;

    public void Start()
    {
        for (int attempt = 0; attempt < 20; attempt++, _port++)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                break;
            }
            catch
            {
                try { _listener?.Close(); } catch { }
                _listener = null;
            }
        }

        if (_listener == null || !_listener.IsListening)
        {
            Console.WriteLine("[WebUI] Could not start the local web server.");
            return;
        }

        string url = $"http://127.0.0.1:{_port}/";
        Console.WriteLine($"\nQuicPunch Console: {url}\n");
        // Capture before scheduling: the owner can stop and dispose its source
        // while the listener is completing an accepted request.
        CancellationToken stopToken = _cts.Token;
        _ = Task.Run(() => ListenLoopAsync(stopToken));
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _chatModule?.Dispose();
        _diskModule?.Dispose();
        _geolocation?.Dispose();
        Notifications?.Dispose();
        _presence?.Dispose();
        Updates = null;
        _ = _hub.DisposeAsync();
    }

    private async Task ListenLoopAsync(CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                // An upgraded socket keeps its handler alive until disconnect.
                // Reserve it separately so open tabs cannot consume HTTP capacity.
                // Never wait in the accept loop: a full pool must not block the other.
                SemaphoreSlim slots = context.Request.IsWebSocketRequest ? _webSocketSlots : _requestSlots;
                if (!slots.Wait(0))
                {
                    try
                    {
                        ApplySecurityHeaders(context.Response);
                        context.Response.StatusCode = 503;
                        context.Response.Headers["Retry-After"] = "1";
                        context.Response.ContentLength64 = 0;
                        context.Response.Close();
                    }
                    catch { try { context.Response.Abort(); } catch { } }
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await HandleRequestAsync(context).ConfigureAwait(false); }
                    finally { slots.Release(); }
                });
            }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogEvent($"[HTTP] {ex.Message}"); }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        HttpListenerRequest req = context.Request;
        HttpListenerResponse resp = context.Response;
        bool isWebSocket = false;
        try
        {
            string path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

            ApplySecurityHeaders(resp);

            if (!IsAllowedOrigin(req))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Cross-origin requests are not allowed."), 403).ConfigureAwait(false);
                return;
            }

            if (_watchWeb != null && (path == "/watch" || path.StartsWith("/watch/", StringComparison.Ordinal)))
            {
                isWebSocket = req.IsWebSocketRequest && path == "/watch/ws";
                await _watchWeb.HandleAsync(context).ConfigureAwait(false);
                return;
            }

            if (_diskModule != null && path.StartsWith("/virtual-disk-dav/", StringComparison.Ordinal) &&
                await _diskModule.HandleAsync(path, req, resp).ConfigureAwait(false)) return;

            // WebSocket Upgrade Handler (Protected with Origin + CSRF verification)
            if (req.IsWebSocketRequest && (path == "/ws" || path == "/api/ws"))
            {
                string? wsCsrf = req.QueryString["csrf"];
                if (string.IsNullOrWhiteSpace(wsCsrf) || !string.Equals(wsCsrf, _csrfToken, StringComparison.Ordinal))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Invalid or missing WebSocket CSRF token."), 403).ConfigureAwait(false);
                    return;
                }

                isWebSocket = true;
                await _hub.AcceptSocketAsync(context).ConfigureAwait(false);
                return;
            }

            if (path == "/api/capabilities" && req.HttpMethod == "GET")
            {
                await WebUiContext.WriteJsonAsync(resp, Capabilities.ToResponseObject()).ConfigureAwait(false);
                return;
            }

            if (path == "/api/client-closing")
            {
                if (req.HttpMethod != "POST")
                {
                    resp.StatusCode = 405;
                    return;
                }

                string? headerCsrf = req.Headers["X-QuicPunch-CSRF"];
                string? queryCsrf = req.QueryString["csrf"];
                if (!string.Equals(headerCsrf, _csrfToken, StringComparison.Ordinal) &&
                    !string.Equals(queryCsrf, _csrfToken, StringComparison.Ordinal))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Invalid CSRF token."), 403).ConfigureAwait(false);
                    return;
                }

                _hub.CheckAutoShutdown(TimeSpan.FromSeconds(2.5));
                resp.StatusCode = 204;
                return;
            }

            if (path == "/api/shutdown" && req.HttpMethod == "POST")
            {
                if (!string.Equals(req.Headers["X-QuicPunch-CSRF"], _csrfToken, StringComparison.Ordinal))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Invalid CSRF token."), 403).ConfigureAwait(false);
                    return;
                }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    try { _cts.Cancel(); } catch { }
                    await Task.Delay(1000);
                    Environment.Exit(0);
                });
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage4(true)).ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 204;
                return;
            }

            if (req.HttpMethod == "POST")
            {
                if (!string.Equals(req.Headers["X-QuicPunch-CSRF"], _csrfToken, StringComparison.Ordinal))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Invalid CSRF token."), 403).ConfigureAwait(false);
                    return;
                }
                if (req.ContentLength64 > WebUiContext.GetRequestBodyLimit(path))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Request body is too large."), 413).ConfigureAwait(false);
                    return;
                }
            }

            // Probe handlers can initiate network activity, so they must run only
            // after the same CSRF and body-size checks as the other API writes.
            if ((path == "/api/probe/ping" || path == "/api/mobile/ping") && req.HttpMethod == "POST")
            {
                using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
                string host = doc.RootElement.TryGetProperty("host", out var hEl) ? hEl.GetString() ?? "" : "";
                int pingPort = doc.RootElement.TryGetProperty("port", out var pEl) ? pEl.GetInt32() : 0;
                string fingerprint = doc.RootElement.TryGetProperty("fingerprint", out var fEl) ? fEl.GetString() ?? "" : "";
                string message = ProbePingHandler != null && pingPort is > 0 and <= 65535 && fingerprint.Length == 64
                    ? ProbePingHandler(host, (ushort)pingPort, fingerprint)
                    : "Probe ping handler not configured or invalid parameters.";
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage3(true, message)).ConfigureAwait(false);
                return;
            }

            if (await TryServeStaticAsync(path, resp).ConfigureAwait(false)) return;

            if (await UpdatesApiModule.HandleAsync(Updates, path, req, resp).ConfigureAwait(false)) return;
            if (_appSettingsModule != null && await _appSettingsModule.HandleAsync(path, req, resp).ConfigureAwait(false)) return;
            // Route through sub-modules if available
            if (_profileModule != null && await _profileModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_statusModule != null && await _statusModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_statusModule == null && path == "/api/status" && req.HttpMethod == "GET")
            {
                await WebUiContext.WriteJsonAsync(resp, GetFallbackStatusObject()).ConfigureAwait(false);
                return;
            }

            if (_chatModule != null && await _chatModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_diskModule != null && await _diskModule.HandleAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_voiceModule != null && await _voiceModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_lanModule != null && await _lanModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_speedTestModule != null && await _speedTestModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (_peerModule != null && await _peerModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, "Not found."), 404).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            if (resp.OutputStream.CanWrite) await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, ex.Message), 400).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            if (resp.OutputStream.CanWrite) await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, $"Invalid JSON: {ex.Message}"), 400).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvent($"[HTTP] {ex.Message}");
            if (resp.OutputStream.CanWrite) await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.WebUiServerMessage2(false, ex.Message), 500).ConfigureAwait(false);
        }
        finally
        {
            if (!isWebSocket)
            {
                try { resp.Close(); } catch { }
            }
        }
    }

    private object GetFallbackStatusObject()
    {
        return new global::QuicNet.Serialization.WebUiServerMessage5(false, Capabilities.Platform, Capabilities.ToResponseObject(), new global::QuicNet.Serialization.WebUiServerMessage6(Guid.Empty.ToString(), Capabilities.Platform == "android" ? "QuicPunch Android" : "QuicPunch", "", "", false, false, _port, Array.Empty<string>()), Array.Empty<object>(), Array.Empty<object>(), EventLogs.ToArray(), UserNotifications.ToArray(), Array.Empty<object>(), Array.Empty<object>(), Array.Empty<object>(), new Dictionary<string, int>(), new global::QuicNet.Serialization.WebUiServerMessage7(new global::QuicNet.Serialization.WebUiServerMessage8(0, ""), new global::QuicNet.Serialization.WebUiServerMessage8(0, ""), new global::QuicNet.Serialization.WebUiServerMessage8(0, ""), new global::QuicNet.Serialization.WebUiServerMessage8(0, "")), new global::QuicNet.Serialization.WebUiServerMessage9(Array.Empty<string>(), Array.Empty<object>(), Array.Empty<ushort>(), "Single", Utilities.GetValidLocalIPAddresses().Select(a => a.ToString()).ToArray(), 0, "Unknown"), Array.Empty<object>(), Array.Empty<object>(), (object?)null, Array.Empty<object>(), new global::QuicNet.Serialization.WebUiServerMessage10(false, false, "", false));
    }

    private static readonly string? DevWwwRoot = FindDevWwwRoot();

    private static string? FindDevWwwRoot()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return null;
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "wwwroot"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "wwwroot")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "QuicNet.Shared", "wwwroot"))
        ];
        foreach (var dir in candidates)
        {
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "index.html")))
                return dir;
        }
        return null;
    }

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".mjs"] = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".wasm"] = "application/wasm",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm"
    };

    private async Task<bool> TryServeStaticAsync(string path, HttpListenerResponse resp)
    {
        string cleanPath = path.Split('?')[0].TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrEmpty(cleanPath) ||
            cleanPath.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("chat.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("call.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("voice.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("vpn.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("speedtest.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("settings.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("watch.html", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Equals("virtual-disk.html", StringComparison.OrdinalIgnoreCase))
        {
            cleanPath = "index.html";
        }

        if (!ResourceExists(cleanPath)) return false;

        string ext = Path.GetExtension(cleanPath).ToLowerInvariant();
        string contentType = MimeTypes.GetValueOrDefault(ext, "application/octet-stream");

        byte[]? body;
        if (ext == ".html" || ext == ".htm")
        {
            string content = LoadContent(cleanPath);
            content = InjectSecurityBootstrap(content);
            body = Encoding.UTF8.GetBytes(content);
        }
        else
        {
            body = ReadRawBytes(cleanPath);
            if (body == null) return false;
        }

        resp.ContentType = contentType;
        resp.ContentLength64 = body.Length;
        await resp.OutputStream.WriteAsync(body).ConfigureAwait(false);
        return true;
    }

    private string LoadContent(string file)
    {
        string content = ReadRawFile(file);
        if (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            content = ResolveIncludes(content);
        }
        return content;
    }

    private bool ResourceExists(string relativePath)
    {
        relativePath = relativePath.TrimStart('/', '\\').Replace('\\', '/');
        if (!IsSafeResourcePath(relativePath)) return false;
        string? diskPath = GetDevResourcePath(relativePath);
        if (diskPath != null)
        {
            if (File.Exists(diskPath)) return true;
        }
        return FindManifestResourceName(relativePath) != null;
    }

    private string? FindManifestResourceName(string relativePath)
    {
        if (!IsSafeResourcePath(relativePath)) return null;
        Assembly assembly = typeof(WebUiServer).Assembly;
        string normalized = relativePath.Replace('/', '.').Replace('\\', '.');
        return assembly.GetManifestResourceNames().FirstOrDefault(name =>
            name.Equals("QuicNet.wwwroot." + normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeResourcePath(string relativePath) =>
        !string.IsNullOrEmpty(relativePath) &&
        !Path.IsPathRooted(relativePath) &&
        !relativePath.Any(c => char.IsControl(c) || c is ':' or '%' or '\\') &&
        relativePath.Split('/').All(segment => segment.Length > 0 && segment == segment.TrimEnd(' ', '.'));

    private static string? GetDevResourcePath(string relativePath)
    {
        if (DevWwwRoot == null || !IsSafeResourcePath(relativePath)) return null;
        string root = Path.GetFullPath(DevWwwRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidate.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? candidate : null;
    }

    private byte[]? ReadRawBytes(string relativePath)
    {
        relativePath = relativePath.TrimStart('/', '\\').Replace('\\', '/');
        if (!IsSafeResourcePath(relativePath)) return null;
        string? diskPath = GetDevResourcePath(relativePath);
        if (diskPath != null)
        {
            if (File.Exists(diskPath))
            {
                try { return File.ReadAllBytes(diskPath); } catch { }
            }
        }
        string? resource = FindManifestResourceName(relativePath);
        if (resource == null) return null;
        using Stream? stream = typeof(WebUiServer).Assembly.GetManifestResourceStream(resource);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private string ReadRawFile(string relativePath)
    {
        byte[]? bytes = ReadRawBytes(relativePath);
        return bytes != null ? Encoding.UTF8.GetString(bytes) : "";
    }

    private string ResolveIncludes(string html)
    {
        return Regex.Replace(html, @"<!--\s*@include\s+[""']?([^""'\s>]+)[""']?\s*-->", m =>
        {
            string includePath = m.Groups[1].Value.Trim();
            string childContent = ReadRawFile(includePath);
            return ResolveIncludes(childContent);
        });
    }

    private string InjectSecurityBootstrap(string html)
    {
        string token = JsonSerializer.Serialize(_csrfToken,global::QuicNet.WebUi.WebUiContext.JsonOptions);
        string caps = JsonSerializer.Serialize(Capabilities.ToResponseObject(), global::QuicNet.WebUi.WebUiContext.JsonOptionsFor(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        string script = "<script>(()=>{const csrf=" + token + ";const caps=" + caps + ";window.__qp_csrf=csrf;window.__qp_capabilities=caps;" +
            "const isAnd=caps.platform==='android'||/Android/i.test(navigator.userAgent);" +
            "document.documentElement.classList.add(isAnd?'platform-android':'platform-desktop');" +
            "window.__qp_is_android=isAnd;" +
            "const original=window.fetch.bind(window);" +
            "window.fetch=(input,init={})=>{const u=new URL(typeof input==='string'?input:input.url,location.href);" +
            "const m=(init.method||(typeof Request!=='undefined'&&input instanceof Request?input.method:'GET')).toUpperCase();" +
            "if(u.origin===location.origin&&!['GET','HEAD','OPTIONS'].includes(m)){const h=new Headers(init.headers||(typeof Request!=='undefined'&&input instanceof Request?input.headers:undefined));h.set('X-QuicPunch-CSRF',csrf);init={...init,headers:h};}" +
            "return original(input,init);};})();</script>";
        int index = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? html.Insert(index, script) : script + html;
    }

    private void ApplySecurityHeaders(HttpListenerResponse resp)
    {
        resp.Headers["X-Content-Type-Options"] = "nosniff";
        resp.Headers["Referrer-Policy"] = "no-referrer";
        resp.Headers["Cache-Control"] = "no-store";
        resp.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' data: blob:; connect-src 'self' ws: wss:; object-src 'none'; base-uri 'none'; frame-ancestors 'self'";
    }

    private bool IsAllowedOrigin(HttpListenerRequest req)
    {
        string? origin = req.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            && uri.Port == _port;
    }
}
