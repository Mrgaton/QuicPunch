using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Photino.NET;
using QuicPunch.Helpers;

namespace QuicPunchTests
{
    internal class WebUiServer
    {
        private readonly QuicPunch.QuicPunch _qcc;
        private readonly ChatHandler _chatHandler;
        private readonly VirtualLanHandler _lanHandler;
        private readonly VoiceCallHandler _voiceHandler;
        private readonly CancellationTokenSource _cts;
        private HttpListener? _listener;
        private int _port;

        public static ConcurrentQueue<string> EventLogs { get; } = new();
        public static ConcurrentQueue<ChatMessage> ChatMessages { get; } = new();
        public static ConcurrentQueue<(string PeerId, byte[] Data)> IncomingAudioQueue { get; } = new();
        public static ConcurrentQueue<(string PeerId, string SignalType)> CallSignals { get; } = new();

        public record ChatMessage(string PeerId, string MsgId, string Sender, string Message, DateTime Timestamp, bool IsMe, bool IsConfirmed);
        public record PendingPetitionItem(Guid RequestId, Guid ProtocolId, string ProtocolName, string PeerName, Guid PeerId, TaskCompletionSource<HandshakeDecision> Tcs);
        public static ConcurrentDictionary<Guid, PendingPetitionItem> PendingPetitions { get; } = new();
        public static ConcurrentDictionary<(Guid PeerId, Guid ProtocolId), Guid> InFlightConnections { get; } = new();

        public WebUiServer(QuicPunch.QuicPunch qcc, ChatHandler chatHandler, VirtualLanHandler lanHandler, VoiceCallHandler voiceHandler, CancellationTokenSource cts, int port = 5000)
        {
            _qcc = qcc;
            _chatHandler = chatHandler;
            _lanHandler = lanHandler;
            _voiceHandler = voiceHandler;
            _cts = cts;
            _port = port;

            LogEvent("Web UI Server initialized.");

            _chatHandler.OnMessageReceived += (peer, msg, msgId) =>
            {
                ChatMessages.Enqueue(new ChatMessage(peer.Id.ToString(), msgId, peer.Name ?? "Unknown", msg, DateTime.Now, false, true));
                string preview = msg.Length > 80 ? (msg.StartsWith("{") ? "[Media Attachment]" : msg[..80] + "...") : msg;
                LogEvent($"[CHAT] Message from {peer.Name ?? "Unknown"}: {preview}");
            };

            _chatHandler.OnMessageAckReceived += (peer, msgId) =>
            {
                var list = ChatMessages.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].MsgId == msgId)
                    {
                        list[i] = list[i] with { IsConfirmed = true };
                        break;
                    }
                }
                LogEvent($"[CHAT ACK] Peer {peer.Name} confirmed message receipt.");
            };

            _chatHandler.OnGetHistoryForPeer += (peerId) =>
            {
                return ChatMessages
                    .Where(m => m.PeerId == peerId.ToString())
                    .Select(m => (m.MsgId, m.Sender, m.Message, m.Timestamp))
                    .ToList();
            };

            _chatHandler.OnHistorySyncReceived += (peer, items) =>
            {
                int added = 0;
                foreach (var item in items)
                {
                    if (!ChatMessages.Any(m => m.MsgId == item.MsgId))
                    {
                        ChatMessages.Enqueue(new ChatMessage(peer.Id.ToString(), item.MsgId, item.Sender, item.Content, item.Timestamp, false, true));
                        added++;
                    }
                }
                if (added > 0)
                {
                    LogEvent($"[CHAT SYNC] Synchronized {added} missing message(s) from {peer.Name}");
                }
            };

            _chatHandler.OnPeerConnected += (peer) =>
            {
                var keys = PendingPetitions.Where(kv => kv.Value.PeerId == peer.Id).Select(kv => kv.Key).ToList();
                foreach (var k in keys) PendingPetitions.TryRemove(k, out _);
                LogEvent($"[CHAT] Direct chat connected with {peer.Name}");
            };

            _qcc.OnPeerDisconnected += (peer) =>
            {
                LogEvent($"[NETWORK] Peer {peer.Name ?? "Unknown"} disconnected");
            };

            _qcc.Manager.HandshakeRequested += (request, ct) =>
            {
                string protoName = _qcc.ProtocolHandlers.TryGetValue(request.ProtocolId, out var h) ? h.ProtocolName : "Connection";

                QuicPunch.PeerInfo? matchedPeer = null;
                if (request.PeerId != Guid.Empty && _qcc.AvailablePeers.TryGetValue(request.PeerId, out var pById))
                {
                    matchedPeer = pById;
                }
                else if (request.CertHash != null && request.CertHash.Length > 0)
                {
                    matchedPeer = _qcc.AvailablePeers.Values.FirstOrDefault(p => p.CertHash != null && CryptographicOperations.FixedTimeEquals(p.CertHash, request.CertHash));
                }

                if (matchedPeer == null)
                {
                    matchedPeer = _qcc.AvailablePeers.Values.FirstOrDefault(p => p.ActiveEndPoint?.Equals(request.RemoteEndPoint) == true || (p.Addresses != null && p.Addresses.Any(a => a.Equals(request.RemoteEndPoint.Address))));
                }

                string peerName = matchedPeer?.Name ?? (request.PeerId != Guid.Empty ? $"Peer-{request.PeerId.ToString()[..6]}" : request.RemoteEndPoint.ToString());
                Guid peerId = matchedPeer?.Id ?? (request.PeerId != Guid.Empty ? request.PeerId : Guid.Empty);

                if (PendingPetitions.TryGetValue(request.Id, out var existingItem))
                {
                    return existingItem.Tcs.Task;
                }

                var existingItemForPeer = PendingPetitions.Values.FirstOrDefault(v => peerId != Guid.Empty ? (v.PeerId == peerId && v.ProtocolId == request.ProtocolId) : (v.PeerName == peerName && v.ProtocolId == request.ProtocolId));
                if (existingItemForPeer != null)
                {
                    return existingItemForPeer.Tcs.Task;
                }

                var tcs = new TaskCompletionSource<HandshakeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                var petition = new PendingPetitionItem(request.Id, request.ProtocolId, protoName, peerName, peerId, tcs);
                PendingPetitions[request.Id] = petition;
                LogEvent($"[PETITION] Connection request from {peerName} ({protoName})");

                ct.Register(() =>
                {
                    if (PendingPetitions.TryRemove(request.Id, out var expired))
                    {
                        LogEvent($"[PETITION] Connection request from {expired.PeerName} ({expired.ProtocolName}) timed out");
                    }
                });

                return tcs.Task;
            };

            _voiceHandler.OnAudioDatagramReceived += (peer, audioBytes) =>
            {
                IncomingAudioQueue.Enqueue((peer.Id.ToString(), audioBytes));
                while (IncomingAudioQueue.Count > 300) IncomingAudioQueue.TryDequeue(out _);
            };

            _voiceHandler.OnCallEstablished += (peer) =>
            {
                var keys = PendingPetitions.Where(kv => kv.Value.PeerId == peer.Id).Select(kv => kv.Key).ToList();
                foreach (var k in keys) PendingPetitions.TryRemove(k, out _);

                CallSignals.Enqueue((peer.Id.ToString(), "call-established"));
                LogEvent($"[VOICE] Voice call established with {peer.Name}");
            };

            _voiceHandler.OnCallEnded += (peer) =>
            {
                CallSignals.Enqueue((peer.Id.ToString(), "call-ended"));
                LogEvent($"[VOICE] Voice call ended with {peer.Name}");
            };

            _qcc.OnPeerAvailable += (peer) =>
            {
                LogEvent($"[DISCOVERY] Peer available: {peer.Name} ({peer.Id})");
            };
        }

        public static void LogEvent(string msg)
        {
            var formatted = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            EventLogs.Enqueue(formatted);
            while (EventLogs.Count > 100) EventLogs.TryDequeue(out _);
        }

        public void Start()
        {
            int attempts = 0;
            while (attempts < 20)
            {
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Start();
                    break;
                }
                catch (Exception ex)
                {
                    try { _listener?.Close(); } catch { }
                    _port++;
                    attempts++;
                    if (attempts == 20)
                    {
                        Console.WriteLine($"[WebUI] Failed to start HTTP listener: {ex.Message}");
                    }
                }
            }

            if (_listener == null || !_listener.IsListening)
            {
                Console.WriteLine("[WebUI] Failed to start HTTP listener after 20 attempts.");
                return;
            }

            var url = $"http://127.0.0.1:{_port}/";
            Console.WriteLine($"\n==================================================");
            Console.WriteLine($"  QuicPunch Console running at:");
            Console.WriteLine($"  >>> {url} <<<");
            Console.WriteLine($"==================================================\n");

            _ = Task.Run(async () => await ListenLoopAsync());

            try
            {
                var staThread = new Thread(() =>
                {
                    try
                    {
                        var window = new PhotinoWindow();
                        window.SetTitle("QuicPunch Console")
                              .SetUseOsDefaultSize(false);

                        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuicPunch");
                        Directory.CreateDirectory(configDir);
                        string stateFilePath = Path.Combine(configDir, "window_state.json");

                        WindowStateConfig state = new WindowStateConfig();
                        if (File.Exists(stateFilePath))
                        {
                            try
                            {
                                string json = File.ReadAllText(stateFilePath);
                                state = JsonSerializer.Deserialize<WindowStateConfig>(json) ?? new WindowStateConfig();
                            }
                            catch { }
                        }

                        if (state.Width >= 300 && state.Height >= 200)
                        {
                            window.SetSize(state.Width, state.Height);
                        }
                        else
                        {
                            window.SetSize(1280, 820);
                        }

                        if (state.Left >= 0 && state.Top >= 0)
                        {
                            window.SetLocation(new System.Drawing.Point(state.Left, state.Top));
                        }
                        else
                        {
                            window.Center();
                        }

                        if (state.IsMaximized)
                        {
                            window.SetMaximized(true);
                        }

                        window.Load(url);

                        void SaveState()
                        {
                            try
                            {
                                var currentState = new WindowStateConfig
                                {
                                    Width = window.Width,
                                    Height = window.Height,
                                    Left = window.Left,
                                    Top = window.Top,
                                    IsMaximized = window.Maximized
                                };
                                string json = JsonSerializer.Serialize(currentState, new JsonSerializerOptions { WriteIndented = true });
                                File.WriteAllText(stateFilePath, json);
                            }
                            catch { }
                        }

                        window.WindowClosing += (sender, e) =>
                        {
                            SaveState();
                            try { _cts?.Cancel(); } catch { }
                            Environment.Exit(0);
                            return false;
                        };

                        window.WaitForClose();

                        try { _cts?.Cancel(); } catch { }
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WebUI] Photino window failed ({ex.Message}), falling back to default browser.");
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                        }
                        catch { }
                    }
                });

                staThread.SetApartmentState(ApartmentState.STA);
                staThread.IsBackground = true;
                staThread.Start();
            }
            catch { }
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.Token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(async () => await HandleRequestAsync(ctx));
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    LogEvent($"HTTP Error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (req.HttpMethod == "OPTIONS")
                {
                    resp.StatusCode = 200;
                    resp.Close();
                    return;
                }

                var path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "/";

                if (path == "/" || path == "/index.html")
                {
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(GetHtmlContent());
                    resp.ContentType = "text/html; charset=utf-8";
                    resp.ContentLength64 = htmlBytes.Length;
                    await resp.OutputStream.WriteAsync(htmlBytes);
                }
                else if (path == "/chat.html")
                {
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(GetChatHtmlContent());
                    resp.ContentType = "text/html; charset=utf-8";
                    resp.ContentLength64 = htmlBytes.Length;
                    await resp.OutputStream.WriteAsync(htmlBytes);
                }
                else if (path == "/call.html")
                {
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(GetCallHtmlContent());
                    resp.ContentType = "text/html; charset=utf-8";
                    resp.ContentLength64 = htmlBytes.Length;
                    await resp.OutputStream.WriteAsync(htmlBytes);
                }
                else if (path == "/api/status")
                {
                    var status = GetStatusJson();
                    byte[] jsonBytes = Encoding.UTF8.GetBytes(status);
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = jsonBytes.Length;
                    await resp.OutputStream.WriteAsync(jsonBytes);
                }
                else if (path == "/api/voice-send" && req.HttpMethod == "POST")
                {
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    byte[] rawAudio = ms.ToArray();
                    string peerIdStr = req.Headers["X-Peer-Id"] ?? "";
                    if (rawAudio.Length > 0)
                    {
                        if (string.IsNullOrEmpty(peerIdStr) || peerIdStr == "all")
                        {
                            _ = VoiceCallHandler.BroadcastAudioDatagramAsync(rawAudio);
                        }
                        else if (Guid.TryParse(peerIdStr, out var pid))
                        {
                            _ = VoiceCallHandler.SendAudioDatagramAsync(pid, rawAudio);
                        }
                    }
                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/voice-hangup" && req.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();
                    string peerIdStr = "";
                    int idx = body.IndexOf("\"peerId\"");
                    if (idx >= 0)
                    {
                        int start = body.IndexOf('"', idx + 8) + 1;
                        int end = body.IndexOf('"', start);
                        if (start > 0 && end > start) peerIdStr = body.Substring(start, end - start);
                    }
                    if (Guid.TryParse(peerIdStr, out var pid))
                    {
                        if (VoiceCallHandler.ActiveCalls.TryRemove(pid, out var call))
                        {
                            try { call.Stream.Close(); } catch { }
                            _ = Task.Run(async () => { try { await call.Connection.DisposeAsync(); } catch { } });
                            CallSignals.Enqueue((pid.ToString(), "call-ended"));
                            LogEvent($"[VOICE] Ended voice call with {call.Peer.Name}");
                        }
                    }
                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/tor-start" && req.HttpMethod == "POST")
                {
                    int vPort = 0;
                    try
                    {
                        using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                        string body = await r.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("port", out var pEl)) vPort = pEl.GetInt32();
                        }
                    }
                    catch { }

                    if (!_qcc.IsTorStarted)
                    {
                        LogEvent("[TOR] Starting Tor runtime and hidden service...");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _qcc.StartTorAsync(vPort, cancellationToken: _cts.Token);
                                LogEvent($"[TOR] Hidden service ready at {_qcc.TorOnionAddress}");
                            }
                            catch (Exception ex)
                            {
                                LogEvent($"[TOR ERROR] Failed to start Tor: {ex.Message}");
                            }
                        });
                    }
                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true,\"message\":\"Tor starting in background\"}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/tor-stop" && req.HttpMethod == "POST")
                {
                    LogEvent("[TOR] Stopping Tor runtime...");
                    await _qcc.StopTorAsync();
                    LogEvent("[TOR] Tor service stopped.");
                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/tor-connect" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string onion = doc.RootElement.GetProperty("onion").GetString() ?? "";
                    int port = doc.RootElement.TryGetProperty("port", out var pEl) ? pEl.GetInt32() : 443;

                    try
                    {
                        if (string.IsNullOrWhiteSpace(onion))
                        {
                            throw new ArgumentException("Onion address is required.");
                        }

                        if (!_qcc.IsTorStarted)
                        {
                            throw new InvalidOperationException("El servicio Tor no está iniciado. Inicia Tor primero.");
                        }

                        if (onion.Contains(':'))
                        {
                            var parts = onion.Split(':');
                            onion = parts[0];
                            if (int.TryParse(parts[1], out int parsedPort)) port = parsedPort;
                        }

                        LogEvent($"[TOR] Connecting to remote onion service {onion}:{port}...");
                        await _qcc.ConnectTorAsync(onion, port, _cts.Token);
                        LogEvent($"[TOR] Connected to {onion}:{port}!");

                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    catch (Exception ex)
                    {
                        LogEvent($"[TOR ERROR] {ex.Message}");
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = false, error = ex.Message }));
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/tor-newnym" && req.HttpMethod == "POST")
                {
                    if (_qcc.TorManager != null)
                    {
                        LogEvent("[TOR] Requesting new clean circuits (NEWNYM)...");
                        await _qcc.TorManager.RequestNewCircuitsAsync(_cts.Token);
                        LogEvent("[TOR] New circuits requested successfully.");
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Tor is not running\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/connect-token" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string token = doc.RootElement.GetProperty("token").GetString() ?? "";
                    token = token.Trim();

                    try
                    {
                        var peer = Utilities.DecodeEndpointToken(token);
                        bool isTor = peer.NetworkType == QuicPunch.QuicPunch.NetworkType.Tor || (peer.Addresses == null || peer.Addresses.Length == 0 && !string.IsNullOrEmpty(peer.OnionAddress));
                        bool isWan = peer.Addresses != null && peer.Addresses.Length > 0;

                        if (isTor && !_qcc.IsTorStarted)
                        {
                            throw new InvalidOperationException("El token es de Tor y el servicio Tor no está iniciado. Inicia Tor primero.");
                        }

                        if (isWan && (!_qcc.IsStarted || _qcc.udp == null))
                        {
                            throw new InvalidOperationException("El token es de WAN y el servicio WAN/UDP no está iniciado.");
                        }

                        LogEvent($"Connecting via {(isTor ? "Tor" : "WAN")} token: {token[..Math.Min(20, token.Length)]}...");
                        await _qcc.PeerInterrogation(peer, _cts.Token);

                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    catch (Exception ex)
                    {
                        LogEvent($"[CONNECT ERROR] {ex.Message}");
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { success = false, error = ex.Message }));
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/cancel-interrogation" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string id = doc.RootElement.GetProperty("id").GetString() ?? "";

                    if (_qcc.CancelInterrogation(id))
                    {
                        LogEvent($"[NETWORK] Canceled hole punching / interrogation attempt ({id})");
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Interrogation session not found\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/disconnect-peer" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string peerIdStr = doc.RootElement.GetProperty("peerId").GetString() ?? "";

                    if (Guid.TryParse(peerIdStr, out var peerId))
                    {
                        _qcc.DisconnectPeer(peerId);
                        LogEvent($"[NETWORK] Disconnected peer {peerId}");
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Invalid peer ID\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/voice-poll" && req.HttpMethod == "GET")
                {
                    var signals = new List<object>();
                    while (CallSignals.TryDequeue(out var sig))
                    {
                        signals.Add(new { peerId = sig.PeerId, signal = sig.SignalType });
                    }

                    var chunks = new List<object>();
                    while (IncomingAudioQueue.TryDequeue(out var item))
                    {
                        chunks.Add(new { peerId = item.PeerId, data = Convert.ToBase64String(item.Data) });
                    }

                    var pollObj = new { signals, chunks };
                    byte[] respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pollObj));
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if ((path == "/api/respond-petition" || path == "/api/accept-petition" || path == "/api/decline-petition") && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string reqIdStr = doc.RootElement.GetProperty("requestId").GetString() ?? "";
                    bool accept = path == "/api/accept-petition" || (path != "/api/decline-petition" && doc.RootElement.TryGetProperty("accept", out var accEl) && accEl.GetBoolean());

                    if (Guid.TryParse(reqIdStr, out var reqId) && PendingPetitions.TryRemove(reqId, out var item))
                    {
                        if (accept)
                        {
                            ushort assignedPort = 0;
                            item.Tcs.TrySetResult(new HandshakeDecision(true, assignedPort, CancellationToken.None));
                            LogEvent($"[PETITION] Accepted connection request from {item.PeerName} ({item.ProtocolName})");
                        }
                        else
                        {
                            item.Tcs.TrySetResult(new HandshakeDecision(false, null, CancellationToken.None));
                            LogEvent($"[PETITION] Declined connection request from {item.PeerName} ({item.ProtocolName})");
                        }

                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Petition not found or expired\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/save-peer" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string peerIdStr = doc.RootElement.TryGetProperty("peerId", out var pEl) ? pEl.GetString() ?? "" : "";
                    string certHashBase64 = doc.RootElement.TryGetProperty("certHash", out var cEl) ? cEl.GetString() ?? "" : "";
                    bool shouldSave = !doc.RootElement.TryGetProperty("save", out var sEl) || sEl.GetBoolean();
                    bool autoConnect = !doc.RootElement.TryGetProperty("autoConnect", out var acEl) || acEl.GetBoolean();

                    QuicPunch.PeerInfo? peerToSave = null;
                    byte[]? targetCertHash = null;

                    if (Guid.TryParse(peerIdStr, out var pid) && _qcc.AvailablePeers.TryGetValue(pid, out var p))
                    {
                        peerToSave = p;
                        targetCertHash = p.CertHash;
                    }
                    else if (!string.IsNullOrEmpty(certHashBase64))
                    {
                        targetCertHash = Convert.FromBase64String(certHashBase64);
                        peerToSave = _qcc.AvailablePeers.Values.FirstOrDefault(x => x.CertHash != null && CryptographicOperations.FixedTimeEquals(x.CertHash, targetCertHash));
                    }

                    if (shouldSave)
                    {
                        if (peerToSave != null)
                        {
                            _qcc.SavePeer(peerToSave, autoConnect);
                            LogEvent($"[PEER STORE] Saved peer {peerToSave.Name ?? "Peer"} in database");
                            byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"saved\":true,\"isSaved\":true,\"autoConnect\":{autoConnect.ToString().ToLower()}}}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
                        }
                        else
                        {
                            resp.StatusCode = 404;
                            byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Connected peer not found\"}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes);
                        }
                    }
                    else
                    {
                        bool removed = false;
                        if (targetCertHash != null && targetCertHash.Length > 0)
                        {
                            removed = _qcc.RemoveSavedPeer(targetCertHash);
                        }
                        else if (peerToSave?.CertHash != null)
                        {
                            removed = _qcc.RemoveSavedPeer(peerToSave.CertHash);
                        }

                        if (removed)
                        {
                            LogEvent($"[PEER STORE] Removed peer {peerToSave?.Name ?? "Peer"} from database");
                            byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true,\"saved\":false,\"isSaved\":false}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
                        }
                        else
                        {
                            resp.StatusCode = 404;
                            byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Peer was not in database or could not be removed\"}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes);
                        }
                    }
                }
                else if (path == "/api/save-all-peers" && req.HttpMethod == "POST")
                {
                    int count = 0;
                    foreach (var p in _qcc.AvailablePeers.Values)
                    {
                        if (p.CertHash != null)
                        {
                            _qcc.SavePeer(p, true);
                            count++;
                        }
                    }
                    LogEvent($"[PEER STORE] Saved {count} connected peer(s) for auto-connect on restart");
                    byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"savedCount\":{count}}}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/toggle-saved-peer-autoconnect" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string certHashBase64 = doc.RootElement.GetProperty("certHash").GetString() ?? "";
                    bool autoConnect = doc.RootElement.GetProperty("autoConnect").GetBoolean();
                    byte[] certHash = Convert.FromBase64String(certHashBase64);

                    if (_qcc.PeerStore != null && _qcc.PeerStore.ToggleAutoConnect(certHash, autoConnect))
                    {
                        LogEvent($"[PEER STORE] Updated auto-connect on startup to {autoConnect}");
                        byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"autoConnect\":{autoConnect.ToString().ToLower()}}}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Peer not found in database\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/connect-saved-peer" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string certHashBase64 = doc.RootElement.GetProperty("certHash").GetString() ?? "";
                    byte[] certHash = Convert.FromBase64String(certHashBase64);

                    if (_qcc.PeerStore != null && _qcc.PeerStore.TryGet(certHash, out var sp) && sp != null)
                    {
                        _qcc.ExpectedPeerCerts.Add(sp.CertHash);
                        _qcc.TrustPeer(sp.CertHash);

                        if (!string.IsNullOrEmpty(sp.OnionAddress) && _qcc.IsTorStarted)
                        {
                            _ = Task.Run(async () =>
                            {
                                try { await _qcc.ConnectTorAsync(sp.OnionAddress, sp.MinPort > 0 ? sp.MinPort : 443, _cts.Token); }
                                catch (Exception ex) { LogEvent($"[CONNECT TOR] Failed for {sp.Name ?? sp.OnionAddress}: {ex.Message}"); }
                            });
                        }
                        else if (sp.Addresses != null && sp.Addresses.Length > 0)
                        {
                            var peerInfo = QuicPunch.QuicPunch.CreatePeerInfoFromSavedPeer(sp);

                            _ = Task.Run(async () =>
                            {
                                try { await _qcc.PeerInterrogation(peerInfo, _cts.Token); }
                                catch (Exception ex) { LogEvent($"[INTERROGATION] Failed for {sp.Name ?? "Peer"}: {ex.Message}"); }
                            });
                        }

                        LogEvent($"[PEER STORE] Initiated connection to saved peer {sp.Name ?? "Peer"}");
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Saved peer not found in database\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/saved-peer-update" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string certHashBase64 = doc.RootElement.GetProperty("certHash").GetString() ?? "";
                    string name = doc.RootElement.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    string onionAddress = doc.RootElement.TryGetProperty("onionAddress", out var onEl) ? onEl.GetString() ?? "" : "";
                    bool autoConnect = !doc.RootElement.TryGetProperty("autoConnect", out var acEl) || acEl.GetBoolean();
                    int minPort = doc.RootElement.TryGetProperty("minPort", out var minEl) ? minEl.GetInt32() : 0;
                    int maxPort = doc.RootElement.TryGetProperty("maxPort", out var maxEl) ? maxEl.GetInt32() : 0;
                    string addrsStr = doc.RootElement.TryGetProperty("addresses", out var adEl) ? adEl.GetString() ?? "" : "";

                    var addrs = addrsStr.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => IPAddress.TryParse(s.Trim(), out var ip) ? ip : null)
                        .Where(ip => ip != null)
                        .Select(ip => ip!)
                        .ToArray();

                    if (addrs.Length == 0 && !string.IsNullOrEmpty(onionAddress))
                    {
                        addrs = new[] { IPAddress.Loopback };
                    }

                    byte[] certHash = Convert.FromBase64String(certHashBase64);

                    if (_qcc.PeerStore != null)
                    {
                        _qcc.PeerStore.AddOrUpdate(addrs, minPort, maxPort, certHash, null, string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(onionAddress) ? null : onionAddress, autoConnect);
                        _qcc.TrustPeer(certHash);
                        LogEvent($"[PEER STORE] Updated saved peer in database ({name})");
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 500;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"PeerStore unavailable\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/saved-peer-delete" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string certHashBase64 = doc.RootElement.GetProperty("certHash").GetString() ?? "";
                    byte[] certHash = Convert.FromBase64String(certHashBase64);

                    if (_qcc.RemoveSavedPeer(certHash))
                    {
                        LogEvent($"[PEER STORE] Removed saved peer from database");
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Peer not found in database\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if ((path == "/api/lan-configure-ip" || path == "/api/lan-config") && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string newIp = doc.RootElement.GetProperty("ip").GetString() ?? "";
                    string mask = doc.RootElement.TryGetProperty("subnetMask", out var smEl) ? smEl.GetString() ?? "255.0.0.0" : (doc.RootElement.TryGetProperty("subnet", out var sEl) ? sEl.GetString() ?? "255.0.0.0" : "255.0.0.0");

                    if (_lanHandler.SetVirtualIp(newIp, mask))
                    {
                        LogEvent($"[LAN] Configured Virtual IP address to {newIp} ({mask})");
                        byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"ip\":\"{newIp}\"}}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Invalid IP address or failed to apply netsh setting\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if ((path == "/api/change-listener-port" || path == "/api/change-port") && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    int port = doc.RootElement.TryGetProperty("port", out var pEl) ? pEl.GetInt32() : 0;

                    if (port > 0 && port <= 65535)
                    {
                        bool success = _qcc.RebindListenerPort((ushort)port);
                        if (success)
                        {
                            LogEvent($"[NETWORK] Listener port successfully changed to {port}");
                            byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"port\":{_qcc.LocalDiscoveryPort},\"listenerPort\":{_qcc.LocalDiscoveryPort}}}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
                        }
                        else
                        {
                            resp.StatusCode = 400;
                            byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Failed to bind port\"}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes);
                        }
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Invalid port number\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/connect-peer" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string peerIdStr = doc.RootElement.TryGetProperty("peerId", out var pIdEl) ? pIdEl.GetString() ?? "" : "";
                    
                    string protocol = "";
                    if (doc.RootElement.TryGetProperty("protocolId", out var prIdEl)) protocol = prIdEl.GetString() ?? "";
                    else if (doc.RootElement.TryGetProperty("protocol", out var prEl)) protocol = prEl.GetString() ?? "";

                    if (Guid.TryParse(peerIdStr, out var pid) && _qcc.AvailablePeers.TryGetValue(pid, out var peer))
                    {
                        Guid protoId = _chatHandler.ProtocolId;
                        if (Guid.TryParse(protocol, out var parsedProtoId))
                        {
                            protoId = parsedProtoId;
                        }
                        else if (protocol.Equals("lan", StringComparison.OrdinalIgnoreCase) || protocol.Equals("friendslan", StringComparison.OrdinalIgnoreCase))
                        {
                            protoId = _lanHandler.ProtocolId;
                        }
                        else if (protocol.Equals("voice", StringComparison.OrdinalIgnoreCase) || protocol.Equals("call", StringComparison.OrdinalIgnoreCase) || protocol.Equals("voicecall", StringComparison.OrdinalIgnoreCase))
                        {
                            protoId = _voiceHandler.ProtocolId;
                        }

                        bool alreadyConnected = false;
                        if (protoId == _chatHandler.ProtocolId && ChatHandler.ActiveChats.ContainsKey(pid))
                        {
                            alreadyConnected = true;
                        }
                        else if (protoId == _voiceHandler.ProtocolId && VoiceCallHandler.ActiveCalls.ContainsKey(pid))
                        {
                            alreadyConnected = true;
                        }
                        else if (protoId == _lanHandler.ProtocolId && _lanHandler.ActivePeers.Values.Any(p => p.Peer.Id == pid))
                        {
                            alreadyConnected = true;
                        }
                        else if (_qcc.HasActiveProtocolSession(pid, protoId))
                        {
                            alreadyConnected = true;
                        }

                        if (alreadyConnected)
                        {
                            LogEvent($"Already connected to {peer.Name} ({peer.Id}) on {protoId}, reusing existing session.");
                            byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true,\"reused\":true}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
                        }
                        else if (_qcc.ProtocolHandlers.TryGetValue(protoId, out var handler))
                        {
                            var inFlightKey = (peer.Id, protoId);
                            if (InFlightConnections.TryGetValue(inFlightKey, out _) || _qcc.IsConnectionInFlight(peer.Id, protoId))
                            {
                                LogEvent($"Connection to {peer.Name} for {handler.ProtocolName} already in progress. Reusing existing initiation.");
                                byte[] inProgBytes = Encoding.UTF8.GetBytes("{\"success\":true,\"in_progress\":true}");
                                resp.ContentType = "application/json";
                                resp.ContentLength64 = inProgBytes.Length;
                                await resp.OutputStream.WriteAsync(inProgBytes);
                                return;
                            }

                            var attemptId = Guid.NewGuid();
                            if (!InFlightConnections.TryAdd(inFlightKey, attemptId))
                            {
                                LogEvent($"Connection to {peer.Name} for {handler.ProtocolName} already in progress. Reusing existing initiation.");
                                byte[] inProgBytes = Encoding.UTF8.GetBytes("{\"success\":true,\"in_progress\":true}");
                                resp.ContentType = "application/json";
                                resp.ContentLength64 = inProgBytes.Length;
                                await resp.OutputStream.WriteAsync(inProgBytes);
                                return;
                            }

                            var keys = PendingPetitions.Where(kv => kv.Value.PeerId == peer.Id && kv.Value.ProtocolId == protoId).Select(kv => kv.Key).ToList();
                            foreach (var k in keys) PendingPetitions.TryRemove(k, out _);

                            LogEvent($"Initiating {handler.ProtocolName} connection with {peer.Name} ({peer.Id})...");
                            ushort localPort = 0;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _qcc.InitQuicConnection(protoId, peer, localPort, _cts.Token);
                                }
                                finally
                                {
                                    InFlightConnections.TryRemove(new KeyValuePair<(Guid PeerId, Guid ProtocolId), Guid>(inFlightKey, attemptId));
                                }
                            });

                            byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
                        }
                        else
                        {
                            resp.StatusCode = 400;
                            byte[] errBytes = Encoding.UTF8.GetBytes($"{{\"success\":false,\"error\":\"Protocol handler {protoId} not found\"}}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes);
                        }
                    }
                    else
                    {
                        resp.StatusCode = 404;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Peer not found\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else if (path == "/api/chat-send" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string peerIdStr = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                    string message = doc.RootElement.GetProperty("message").GetString() ?? "";

                    if (Guid.TryParse(peerIdStr, out var pid))
                    {
                        var (sent, msgId) = await ChatHandler.SendMessageAsync(pid, _qcc.CurrentPeer?.Name ?? "Me", message);
                        if (sent)
                        {
                            ChatMessages.Enqueue(new ChatMessage(pid.ToString(), msgId, "Me", message, DateTime.Now, true, false));
                            string preview = message.Length > 80 ? (message.StartsWith("{") ? "[Media Attachment]" : message[..80] + "...") : message;
                            LogEvent($"[CHAT OUT] Sent to {pid}: {preview}");

                            byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"msgId\":\"{msgId}\"}}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
                        }
                        else
                        {
                            resp.StatusCode = 400;
                            byte[] errBytes = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"Peer disconnected. Initiate connection first.\"}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes);
                        }
                    }
                }
                else if (path == "/api/settings/auto-accept" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    bool autoAccept = doc.RootElement.GetProperty("autoAcceptAll").GetBoolean();
                    _qcc.AutoAcceptConnections = autoAccept;
                    LogEvent($"[SETTINGS] Auto-accept all connections set to {autoAccept}");

                    byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"autoAcceptAll\":{autoAccept.ToString().ToLower()}}}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/peer/auto-accept" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string peerIdStr = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                    bool autoAccept = doc.RootElement.GetProperty("autoAccept").GetBoolean();

                    if (Guid.TryParse(peerIdStr, out var pid))
                    {
                        _qcc.SetPeerAutoAccept(pid, autoAccept);
                        LogEvent($"[SETTINGS] Auto-accept for peer {pid} set to {autoAccept}");

                        byte[] respBytes = Encoding.UTF8.GetBytes($"{{\"success\":true,\"peerId\":\"{pid}\",\"autoAccept\":{autoAccept.ToString().ToLower()}}}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\":\"Invalid peer ID\"}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = errBytes.Length;
                        await resp.OutputStream.WriteAsync(errBytes);
                    }
                }
                else
                {
                    resp.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                resp.StatusCode = 500;
                LogEvent($"Handler error: {ex.Message}");
            }
            finally
            {
                resp.Close();
            }
        }

        private string GetStatusJson()
        {
            try
            {
                string wanToken = _qcc.GetWanToken() ?? "";
                string torToken = _qcc.GetTorToken() ?? "";
                string myToken = wanToken;
                string quickUri = $"https://gato.ovh/protred?uri=QP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(wanToken))}";
                string nodeName = _qcc.CurrentPeer?.Name ?? "LocalNode";
                string nodeId = _qcc.CurrentPeer?.Id.ToString() ?? "";
                int minPort = _qcc.CurrentPeer?.MinPort ?? 0;
                int maxPort = _qcc.CurrentPeer?.MaxPort ?? 0;
                int listenerPort = _qcc.LocalDiscoveryPort;
                string networkType = _qcc.CurrentPeer?.NetworkType.ToString() ?? "Unknown";

                var allAddrs = new List<string>();
                if (_qcc.CurrentPeer?.Addresses != null)
                {
                    foreach (var a in _qcc.CurrentPeer.Addresses)
                    {
                        if (a != null && !allAddrs.Contains(a.ToString()))
                            allAddrs.Add(a.ToString());
                    }
                }

                try
                {
                    foreach (var ip in Utilities.GetValidLocalIPAddresses())
                    {
                        string s = ip.ToString();
                        if (!allAddrs.Contains(s)) allAddrs.Add(s);
                    }
                }
                catch { }

                var savedPeersList = (_qcc.PeerStore?.SavedPeers?.ToList() ?? new List<PeerStore.SavedPeer>()).Select(sp => new
                {
                    name = sp.Name ?? "",
                    onionAddress = sp.OnionAddress ?? "",
                    autoConnect = sp.AutoConnect,
                    certHash = Convert.ToBase64String(sp.CertHash),
                    minPort = sp.MinPort,
                    maxPort = sp.MaxPort,
                    networkType = (int)sp.NetworkType,
                    addresses = sp.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>()
                }).ToList();

                var savedCertHashSet = new HashSet<string>(savedPeersList.Select(s => s.certHash));
                var savedAutoConnectSet = new HashSet<string>(savedPeersList.Where(s => s.autoConnect).Select(s => s.certHash));
                var autoAcceptedPeerIds = _qcc.GetAutoAcceptedPeers();
                var autoAcceptedIdSet = new HashSet<Guid>(autoAcceptedPeerIds);

                var availablePeersList = _qcc.AvailablePeers.Values.Select(p =>
                {
                    string cHashStr = p.CertHash != null ? Convert.ToBase64String(p.CertHash) : "";
                    bool isSaved = !string.IsNullOrEmpty(cHashStr) && savedCertHashSet.Contains(cHashStr);
                    bool autoConnectOnStartup = !string.IsNullOrEmpty(cHashStr) && savedAutoConnectSet.Contains(cHashStr);
                    bool isAutoAccepted = autoAcceptedIdSet.Contains(p.Id);

                    return new
                    {
                        id = p.Id.ToString(),
                        name = p.Name ?? "",
                        ping = p.Ping.HasValue ? Math.Round(p.Ping.Value.TotalMilliseconds, 1) : -1,
                        hasPing = p.Ping.HasValue,
                        activeEndPoint = p.ActiveEndPoint?.ToString() ?? (p.OnionAddress != null ? $"{p.OnionAddress}:{p.MinPort}" : "Unknown"),
                        minPort = p.MinPort,
                        maxPort = p.MaxPort,
                        addresses = p.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                        activeTransport = p.ActiveTransport.ToString().ToLowerInvariant(),
                        onionAddress = p.OnionAddress ?? "",
                        isTor = p.ActiveTransport == global::QuicPunch.QuicPunch.TransportType.Tor || p.NetworkType == global::QuicPunch.QuicPunch.NetworkType.Tor || !string.IsNullOrEmpty(p.OnionAddress),
                        hasCipher = p.PeerCipher != null,
                        networkType = p.NetworkType.ToString(),
                        isSaved,
                        isAutoAccepted,
                        autoConnectOnStartup,
                        lastSeenSecondsAgo = p.LastSeen > DateTime.MinValue ? (int)Math.Max(0, (DateTime.UtcNow - p.LastSeen).TotalSeconds) : 99999,
                        lastSeenFormatted = p.LastSeen > DateTime.MinValue ? p.LastSeen.ToLocalTime().ToString("HH:mm:ss") : "Never"
                    };
                }).ToList();

                var activeChatsList = ChatHandler.ActiveChats.Values.Select(c => new
                {
                    peerId = c.Peer.Id.ToString(),
                    peerName = c.Peer.Name ?? ""
                }).ToList();

                var msgsList = ChatMessages.TakeLast(100).Select(m => new
                {
                    peerId = m.PeerId ?? "",
                    msgId = m.MsgId ?? "",
                    sender = m.Sender ?? "",
                    message = m.Message ?? "",
                    time = m.Timestamp.ToString("HH:mm:ss"),
                    isMe = m.IsMe,
                    isConfirmed = m.IsConfirmed
                }).ToList();

                var registeredProtocols = _qcc.ProtocolHandlers.Select(kv => new
                {
                    id = kv.Key.ToString(),
                    name = kv.Value.ProtocolName ?? "Protocol"
                }).ToList();

                var pendingPetitionsList = PendingPetitions.Values.Select(p => new
                {
                    requestId = p.RequestId.ToString(),
                    peerName = p.PeerName,
                    peerId = p.PeerId.ToString(),
                    protocolName = p.ProtocolName,
                    protocolId = p.ProtocolId.ToString()
                }).ToList();

                var activeVoiceList = VoiceCallHandler.ActiveCalls.Values.Select(c => new
                {
                    peerId = c.Peer.Id.ToString(),
                    peerName = c.Peer.Name ?? ""
                }).ToList();

                var activeInterrogationsList = _qcc.ActiveInterrogations.Values.Select(s => new
                {
                    id = s.Id,
                    peerName = s.Peer.Name ?? "Unknown",
                    addresses = s.Peer.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                    minPort = s.Peer.MinPort,
                    maxPort = s.Peer.MaxPort,
                    certHash = s.Peer.CertHash != null ? Convert.ToBase64String(s.Peer.CertHash) : "",
                    startTime = s.StartTime.ToString("HH:mm:ss")
                }).ToList();

                var logsList = EventLogs.TakeLast(50).ToList();

                var activeLanPeersList = _lanHandler.ActivePeers.Values.Select(p => new
                {
                    peerId = p.Peer.Id.ToString(),
                    peerName = p.Peer.Name ?? "Unknown",
                    virtualIp = p.RemoteIp.ToString(),
                    connectedAt = p.ConnectedAt.ToString("HH:mm:ss"),
                    rxPackets = p.RxPackets,
                    txPackets = p.TxPackets,
                    rxBytes = p.RxBytes,
                    txBytes = p.TxBytes
                }).ToList();

                var lanStatusObj = new
                {
                    adapterName = _lanHandler.AdapterName,
                    adapterStatus = _lanHandler.AdapterStatus,
                    lastError = _lanHandler.LastError ?? "",
                    localVirtualIp = _lanHandler.LocalIp.ToString(),
                    subnetMask = _lanHandler.SubnetMask,
                    mtu = _lanHandler.Mtu,
                    activePeers = activeLanPeersList,
                    totalRxPackets = _lanHandler.TotalRxPackets,
                    totalTxPackets = _lanHandler.TotalTxPackets,
                    totalRxBytes = _lanHandler.TotalRxBytes,
                    totalTxBytes = _lanHandler.TotalTxBytes
                };

                var torStatusObj = new
                {
                    isStarted = _qcc.IsTorStarted,
                    onionAddress = _qcc.TorOnionAddress ?? "",
                    serviceId = _qcc.TorIdentity?.ServiceId ?? "",
                    socksPort = _qcc.TorManager?.SocksPort ?? 0,
                    controlPort = _qcc.TorManager?.ControlPort ?? 0,
                    virtualPort = _qcc.TorHub?.VirtualPort ?? 0,
                    bootstrapProgress = _qcc.TorBootstrapProgress,
                    bootstrapStatus = _qcc.TorBootstrapStatus,
                    torNodeId = _qcc.TorCurrentPeer?.Id.ToString() ?? "",
                    torCertHash = _qcc.TorCertManager?.CertPublicHash != null ? Convert.ToBase64String(_qcc.TorCertManager.CertPublicHash) : "",
                    torLastError = _qcc.TorLastError ?? ""
                };

                var statusObj = new
                {
                    nodeName,
                    nodeId,
                    minPort,
                    maxPort,
                    listenerPort,
                    networkType,
                    token = myToken,
                    wanToken,
                    torToken,
                    quickUri,
                    publicEndpoints = allAddrs,
                    availablePeers = availablePeersList,
                    savedPeers = savedPeersList,
                    registeredProtocols,
                    activeChats = activeChatsList,
                    activeVoiceCalls = activeVoiceList,
                    activeInterrogations = activeInterrogationsList,
                    chatMessages = msgsList,
                    pendingPetitions = pendingPetitionsList,
                    autoAcceptAll = _qcc.AutoAcceptConnections,
                    autoAcceptedPeers = _qcc.GetAutoAcceptedPeers().Select(g => g.ToString()).ToList(),
                    lanStatus = lanStatusObj,
                    torStatus = torStatusObj,
                    torOnionAddress = _qcc.TorOnionAddress ?? "",
                    logs = logsList
                };

                return JsonSerializer.Serialize(statusObj);
            }
            catch (Exception ex)
            {
                LogEvent($"GetStatusJson exception: {ex.Message}");
                return JsonSerializer.Serialize(new { nodeName = "Error", logs = new[] { $"Status error: {ex.Message}" } });
            }
        }

        private static string LoadEmbeddedResourceInMemory(string filename)
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(filename, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(resourceName))
            {
                return $"<h1>Embedded Resource {filename} Not Found</h1>";
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return $"<h1>Failed to stream {filename} from assembly memory</h1>";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private string GetHtmlContent() => LoadEmbeddedResourceInMemory("index.html");
        private string GetCallHtmlContent() => LoadEmbeddedResourceInMemory("call.html");
        private string GetChatHtmlContent() => LoadEmbeddedResourceInMemory("chat.html");
    }

    public class WindowStateConfig
    {
        public int Width { get; set; } = 1280;
        public int Height { get; set; } = 820;
        public int Left { get; set; } = -1;
        public int Top { get; set; } = -1;
        public bool IsMaximized { get; set; } = false;
    }
}
