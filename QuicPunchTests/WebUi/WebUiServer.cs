using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Photino.NET;
using QuicPunch;
using QuicPunch.Helpers;
using QuicPunchTests.Protocols;
using QuicPunchTests.Settings;
using QuicPunchTests.WebUi.Modules;

namespace QuicPunchTests.WebUi;

internal sealed class WebUiServer
{
    private static WebUiServer? _instance;
    public static WebUiServer? Instance => _instance;

    private readonly QuicPunch.QuicPunch _qcc;
    private readonly ChatHandler _chatHandler;
    private readonly VirtualLanHandler _lanHandler;
    private readonly VoiceCallHandler _voiceHandler;
    private readonly RelayDriveHandler _relayDriveHandler;
    private readonly AppPreferencesStore _preferences;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _requestSlots = new(WebUiContext.MaxConcurrentHttpRequests, WebUiContext.MaxConcurrentHttpRequests);
    private readonly string _csrfToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private readonly WebUiWebSocketHub _hub;
    private readonly ChatApiModule _chatModule;
    private readonly VoiceApiModule _voiceModule;
    private readonly LanApiModule _lanModule;
    private readonly FilesApiModule _filesModule;
    private readonly PeerApiModule _peerModule;
    private readonly StatusApiModule _statusModule;

    private HttpListener? _listener;
    private int _port;

    public static ConcurrentQueue<string> EventLogs { get; } = new();
    public static ConcurrentQueue<UserNotification> UserNotifications { get; } = new();

    public static ConcurrentQueue<ChatMessage> ChatMessages => _instance?._chatModule.ChatMessages ?? _emptyChatMessages;
    public static ConcurrentDictionary<string, ConcurrentQueue<byte[]>> IncomingAudioQueues => _instance?._voiceModule.IncomingAudioQueues ?? _emptyAudioQueues;
    public static ConcurrentQueue<(string PeerId, string SignalType)> CallSignals => _instance?._voiceModule.CallSignals ?? _emptyCallSignals;
    public static ConcurrentQueue<ClipboardItem> ClipboardItems => _instance?._peerModule.ClipboardItems ?? _emptyClipboardItems;
    public static ConcurrentDictionary<Guid, PendingPetitionItem> PendingPetitions => _instance?._peerModule.PendingPetitions ?? _emptyPetitions;
    public static ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> InFlightConnections => _instance?._peerModule.InFlightConnections ?? _emptyInFlight;

    private static readonly ConcurrentQueue<ChatMessage> _emptyChatMessages = new();
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<byte[]>> _emptyAudioQueues = new();
    private static readonly ConcurrentQueue<(string PeerId, string SignalType)> _emptyCallSignals = new();
    private static readonly ConcurrentQueue<ClipboardItem> _emptyClipboardItems = new();
    private static readonly ConcurrentDictionary<Guid, PendingPetitionItem> _emptyPetitions = new();
    private static readonly ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> _emptyInFlight = new();

    public record ChatMessage(string PeerId, string MsgId, string Sender, string Message, DateTime Timestamp, bool IsMe, bool IsConfirmed);
    public record ClipboardItem(string PeerId, string PeerName, string Text, DateTime Timestamp, bool IsMe);
    public record PendingPetitionItem(Guid RequestId, Guid ProtocolId, string ProtocolName, string PeerName, Guid PeerId, TaskCompletionSource<HandshakeDecision> Tcs);
    public record UserNotification(string Id, string Type, string Message, DateTime Timestamp);

    public static void AddNotification(string type, string message)
    {
        var notification = new UserNotification(Guid.NewGuid().ToString("N"), type, message, DateTime.UtcNow);
        UserNotifications.Enqueue(notification);
        while (UserNotifications.Count > 60) UserNotifications.TryDequeue(out _);
        _instance?._hub.Broadcast("notification", notification);
    }

    public static void LogEvent(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        EventLogs.Enqueue(entry);
        while (EventLogs.Count > 150) EventLogs.TryDequeue(out _);
        _instance?._hub.Broadcast("log_event", new { entry });
    }

    public static void ClearPeerPetitions(Guid peerId, Guid? protocolId = null) => _instance?._peerModule.ClearPeerPetitions(peerId, protocolId);
    public static (bool Reused, bool InProgress) StartProtocolConnection(PeerInfo peer, Guid protocolId) => _instance?._peerModule.StartProtocolConnection(peer, protocolId) ?? (false, false);

    public WebUiServer(
        QuicPunch.QuicPunch qcc,
        ChatHandler chatHandler,
        VirtualLanHandler lanHandler,
        VoiceCallHandler voiceHandler,
        RelayDriveHandler relayDriveHandler,
        AppPreferencesStore preferences,
        CancellationTokenSource cts,
        int port = 5000)
    {
        _instance = this;
        _qcc = qcc;
        _chatHandler = chatHandler;
        _lanHandler = lanHandler;
        _voiceHandler = voiceHandler;
        _relayDriveHandler = relayDriveHandler;
        _preferences = preferences;
        _cts = cts;
        _port = port;

        _hub = new WebUiWebSocketHub(_cts);
        _chatModule = new ChatApiModule(_qcc, _chatHandler, _hub);
        _voiceModule = new VoiceApiModule(_voiceHandler, _hub);
        _lanModule = new LanApiModule(_qcc, _lanHandler, _preferences);
        _filesModule = new FilesApiModule(_qcc, _relayDriveHandler, _cts);
        _peerModule = new PeerApiModule(_qcc, _chatHandler, _lanHandler, _voiceHandler, _relayDriveHandler, _preferences, _hub, _cts);
        _statusModule = new StatusApiModule(_qcc, _chatHandler, _lanHandler, _voiceHandler, _relayDriveHandler, _preferences, _peerModule, _chatModule);
    }

    public int Port => _port;
    public string CsrfToken => _csrfToken;

    public void Start(bool openWindow = true)
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
        _ = Task.Run(ListenLoopAsync);
        if (openWindow)
            StartWindow(url);
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }

    private void StartWindow(string url)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var window = new PhotinoWindow()
                    .SetTitle("QuicPunch")
                    .SetUseOsDefaultSize(false);

                string statePath = Path.Combine(_qcc.NodeAppDataPath, "window_state.json");
                Directory.CreateDirectory(_qcc.NodeAppDataPath);
                WindowStateConfig state = LoadWindowState(statePath);
                window.SetSize(state.Width >= 700 ? state.Width : 1320, state.Height >= 500 ? state.Height : 860);
                if (state.Left >= 0 && state.Top >= 0) window.SetLocation(new System.Drawing.Point(state.Left, state.Top));
                else window.Center();
                if (state.IsMaximized) window.SetMaximized(true);
                window.Load(url);

                window.WindowClosing += (_, _) =>
                {
                    SaveWindowState(statePath, window);
                    try { _cts.Cancel(); } catch { }
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                        Environment.Exit(0);
                    });
                    return false;
                };
                window.WaitForClose();
                try { _cts.Cancel(); } catch { }
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    Environment.Exit(0);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebUI] Native window unavailable ({ex.Message}). Opening browser.");
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
            }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                await _requestSlots.WaitAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    try { await HandleRequestAsync(context).ConfigureAwait(false); }
                    finally { _requestSlots.Release(); }
                });
            }
            catch (ObjectDisposedException) { break; }
            catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested) { break; }
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
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Cross-origin requests are not allowed." }, 403).ConfigureAwait(false);
                return;
            }

            // WebSocket Upgrade Handler (Protected with Origin + CSRF verification)
            if (req.IsWebSocketRequest && (path == "/ws" || path == "/api/ws"))
            {
                string? wsCsrf = req.QueryString["csrf"];
                if (string.IsNullOrWhiteSpace(wsCsrf) || !string.Equals(wsCsrf, _csrfToken, StringComparison.Ordinal))
                {
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Invalid or missing WebSocket CSRF token." }, 403).ConfigureAwait(false);
                    return;
                }

                isWebSocket = true;
                await _hub.AcceptSocketAsync(context).ConfigureAwait(false);
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
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Invalid CSRF token." }, 403).ConfigureAwait(false);
                    return;
                }

                _hub.CheckAutoShutdown(TimeSpan.FromSeconds(2.5));
                resp.StatusCode = 204;
                return;
            }

            if (path == "/api/shutdown" && req.HttpMethod == "POST")
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    try { _cts.Cancel(); } catch { }
                    await Task.Delay(1000);
                    Environment.Exit(0);
                });
                await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
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
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Invalid CSRF token." }, 403).ConfigureAwait(false);
                    return;
                }
                if (req.ContentLength64 > WebUiContext.GetRequestBodyLimit(path))
                {
                    await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Request body is too large." }, 413).ConfigureAwait(false);
                    return;
                }
            }

            if (await TryServeStaticAsync(path, resp).ConfigureAwait(false)) return;

            // Route through sub-modules
            if (await _statusModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (await _chatModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (await _voiceModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (await _lanModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (await _filesModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;
            if (await _peerModule.HandleRequestAsync(path, req, resp).ConfigureAwait(false)) return;

            await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Not found." }, 404).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            if (resp.OutputStream.CanWrite) await WebUiContext.WriteJsonAsync(resp, new { success = false, error = ex.Message }, 400).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            if (resp.OutputStream.CanWrite) await WebUiContext.WriteJsonAsync(resp, new { success = false, error = $"Invalid JSON: {ex.Message}" }, 400).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEvent($"[HTTP] {ex.Message}");
            if (resp.OutputStream.CanWrite) await WebUiContext.WriteJsonAsync(resp, new { success = false, error = ex.Message }, 500).ConfigureAwait(false);
        }
        finally
        {
            if (!isWebSocket)
            {
                try { resp.Close(); } catch { }
            }
        }
    }

    private async Task<bool> TryServeStaticAsync(string path, HttpListenerResponse resp)
    {
        string? file = path switch
        {
            "/" or "/index.html" => "index.html",
            "/chat.html" or "/call.html" or "/vpn.html" or "/files.html" or "/clipboard.html" => "index.html",
            "/app.css" => "app.css",
            "/app.js" => "app.js",
            "/dashboard.js" => "dashboard.js",
            "/chat.js" => "chat.js",
            "/voice.js" => "voice.js",
            "/vpn.js" => "vpn.js",
            "/clipboard.js" => "clipboard.js",
            "/files.js" => "files.js",
            _ => null
        };
        if (file == null) return false;

        string content = LoadEmbeddedResource(file);
        string contentType = file.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css; charset=utf-8" :
            file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? "application/javascript; charset=utf-8" : "text/html; charset=utf-8";
        if (file.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) content = InjectSecurityBootstrap(content);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        resp.ContentType = contentType;
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        return true;
    }

    private string LoadEmbeddedResource(string file)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(file, StringComparison.OrdinalIgnoreCase));
        if (resource == null) return file.EndsWith(".html") ? $"<h1>{file} not found</h1>" : "";
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream == null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string InjectSecurityBootstrap(string html)
    {
        string token = JsonSerializer.Serialize(_csrfToken);
        string script = "<script>(()=>{const csrf=" + token + ";window.__qp_csrf=csrf;const original=window.fetch.bind(window);" +
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
        resp.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self' data: blob:; connect-src 'self' ws: wss:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    }

    private bool IsAllowedOrigin(HttpListenerRequest req)
    {
        string? origin = req.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            // For standard HTTP requests without an Origin header (e.g. initial navigation), allow.
            // But if it's a cross-origin WebSocket request, browsers always supply Origin.
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            && uri.Port == _port;
    }

    private static WindowStateConfig LoadWindowState(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<WindowStateConfig>(File.ReadAllText(path)) ?? new WindowStateConfig() : new WindowStateConfig(); }
        catch { return new WindowStateConfig(); }
    }

    private static void SaveWindowState(string path, PhotinoWindow window)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new WindowStateConfig
            {
                Width = window.Width,
                Height = window.Height,
                Left = window.Left,
                Top = window.Top,
                IsMaximized = window.Maximized
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public sealed class WindowStateConfig
{
    public int Width { get; set; } = 1320;
    public int Height { get; set; } = 860;
    public int Left { get; set; } = -1;
    public int Top { get; set; } = -1;
    public bool IsMaximized { get; set; }
}
