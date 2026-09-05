using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class VoiceApiModule
{
    private readonly VoiceCallHandler _voiceHandler;
    private readonly WebUiWebSocketHub _hub;

    public ConcurrentDictionary<string, ConcurrentQueue<byte[]>> IncomingAudioQueues { get; } = new();
    public ConcurrentQueue<(string PeerId, string SignalType)> CallSignals { get; } = new();

    public VoiceApiModule(VoiceCallHandler voiceHandler, WebUiWebSocketHub hub)
    {
        _voiceHandler = voiceHandler;
        _hub = hub;

        _voiceHandler.OnAudioDatagramReceived += (peer, data) =>
        {
            string peerId = peer.Id.ToString();
            var queue = IncomingAudioQueues.GetOrAdd(peerId, _ => new ConcurrentQueue<byte[]>());
            queue.Enqueue(data);
            while (queue.Count > 120) queue.TryDequeue(out _);
            _hub.BroadcastBinaryAudio(peer.Id, data);
        };

        _voiceHandler.OnCallEstablished += peer =>
        {
            WebUiServer.ClearPeerPetitions(peer.Id);
            CallSignals.Enqueue((peer.Id.ToString(), "call-established"));
            WebUiServer.LogEvent($"[VOICE] Call established with {peer.Name}");
            _hub.Broadcast("voice_signal", new { peerId = peer.Id.ToString(), signal = "call-established" });
        };

        _voiceHandler.OnCallEnded += peer =>
        {
            string peerId = peer.Id.ToString();
            IncomingAudioQueues.TryRemove(peerId, out _);
            CallSignals.Enqueue((peerId, "call-ended"));
            WebUiServer.LogEvent($"[VOICE] Call ended with {peer.Name}");
            _hub.Broadcast("voice_signal", new { peerId, signal = "call-ended" });
        };
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/voice-send" && req.HttpMethod == "POST")
        {
            byte[] audio = await WebUiContext.ReadRequestBytesAsync(req, WebUiContext.MaxVoiceBodyBytes).ConfigureAwait(false);
            string peerIdsHeader = req.Headers["X-Peer-Ids"] ?? req.Headers["X-Peer-Id"] ?? "";
            if (peerIdsHeader == "all" || string.IsNullOrWhiteSpace(peerIdsHeader))
            {
                await VoiceCallHandler.BroadcastAudioDatagramAsync(audio).ConfigureAwait(false);
            }
            else
            {
                Guid[] peerIds = peerIdsHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => Guid.TryParse(value, out Guid id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .Take(32)
                    .ToArray();
                await Task.WhenAll(peerIds.Select(id => VoiceCallHandler.SendAudioDatagramAsync(id, audio))).ConfigureAwait(false);
            }
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice-poll" && req.HttpMethod == "GET")
        {
            var signals = new List<object>();
            while (CallSignals.TryDequeue(out var signal)) signals.Add(new { peerId = signal.PeerId, signal = signal.SignalType });
            var chunks = new List<object>();
            foreach (var pair in IncomingAudioQueues.ToArray())
            {
                int count = 0;
                while (count++ < 32 && pair.Value.TryDequeue(out var data))
                    chunks.Add(new { peerId = pair.Key, data = Convert.ToBase64String(data) });
            }
            await WebUiContext.WriteJsonAsync(resp, new { signals, chunks }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice-hangup" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            if (VoiceCallHandler.ActiveCalls.TryRemove(peerId, out var call))
            {
                try { call.Stream.Close(); } catch { }
                try { await call.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                call.Dispose();
                IncomingAudioQueues.TryRemove(peerId.ToString(), out _);
                CallSignals.Enqueue((peerId.ToString(), "call-ended"));
                _hub.Broadcast("voice_signal", new { peerId = peerId.ToString(), signal = "call-ended" });
            }
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
