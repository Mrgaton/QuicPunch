using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Photino.NET;
using QuicPunch;

namespace QuicPunchTests
{
    internal class WebUiServer
    {
        private readonly QuicPunch.QuicPunch _qcc;
        private readonly ChatHandler _chatHandler;
        private readonly VirtualLanHandler _lanHandler;
        private readonly VoiceCallHandler _voiceHandler;
        private readonly CancellationTokenSource _cts;
        private HttpListener _listener;
        private int _port;

        public static ConcurrentQueue<string> EventLogs { get; } = new();
        public static ConcurrentQueue<ChatMessage> ChatMessages { get; } = new();
        public static ConcurrentQueue<(string PeerId, byte[] Data)> IncomingAudioQueue { get; } = new();
        public static ConcurrentQueue<(string PeerId, string SignalType)> CallSignals { get; } = new();

        public record ChatMessage(string PeerId, string MsgId, string Sender, string Message, DateTime Timestamp, bool IsMe, bool IsConfirmed);
        public record PendingPetitionItem(Guid RequestId, Guid ProtocolId, string ProtocolName, string PeerName, Guid PeerId, TaskCompletionSource<HandshakeDecision> Tcs);
        public static ConcurrentDictionary<Guid, PendingPetitionItem> PendingPetitions { get; } = new();

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
                ChatMessages.Enqueue(new ChatMessage(peer.Id.ToString(), msgId, peer.Name, msg, DateTime.Now, false, true));
                LogEvent($"[CHAT] Message from {peer.Name}: {msg}");
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

            _qcc.Manager.HandshakeRequested += (request, ct) =>
            {
                string protoName = _qcc.ProtocolHandlers.TryGetValue(request.ProtocolId, out var h) ? h.ProtocolName : "Connection";
                var matchedPeer = _qcc.AvailablePeers.Values.FirstOrDefault(p => p.ActiveEndPoint?.Equals(request.RemoteEndPoint) == true || (p.Addresses != null && p.Addresses.Any(a => a.Equals(request.RemoteEndPoint.Address))));
                string peerName = matchedPeer?.Name ?? request.RemoteEndPoint.ToString();
                Guid peerId = matchedPeer?.Id ?? Guid.Empty;

                // Deduplicate: If an existing petition for this peer & protocol is pending, replace it
                var existingKey = PendingPetitions.FirstOrDefault(kv => kv.Value.PeerName == peerName && kv.Value.ProtocolId == request.ProtocolId).Key;
                if (existingKey != default)
                {
                    if (PendingPetitions.TryRemove(existingKey, out var oldPet))
                    {
                        try { oldPet.Tcs.TrySetResult(new HandshakeDecision(false, null, CancellationToken.None)); } catch { }
                    }
                }

                var tcs = new TaskCompletionSource<HandshakeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                var petition = new PendingPetitionItem(request.Id, request.ProtocolId, protoName, peerName, peerId, tcs);
                PendingPetitions[request.Id] = petition;
                LogEvent($"[PETITION] Connection request from {peerName} ({protoName})");

                return tcs.Task;
            };

            _voiceHandler.OnAudioDatagramReceived += (peer, audioBytes) =>
            {
                IncomingAudioQueue.Enqueue((peer.Id.ToString(), audioBytes));
                while (IncomingAudioQueue.Count > 300) IncomingAudioQueue.TryDequeue(out _);
            };

            _voiceHandler.OnCallEstablished += (peer) =>
            {
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
                              .SetSize(1280, 820)
                              .Center()
                              .SetUseOsDefaultSize(false)
                              .Load(url);

                        window.WaitForClose();
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
            while (!_cts.Token.IsCancellationRequested && _listener.IsListening)
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

                var path = req.Url.AbsolutePath.ToLowerInvariant();

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
                            LogEvent($"[VOICE] Ended voice call with {call.Peer.Name}");
                        }
                    }
                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/saved-peer-delete" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string certHashB64 = doc.RootElement.GetProperty("certHash").GetString() ?? "";
                    if (!string.IsNullOrEmpty(certHashB64))
                    {
                        byte[] certHash = Convert.FromBase64String(certHashB64);
                        _qcc.PeerStore.Remove(certHash);
                        LogEvent("[DB] Removed saved peer from peers.db");
                    }
                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/saved-peer-update" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string certHashB64 = doc.RootElement.GetProperty("certHash").GetString() ?? "";
                    int minPort = doc.RootElement.TryGetProperty("minPort", out var mnEl) ? mnEl.GetInt32() : 1024;
                    int maxPort = doc.RootElement.TryGetProperty("maxPort", out var mxEl) ? mxEl.GetInt32() : 65535;
                    minPort = Math.Clamp(minPort, 1, 65535);
                    maxPort = Math.Clamp(maxPort, minPort, 65535);

                    string addrsRaw = doc.RootElement.TryGetProperty("addresses", out var adEl) ? adEl.GetString() ?? "" : "";
                    
                    var ips = new List<IPAddress>();
                    foreach (var raw in addrsRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (IPAddress.TryParse(raw.Trim(), out var ip))
                        {
                            ips.Add(ip);
                        }
                    }

                    if (!string.IsNullOrEmpty(certHashB64) && ips.Count > 0)
                    {
                        byte[] certHash = Convert.FromBase64String(certHashB64);
                        _qcc.PeerStore.AddOrUpdate(ips, minPort, maxPort, certHash);
                        LogEvent($"[DB] Updated saved peer in peers.db ({minPort}-{maxPort}) with {ips.Count} valid IP(s)");
                    }

                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/voice-poll")
                {
                    var chunks = new List<string>();
                    while (IncomingAudioQueue.TryDequeue(out var item))
                    {
                        chunks.Add($"{{\"peerId\":\"{HttpUtility.JavaScriptStringEncode(item.PeerId)}\",\"data\":\"{Convert.ToBase64String(item.Data)}\"}}");
                    }
                    var signals = new List<string>();
                    while (CallSignals.TryDequeue(out var sig))
                    {
                        signals.Add($"{{\"peerId\":\"{HttpUtility.JavaScriptStringEncode(sig.PeerId)}\",\"signal\":\"{HttpUtility.JavaScriptStringEncode(sig.SignalType)}\"}}");
                    }
                    string json = $"{{\"chunks\":[{string.Join(",", chunks)}],\"signals\":[{string.Join(",", signals)}]}}";
                    byte[] respBytes = Encoding.UTF8.GetBytes(json);
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/connect-token" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string token = doc.RootElement.GetProperty("token").GetString() ?? "";

                    LogEvent($"Connecting via token: {token[..Math.Min(20, token.Length)]}...");
                    _ = _qcc.PeerInterrogation(token, _cts);

                    byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                    resp.ContentType = "application/json";
                    resp.ContentLength64 = respBytes.Length;
                    await resp.OutputStream.WriteAsync(respBytes);
                }
                else if (path == "/api/connect-peer" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string peerIdStr = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                    string protocol = doc.RootElement.GetProperty("protocol").GetString() ?? "chat";

                    if (Guid.TryParse(peerIdStr, out var pid) && _qcc.AvailablePeers.TryGetValue(pid, out var peer))
                    {
                        Guid protoId = _chatHandler.ProtocolId;
                        if (Guid.TryParse(protocol, out var parsedProtoId))
                        {
                            protoId = parsedProtoId;
                        }
                        else if (protocol.ToLower() == "lan")
                        {
                            protoId = _lanHandler.ProtocolId;
                        }

                        if (_qcc.ProtocolHandlers.TryGetValue(protoId, out var handler))
                        {
                            LogEvent($"Initiating {handler.ProtocolName} connection with {peer.Name} ({peer.Id})...");
                            ushort localPort = (ushort)Random.Shared.Next(1024, 65535);
                            _ = Task.Run(async () => await _qcc.InitQuicConnection(protoId, peer, localPort, _cts));

                            byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                            resp.ContentType = "application/json";
                            resp.ContentLength64 = respBytes.Length;
                            await resp.OutputStream.WriteAsync(respBytes);
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
                            LogEvent($"[CHAT OUT] Sent to {pid}: {message}");

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
                else if (path == "/api/accept-petition" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string reqIdStr = doc.RootElement.GetProperty("requestId").GetString() ?? "";

                    if (Guid.TryParse(reqIdStr, out var reqId) && PendingPetitions.TryRemove(reqId, out var item))
                    {
                        ushort assignedPort = (ushort)Random.Shared.Next(1024, 65535);
                        item.Tcs.TrySetResult(new HandshakeDecision(true, assignedPort, CancellationToken.None));
                        LogEvent($"[PETITION] Accepted connection request from {item.PeerName}");

                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
                    }
                }
                else if (path == "/api/decline-petition" && req.HttpMethod == "POST")
                {
                    using var r = new StreamReader(req.InputStream, req.ContentEncoding);
                    string body = await r.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string reqIdStr = doc.RootElement.GetProperty("requestId").GetString() ?? "";

                    if (Guid.TryParse(reqIdStr, out var reqId) && PendingPetitions.TryRemove(reqId, out var item))
                    {
                        item.Tcs.TrySetResult(new HandshakeDecision(false, null, CancellationToken.None));
                        LogEvent($"[PETITION] Declined connection request from {item.PeerName}");

                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\":true}");
                        resp.ContentType = "application/json";
                        resp.ContentLength64 = respBytes.Length;
                        await resp.OutputStream.WriteAsync(respBytes);
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
                string myToken = _qcc.GetToken() ?? "";
                string quickUri = $"https://gato.ovh/protred?uri=QPHP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(myToken))}";
                string nodeName = _qcc.CurrentPeer?.Name ?? "LocalNode";
                string nodeId = _qcc.CurrentPeer?.Id.ToString() ?? "";
                int minPort = _qcc.CurrentPeer?.MinPort ?? 0;
                int maxPort = _qcc.CurrentPeer?.MaxPort ?? 0;
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
                    var host = Dns.GetHostEntry(Dns.GetHostName());
                    foreach (var ip in host.AddressList)
                    {
                        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        {
                            string s = ip.ToString();
                            if (!allAddrs.Contains(s)) allAddrs.Add(s);
                        }
                    }
                }
                catch { }

                var availablePeersList = _qcc.AvailablePeers.Values.Select(p => new
                {
                    id = p.Id.ToString(),
                    name = p.Name ?? "",
                    ping = p.Ping.HasValue ? (int)p.Ping.Value.TotalMilliseconds : 0,
                    activeEndPoint = p.ActiveEndPoint?.ToString() ?? "Unknown",
                    minPort = p.MinPort,
                    maxPort = p.MaxPort,
                    addresses = p.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                    lastSeen = p.LastSeen.ToString("HH:mm:ss")
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

                var savedPeersList = (_qcc.PeerStore?.SavedPeers?.ToList() ?? new List<PeerStore.SavedPeer>()).Select(sp => new
                {
                    certHash = Convert.ToBase64String(sp.CertHash),
                    minPort = sp.MinPort,
                    maxPort = sp.MaxPort,
                    addresses = sp.Addresses?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>()
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

                var logsList = EventLogs.TakeLast(50).ToList();

                var statusObj = new
                {
                    nodeName,
                    nodeId,
                    minPort,
                    maxPort,
                    networkType,
                    token = myToken,
                    quickUri,
                    publicEndpoints = allAddrs,
                    availablePeers = availablePeersList,
                    savedPeers = savedPeersList,
                    registeredProtocols,
                    activeChats = activeChatsList,
                    activeVoiceCalls = activeVoiceList,
                    chatMessages = msgsList,
                    pendingPetitions = pendingPetitionsList,
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
}
