using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class VoiceApiModule
{
    private readonly VoiceCallHandler _voiceHandler;
    private readonly WebUiWebSocketHub _hub;
    private QuicPunchTests.Services.NativeScreenCaptureService? _nativeCapture;

    private long _lastLocalMeterTick;
    private readonly ConcurrentDictionary<Guid, long> _lastPeerMeterTicks = new();

    public ConcurrentQueue<(string PeerId, string SignalType)> CallSignals { get; } = new();

    public VoiceApiModule(VoiceCallHandler voiceHandler, WebUiWebSocketHub hub)
    {
        _voiceHandler = voiceHandler;
        _hub = hub;

        _voiceHandler.OnCallEstablished += peer =>
        {
            WebUiServer.ClearPeerPetitions(peer.Id);
            CallSignals.Enqueue((peer.Id.ToString(), "call-established"));
            WebUiServer.LogEvent($"[VOICE] Call established with {peer.Name}");
            _hub.Broadcast("voice_signal", new { peerId = peer.Id.ToString(), signal = "call-established" });
            BroadcastVoiceState();
            WebUiServer.BroadcastStatusUpdate();
        };

        _voiceHandler.OnCallEnded += peer =>
        {
            string peerId = peer.Id.ToString();
            CallSignals.Enqueue((peerId, "call-ended"));
            WebUiServer.LogEvent($"[VOICE] Call ended with {peer.Name}");
            _hub.Broadcast("voice_signal", new { peerId, signal = "call-ended" });
            BroadcastVoiceState();
            WebUiServer.BroadcastStatusUpdate();
        };

        _voiceHandler.AudioService.OnLocalAudioLevel += level =>
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastLocalMeterTick, now).TotalMilliseconds >= 75)
            {
                _lastLocalMeterTick = now;
                _hub.Broadcast("voice_vad", new { isLocal = true, level, speaking = level > 0.08f });
            }
        };

        _voiceHandler.AudioService.OnPeerAudioLevel += (peerId, level) =>
        {
            long now = Stopwatch.GetTimestamp();
            long last = _lastPeerMeterTicks.TryGetValue(peerId, out var tick) ? tick : 0;
            if (last == 0 || Stopwatch.GetElapsedTime(last, now).TotalMilliseconds >= 75)
            {
                _lastPeerMeterTicks[peerId] = now;
                _hub.Broadcast("voice_vad", new { isLocal = false, peerId = peerId.ToString(), level, speaking = level > 0.08f });
            }
        };

        _voiceHandler.OnScreenShareStarted += (peer, width, height, codecId, fps) =>
        {
            WebUiServer.LogEvent($"[VOICE] {peer.Name} started screen sharing ({width}x{height}, codec: {codecId}, {fps} fps)");
            _hub.Broadcast("screen_share_started", new { peerId = peer.Id.ToString(), peerName = peer.Name, width, height, codecId, fps });
        };

        _voiceHandler.OnScreenShareStopped += peer =>
        {
            WebUiServer.LogEvent($"[VOICE] {peer.Name} stopped screen sharing");
            _hub.Broadcast("screen_share_stopped", new { peerId = peer.Id.ToString(), peerName = peer.Name });
        };

        _voiceHandler.OnScreenFrameReceived += (peer, codecId, frameData) =>
        {
            // Binary video chunk forwarding directly to UI clients: Tag 0x02, PeerId, CodecId, FrameData
            _hub.BroadcastBinaryVideo(peer.Id, codecId, frameData);
        };

        _voiceHandler.OnScreenAudioReceived += (peer, audioData) =>
        {
            // Binary screen audio chunk forwarding directly to UI clients: Tag 0x04, PeerId, AudioData
            _hub.BroadcastBinaryScreenAudio(peer.Id, audioData);
        };
    }

    private void BroadcastVoiceState()
    {
        var audio = _voiceHandler.AudioService;
        var activeCalls = VoiceCallHandler.ActiveCalls.Values.Select(c => new
        {
            peerId = c.Peer.Id.ToString(),
            peerName = c.Peer.Name ?? "Peer",
            volume = audio.GetPeerVolume(c.Peer.Id),
            muted = audio.GetPeerMuted(c.Peer.Id),
            outboundMuted = c.IsOutboundMuted,
            screenShareAllowed = c.IsScreenShareAllowed
        }).ToList();

        _hub.Broadcast("voice_state", new
        {
            isMuted = audio.IsMuted,
            isDeafened = audio.IsDeafened,
            masterVolume = audio.MasterVolume,
            isTestLoopback = audio.IsTestLoopback,
            currentInputDevice = audio.CurrentInputDeviceId ?? "default",
            currentOutputDevice = audio.CurrentOutputDeviceId ?? "default",
            backendName = audio.BackendName,
            opusBitrate = _voiceHandler.Codec.Bitrate,
            isScreenSharing = VoiceCallHandler.IsLocalScreenSharing || (_nativeCapture?.IsRunning == true),
            isNativeScreenSharing = _nativeCapture?.IsRunning == true,
            activeCalls
        });
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        var audio = _voiceHandler.AudioService;

        if (path == "/api/voice/state" && req.HttpMethod == "GET")
        {
            var activeCalls = VoiceCallHandler.ActiveCalls.Values.Select(c => new
            {
                peerId = c.Peer.Id.ToString(),
                peerName = c.Peer.Name ?? "Peer",
                volume = audio.GetPeerVolume(c.Peer.Id),
                muted = audio.GetPeerMuted(c.Peer.Id),
                outboundMuted = c.IsOutboundMuted,
                isRemoteScreenSharing = c.IsRemoteScreenSharing
            }).ToList();

            await WebUiContext.WriteJsonAsync(resp, new
            {
                isMuted = audio.IsMuted,
                isDeafened = audio.IsDeafened,
                masterVolume = audio.MasterVolume,
                isTestLoopback = audio.IsTestLoopback,
                currentInputDevice = audio.CurrentInputDeviceId ?? "default",
                currentOutputDevice = audio.CurrentOutputDeviceId ?? "default",
                backendName = audio.BackendName,
                opusBitrate = _voiceHandler.Codec.Bitrate,
                isScreenSharing = VoiceCallHandler.IsLocalScreenSharing || (_nativeCapture?.IsRunning == true),
                isNativeScreenSharing = _nativeCapture?.IsRunning == true,
                activeCalls
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/devices" && req.HttpMethod == "GET")
        {
            var inputs = audio.GetInputDevices().Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }).ToList();
            var outputs = audio.GetOutputDevices().Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }).ToList();

            await WebUiContext.WriteJsonAsync(resp, new
            {
                inputs,
                outputs,
                currentInput = audio.CurrentInputDeviceId ?? "default",
                currentOutput = audio.CurrentOutputDeviceId ?? "default"
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/gpu-codecs" && req.HttpMethod == "GET")
        {
            var encoders = new List<object>
            {
                new { id = "h264", name = "H.264 / AVC (NVENC / AMF / QSV)", gpuAccelerated = true, maxFps = 144, recommendedBitrate = "8-20 Mbps", description = "Maximum GPU acceleration (NVIDIA NVENC, AMD AMF, Intel QSV). Ideal for 120/144 FPS." },
                new { id = "hevc", name = "H.265 / HEVC (NVENC / AMF)", gpuAccelerated = true, maxFps = 144, recommendedBitrate = "6-15 Mbps", description = "Modern compression ~40% more efficient than H.264 at high FPS with GPU support." },
                new { id = "vp9", name = "VP9 (GPU Accelerated / Chromium)", gpuAccelerated = true, maxFps = 120, recommendedBitrate = "6-16 Mbps", description = "Crisp text and UI rendering, high resolution 1440p/4K and vector graphics." },
                new { id = "av1", name = "AV1 (NVENC Ada/Blackwell / AMF / CPU)", gpuAccelerated = true, maxFps = 120, recommendedBitrate = "4-12 Mbps", description = "State-of-the-art open royalty-free next-generation video codec." },
                new { id = "jpeg", name = "JPEG Raw Frames (Compatibility Fallback)", gpuAccelerated = false, maxFps = 30, recommendedBitrate = "2-5 Mbps", description = "Emergency frame-by-frame snapshot mode without inter-frame temporal compression." }
            };

            await WebUiContext.WriteJsonAsync(resp, new
            {
                gpuName = "NVIDIA GeForce RTX (NVENC)",
                hardwareSupported = true,
                maxSupportedFps = 144,
                maxSupportedBitrate = 30000000,
                encoders
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/device" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string type = doc.RootElement.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";
            string deviceId = doc.RootElement.TryGetProperty("deviceId", out var idEl) ? idEl.GetString() ?? "default" : "default";

            if (type.Equals("input", StringComparison.OrdinalIgnoreCase))
            {
                audio.SetInputDevice(deviceId);
            }
            else if (type.Equals("output", StringComparison.OrdinalIgnoreCase))
            {
                audio.SetOutputDevice(deviceId);
            }

            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, type, deviceId }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/mute" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool muted = doc.RootElement.TryGetProperty("muted", out var mutedEl) ? mutedEl.GetBoolean() : !audio.IsMuted;
            audio.IsMuted = muted;
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, isMuted = audio.IsMuted }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/deafen" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool deafened = doc.RootElement.TryGetProperty("deafened", out var deafenedEl) ? deafenedEl.GetBoolean() : !audio.IsDeafened;
            audio.IsDeafened = deafened;
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, isDeafened = audio.IsDeafened }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/volume" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("masterVolume", out var masterEl))
            {
                audio.MasterVolume = (float)masterEl.GetDouble();
            }

            if (doc.RootElement.TryGetProperty("peerId", out var peerIdEl))
            {
                if (Guid.TryParse(peerIdEl.GetString(), out Guid pId))
                {
                    if (doc.RootElement.TryGetProperty("volume", out var volEl))
                    {
                        audio.SetPeerVolume(pId, (float)volEl.GetDouble());
                    }
                    if (doc.RootElement.TryGetProperty("muted", out var pMutedEl))
                    {
                        audio.SetPeerMuted(pId, pMutedEl.GetBoolean());
                    }
                    if (doc.RootElement.TryGetProperty("outboundMuted", out var outMutedEl))
                    {
                        if (VoiceCallHandler.ActiveCalls.TryGetValue(pId, out var session))
                        {
                            session.IsOutboundMuted = outMutedEl.GetBoolean();
                        }
                    }
                }
            }

            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, masterVolume = audio.MasterVolume }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/test-mic" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            bool enabled = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("enabled", out var enEl)
                ? enEl.GetBoolean()
                : !audio.IsTestLoopback;

            audio.IsTestLoopback = enabled;
            if (enabled)
            {
                audio.Start();
            }
            else if (VoiceCallHandler.ActiveCalls.IsEmpty)
            {
                audio.Stop();
            }

            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, isTestLoopback = audio.IsTestLoopback }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice-hangup" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = doc.RootElement.TryGetProperty("peerId", out var idEl) && Guid.TryParse(idEl.GetString(), out var id)
                ? id
                : Guid.Empty;

            bool hungUp = peerId != Guid.Empty && _voiceHandler.Hangup(peerId);
            BroadcastVoiceState();
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, hungUp }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/codec-settings" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("bitrate", out var brEl))
            {
                int newBitrate = brEl.GetInt32();
                if (newBitrate is >= 8000 and <= 256000)
                {
                    _voiceHandler.Codec.Bitrate = newBitrate;
                    QuicPunch.QuicPunchLog.Info($"[VOICE] Opus codec bitrate set to {newBitrate} bps via WebUI.");
                }
            }

            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new
            {
                success = true,
                bitrate = _voiceHandler.Codec.Bitrate
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/start" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            int width = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 1280;
            int height = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 720;
            byte codecId = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("codecId", out var cEl) ? cEl.GetByte() : (byte)1;
            byte fps = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("fps", out var fpsEl) ? fpsEl.GetByte() : (byte)30;

            await VoiceCallHandler.StartScreenShareAsync(width, height, codecId, fps).ConfigureAwait(false);
            BroadcastVoiceState();
            _hub.Broadcast("screen_share_started", new { peerId = "local", peerName = "You", width, height, codecId, fps });
            await WebUiContext.WriteJsonAsync(resp, new { success = true, isSharing = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/stop" && req.HttpMethod == "POST")
        {
            if (_nativeCapture?.IsRunning == true)
            {
                _nativeCapture.Stop();
            }

            await VoiceCallHandler.StopScreenShareAsync().ConfigureAwait(false);
            BroadcastVoiceState();
            _hub.Broadcast("screen_share_stopped", new { peerId = "local", peerName = "You" });
            await WebUiContext.WriteJsonAsync(resp, new { success = true, isSharing = false }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/peer-toggle" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool allowed = doc.RootElement.TryGetProperty("allowed", out var aEl) && aEl.GetBoolean();
            VoiceCallHandler.SetPeerScreenShareAllowed(peerId, allowed);
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, peerId = peerId.ToString(), allowed }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/frame" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("frame", out var frameEl))
            {
                string? frameStr = frameEl.GetString();
                if (!string.IsNullOrEmpty(frameStr))
                {
                    int commaIdx = frameStr.IndexOf(',');
                    if (commaIdx >= 0)
                    {
                        frameStr = frameStr[(commaIdx + 1)..];
                    }
                    byte[] jpegBytes = Convert.FromBase64String(frameStr);
                    await VoiceCallHandler.SendScreenFrameAsync(jpegBytes).ConfigureAwait(false);
                }
            }
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/native-toggle" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            bool enable = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("enable", out var enEl)
                ? enEl.GetBoolean()
                : !(_nativeCapture?.IsRunning == true);

            if (_nativeCapture == null)
            {
                _nativeCapture = new QuicPunchTests.Services.NativeScreenCaptureService(async frameData =>
                {
                    await VoiceCallHandler.SendScreenFrameAsync(frameData).ConfigureAwait(false);
                    // Also forward to local preview
                    string base64 = Convert.ToBase64String(frameData);
                    _hub.Broadcast("screen_frame", new { peerId = "local", dataUrl = "data:image/jpeg;base64," + base64 });
                });
            }

            if (enable)
            {
                int targetFps = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("fps", out var fpsEl) ? fpsEl.GetInt32() : 60;
                int maxW = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 1920;
                int maxH = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 1080;

                await VoiceCallHandler.StartScreenShareAsync(maxW, maxH, codecId: 0, fps: (byte)Math.Clamp(targetFps, 1, 120)).ConfigureAwait(false);
                _nativeCapture.Start(targetFps: targetFps, maxWidth: maxW, maxHeight: maxH, jpegQuality: targetFps >= 60 ? 60L : 70L);
                _hub.Broadcast("screen_share_started", new { peerId = "local", peerName = "You", width = maxW, height = maxH, fps = targetFps });
            }
            else
            {
                _nativeCapture.Stop();
                await VoiceCallHandler.StopScreenShareAsync().ConfigureAwait(false);
                _hub.Broadcast("screen_share_stopped", new { peerId = "local", peerName = "You" });
            }

            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new { success = true, isNativeRunning = _nativeCapture.IsRunning }).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
