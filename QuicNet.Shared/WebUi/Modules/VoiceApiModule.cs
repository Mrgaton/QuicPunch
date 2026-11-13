using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using QuicNet.Protocols;
using QuicNet.Video;
using QuicPunch.Helpers;

namespace QuicNet.WebUi.Modules;

internal sealed class VoiceApiModule
{
    private readonly VoiceCallHandler _voiceHandler;
    private readonly WebUiWebSocketHub _hub;

    private long _lastLocalMeterTick;
    private long _platformGeneration;
    private readonly ConcurrentDictionary<Guid, long> _lastPeerMeterTicks = new();

    public ConcurrentQueue<(string PeerId, string SignalType)> CallSignals { get; } = new();

    public VoiceApiModule(VoiceCallHandler voiceHandler, WebUiWebSocketHub hub)
    {
        _voiceHandler = voiceHandler;
        _hub = hub;
        _voiceHandler.AudioService.OnDeviceStateChanged += BroadcastVoiceState;
        _voiceHandler.SystemAudioCaptureFailed += (shareId, message) => _hub.Broadcast("screen_audio_error", new global::QuicNet.Serialization.VoiceApiModuleMessage21(message) { audioSessionId = shareId });
        _voiceHandler.OnBrowserScreenSettingsRequested += (peerId, requestId, settings) =>
            _hub.Broadcast("screen_settings_request", new global::QuicNet.Serialization.VoiceApiModuleMessage1(peerId, requestId, settings));
        _voiceHandler.OnLocalNativeScreenFrame += (codec, packet) => { NativeVideoRuntime.Instance.Enqueue(Guid.Empty, codec, packet); return Task.CompletedTask; };
        NativeVideoRuntime.Instance.CapabilitiesChanged += capabilities => { _ = _voiceHandler.PublishNativeVideoCapabilitiesAsync(); _hub.Broadcast("native_video_capabilities", capabilities); };
        NativeVideoRuntime.Instance.StateChanged += state => _hub.Broadcast("native_video_state", state);
        _voiceHandler.OnLocalScreenControlChanged += () =>
        {
            if (_voiceHandler.ScreenEncoder.Profile is { } profile)
                _hub.Broadcast("screen_share_started", new global::QuicNet.Serialization.VoiceApiModuleMessage2("local", "You", profile.Width, profile.Height, profile.CodecId, profile.Fps));
            else if (!VoiceCallHandler.IsLocalScreenSharing)
                _hub.Broadcast("screen_share_stopped", new global::QuicNet.Serialization.VoiceApiModuleMessage3("local", "You"));
            BroadcastVoiceState();
        };
        _voiceHandler.OnScreenDecoderProbe += (peer, config) => { _ = _voiceHandler.ReplyNativeDecoderProbeAsync(peer, config); };
        _voiceHandler.OnScreenDecoderDiagnostic += (peer, config) => _hub.Broadcast("screen_decoder_diagnostic", new global::QuicNet.Serialization.VoiceApiModuleMessage4(peer.Id, config));
        _voiceHandler.ScreenEncoder.Failed += reason =>
        {
            _ = HandleEncoderFailureAsync(reason);
        };

        _voiceHandler.OnCallEstablished += peer =>
        {
            WebUiServer.ClearPeerPetitions(peer.Id);
            CallSignals.Enqueue((peer.Id.ToString(), "call-established"));
            WebUiServer.LogEvent($"[VOICE] Call established with {peer.Name}");
            _hub.Broadcast("voice_signal", new global::QuicNet.Serialization.VoiceApiModuleMessage5(peer.Id.ToString(), "call-established"));
            BroadcastVoiceState();
            WebUiServer.BroadcastStatusUpdate();
        };

        _voiceHandler.OnCallEnded += peer =>
        {
            _ = NativeVideoRuntime.Instance.StopAsync(peer.Id.ToString());
            if (!VoiceCallHandler.IsLocalScreenSharing)
                _hub.Broadcast("screen_share_stopped", new global::QuicNet.Serialization.VoiceApiModuleMessage3("local", "You"));
            string peerId = peer.Id.ToString();
            CallSignals.Enqueue((peerId, "call-ended"));
            WebUiServer.LogEvent($"[VOICE] Call ended with {peer.Name}");
            _hub.Broadcast("voice_signal", new global::QuicNet.Serialization.VoiceApiModuleMessage5(peerId, "call-ended"));
            BroadcastVoiceState();
            WebUiServer.BroadcastStatusUpdate();
        };

        _voiceHandler.AudioService.OnLocalAudioLevel += level =>
        {
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastLocalMeterTick, now).TotalMilliseconds >= 75)
            {
                _lastLocalMeterTick = now;
                _hub.Broadcast("voice_vad", new global::QuicNet.Serialization.VoiceApiModuleMessage6(true, level, level > 0.08f));
            }
        };

        _voiceHandler.AudioService.OnPeerAudioLevel += (peerId, level) =>
        {
            long now = Stopwatch.GetTimestamp();
            long last = _lastPeerMeterTicks.TryGetValue(peerId, out var tick) ? tick : 0;
            if (last == 0 || Stopwatch.GetElapsedTime(last, now).TotalMilliseconds >= 75)
            {
                _lastPeerMeterTicks[peerId] = now;
                _hub.Broadcast("voice_vad", new global::QuicNet.Serialization.VoiceApiModuleMessage7(false, peerId.ToString(), level, level > 0.08f));
            }
        };

        _voiceHandler.OnScreenShareStarted += (peer, width, height, codecId, fps) =>
        {
            var kind = VoiceCallHandler.ActiveCalls.TryGetValue(peer.Id, out var session) ? session.RemoteVideoKind : "screen";
            WebUiServer.Instance?.Notifications?.Publish("screen", peer.Name ?? "Vídeo", kind == "camera" ? "Ha activado su cámara." : "Ha empezado a compartir su pantalla.", "/call.html", Convert.ToHexString(peer.CertHash));
            WebUiServer.LogEvent($"[VOICE] {peer.Name} started {kind} video ({width}x{height}, codec: {codecId}, {fps} fps)");
            _hub.Broadcast("screen_share_started", new global::QuicNet.Serialization.VoiceApiModuleMessage8(peer.Id.ToString(), peer.Name, width, height, codecId, fps, kind));
        };

        _voiceHandler.OnScreenShareStopped += peer =>
        {
            _ = NativeVideoRuntime.Instance.StopAsync(peer.Id.ToString());
            WebUiServer.LogEvent($"[VOICE] {peer.Name} stopped screen sharing");
            _hub.Broadcast("screen_share_stopped", new global::QuicNet.Serialization.VoiceApiModuleMessage3(peer.Id.ToString(), peer.Name));
        };
        _voiceHandler.OnScreenShareRequested += peer =>
        {
            WebUiServer.LogEvent($"[VOICE] {peer.Name} requested local screen sharing with prior permission.");
            _hub.Broadcast("screen_share_request", new global::QuicNet.Serialization.VoiceApiModuleMessage3(peer.Id.ToString(), peer.Name));
        };
        _voiceHandler.OnScreenShareRequestDenied += peer =>
            _hub.Broadcast("screen_share_request_denied", new global::QuicNet.Serialization.VoiceApiModuleMessage3(peer.Id.ToString(), peer.Name));

        _voiceHandler.OnScreenFrameReceivedAsync += (peer, codecId, frameData) =>
        {
            NativeVideoRuntime.Instance.Enqueue(peer.Id, codecId, frameData);
            return Task.CompletedTask;
        };

        _voiceHandler.OnScreenAudioReceived += (peer, audioData) =>
        {
            // Binary screen audio chunk forwarding directly to UI clients: Tag 0x04, PeerId, AudioData
            _hub.BroadcastBinaryScreenAudio(peer.Id, audioData);
        };

        _voiceHandler.OnScreenCodecCapabilitiesReceived += (peer, encoderCodecMask, decoderCodecMask) =>
        {
            _hub.Broadcast("screen_codec_capabilities", new global::QuicNet.Serialization.VoiceApiModuleMessage9(peer.Id.ToString(), peer.Name, encoderCodecMask, decoderCodecMask) { decoderHardwarePreferenceMask = VoiceCallHandler.ActiveCalls.TryGetValue(peer.Id, out var session) ? session.RemoteScreenDecoderHardwarePreferenceMask : null, capabilitiesReceived = session?.RemoteScreenCapabilitiesReceived == true });
            BroadcastVoiceState();
        };
    }

    private void BroadcastVoiceState()
    {
        var audio = _voiceHandler.AudioService;
        var activeCalls = VoiceCallHandler.ActiveCalls.Values.Select(c => new global::QuicNet.Serialization.VoiceApiModuleMessage10(c.Peer.Id.ToString(), c.Peer.Name ?? "Peer", audio.GetPeerVolume(c.Peer.Id), audio.GetPeerMuted(c.Peer.Id), c.IsOutboundMuted, c.IsScreenShareAllowed, c.RemoteScreenEncoderCodecMask, c.RemoteScreenDecoderCodecMask) { startedAtUnixMs = new DateTimeOffset(c.StartedAtUtc).ToUnixTimeMilliseconds(), screenDecoderHardwarePreferenceMask = c.RemoteScreenDecoderHardwarePreferenceMask, screenCapabilitiesReceived = c.RemoteScreenCapabilitiesReceived }).ToList();

        _hub.Broadcast("voice_state", new global::QuicNet.Serialization.VoiceApiModuleMessage11(audio.IsMuted, audio.IsDeafened, audio.MasterVolume, audio.IsTestLoopback, audio.CurrentInputDeviceId ?? "default", audio.CurrentOutputDeviceId ?? "default", audio.BackendName, audio.IsCaptureRunning && audio.CaptureError == null, audio.IsPlaybackRunning && audio.PlaybackError == null, audio.CaptureError, audio.PlaybackError, _voiceHandler.AudioService.Bitrate, _voiceHandler.RequestedBitrate, VoiceCallHandler.IsLocalScreenSharing, _voiceHandler.ScreenEncoder.Profile != null, VoiceCallHandler.LocalScreenTargetPeerId, _voiceHandler.ScreenEncoder.Profile, _voiceHandler.NativeScreenSettings, activeCalls));
    }

    private async Task HandleEncoderFailureAsync(string reason)
    {
        await _voiceHandler.ScreenEncoder.StopAsync().ConfigureAwait(false);
        await VoiceCallHandler.StopScreenShareAsync().ConfigureAwait(false);
        _hub.Broadcast("screen_encoder_failed", new global::QuicNet.Serialization.VoiceApiModuleMessage12(reason));
        BroadcastVoiceState();
    }

    private async Task PlatformStoppedAsync(string? reason, long generation)
    {
        try
        {
            if (generation != Interlocked.Read(ref _platformGeneration)) return;
            await NativeVideoRuntime.Instance.StopAsync("local").ConfigureAwait(false);
            await VoiceCallHandler.StopScreenShareAsync().ConfigureAwait(false);
            if (generation != Interlocked.Read(ref _platformGeneration)) return;
            _hub.Broadcast("screen_share_stopped", new global::QuicNet.Serialization.VoiceApiModuleMessage3("local", "Tú"));
            if (reason != null) _hub.Broadcast("screen_encoder_failed", new global::QuicNet.Serialization.VoiceApiModuleMessage12(reason));
            BroadcastVoiceState();
        }
        catch (Exception ex) { WebUiServer.LogEvent($"[SCREEN] {ex}"); }
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        var audio = _voiceHandler.AudioService;

        if (path == "/api/voice/cameras" && req.HttpMethod == "GET")
        {
            var cameras = WebUiServer.Instance?.PlatformScreenCapture?.Cameras() ?? await FfmpegScreenEncoder.CamerasAsync().ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp,cameras).ConfigureAwait(false);return true;
        }
        if (path == "/api/voice/native-video" && req.HttpMethod == "GET")
        {
            if (req.QueryString["refresh"] == "1") await NativeVideoRuntime.Instance.DetectAsync().ConfigureAwait(false);
            else await NativeVideoRuntime.Instance.EnsureCapabilitiesAsync().ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new QuicNet.Serialization.NativeVideoSnapshot(NativeVideoRuntime.Instance.Capabilities,
                VoiceCallHandler.ActiveCalls.Values.Select(s => new QuicNet.Serialization.NativeVideoPeerCapabilities(s.Peer.Id.ToString(), s.Peer.Name, s.NativeVideoCapabilities)).ToArray(), NativeVideoRuntime.Instance.States)).ConfigureAwait(false);
            return true;
        }
        if (path == "/api/voice/native-video/playback" && req.HttpMethod == "POST")
        {
            using var doc=await WebUiContext.ReadJsonAsync(req,path).ConfigureAwait(false);
            var options=doc.RootElement.Deserialize(NativeVideoJsonContext.Default.NativePlaybackOptions) ?? throw new ArgumentException("Missing playback options.");
            NativeVideoRuntime.Instance.Configure(options);
            await WebUiContext.WriteJsonAsync(resp,new QuicNet.Serialization.VoiceApiModuleMessage20(true)).ConfigureAwait(false);return true;
        }
        if (path == "/api/voice/native-video/layout" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req,path).ConfigureAwait(false);
            var layout = doc.RootElement.Deserialize<NativeVideoLayout>(NativeVideoJsonContext.Default.NativeVideoLayout) ?? throw new ArgumentException("Missing layout.");
            if (layout.peerId != "local" && (!Guid.TryParse(layout.peerId,out var videoPeer) || !VoiceCallHandler.ActiveCalls.ContainsKey(videoPeer))) throw new ArgumentException("No active video peer.");
            await NativeVideoRuntime.Instance.LayoutAsync(layout).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp,new QuicNet.Serialization.VoiceApiModuleMessage20(true)).ConfigureAwait(false);return true;
        }

        if (path == "/api/voice/state" && req.HttpMethod == "GET")
        {
            var activeCalls = VoiceCallHandler.ActiveCalls.Values.Select(c => new global::QuicNet.Serialization.VoiceApiModuleMessage13(c.Peer.Id.ToString(), c.Peer.Name ?? "Peer", _voiceHandler.AudioService.GetPeerBufferedAudioMs(c.Peer.Id), audio.GetPeerVolume(c.Peer.Id), audio.GetPeerMuted(c.Peer.Id), c.IsOutboundMuted, c.IsRemoteScreenSharing, c.IsScreenShareAllowed, c.RemoteScreenEncoderCodecMask, c.RemoteScreenDecoderCodecMask) { startedAtUnixMs = new DateTimeOffset(c.StartedAtUtc).ToUnixTimeMilliseconds(), screenDecoderHardwarePreferenceMask = c.RemoteScreenDecoderHardwarePreferenceMask, screenCapabilitiesReceived = c.RemoteScreenCapabilitiesReceived }).ToList();

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage14(audio.IsMuted, audio.IsDeafened, audio.MasterVolume, audio.IsTestLoopback, audio.CurrentInputDeviceId ?? "default", audio.CurrentOutputDeviceId ?? "default", audio.BackendName, audio.IsCaptureRunning && audio.CaptureError == null, audio.IsPlaybackRunning && audio.PlaybackError == null, audio.CaptureError, audio.PlaybackError, _voiceHandler.AudioService.Bitrate, _voiceHandler.RequestedBitrate, VoiceCallHandler.IsLocalScreenSharing, _voiceHandler.ScreenEncoder.Profile != null, VoiceCallHandler.LocalScreenTargetPeerId, _voiceHandler.ScreenEncoder.Profile, _voiceHandler.NativeScreenSettings, activeCalls)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/devices" && req.HttpMethod == "GET")
        {
            var inputs = audio.GetInputDevices().Select(d => new global::QuicNet.Serialization.VoiceApiModuleMessage15(d.Id, d.Name, d.IsDefault)).ToList();
            var outputs = audio.GetOutputDevices().Select(d => new global::QuicNet.Serialization.VoiceApiModuleMessage15(d.Id, d.Name, d.IsDefault)).ToList();

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage16(inputs, outputs, audio.CurrentInputDeviceId ?? "default", audio.CurrentOutputDeviceId ?? "default")).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/voice/media-capabilities" or "/api/voice/screen-share/gpu-codecs" && req.HttpMethod == "GET")
        {
            var ffmpeg = await FfmpegInstallation.GetStatusAsync(req.QueryString["refresh"] == "1", CancellationToken.None).ConfigureAwait(false);
            var platformCapture = WebUiServer.Instance?.PlatformScreenCapture;
            var nativeEncoders = platformCapture?.Encoders() ?? await _voiceHandler.ScreenEncoder.CapabilitiesAsync(CancellationToken.None, req.QueryString["refresh"] == "1").ConfigureAwait(false);
            var displays = ScreenCaptureInventory.Displays();
            var encoders = new List<object>
            {
                new global::QuicNet.Serialization.VoiceApiModuleMessage17("h264", "H.264 / AVC", (bool?)null, (int?)null, "4-12 Mbps", "FFmpeg verifica la recepción con vídeo de muestra y anuncia el resultado a los participantes."),
                new global::QuicNet.Serialization.VoiceApiModuleMessage17("hevc", "H.265 / HEVC", (bool?)null, (int?)null, "4-12 Mbps", "FFmpeg verifica la recepción con vídeo de muestra y anuncia el resultado a los participantes."),
                new global::QuicNet.Serialization.VoiceApiModuleMessage17("vp9", "VP9", (bool?)null, (int?)null, "4-12 Mbps", "FFmpeg verifica la recepción con vídeo de muestra y anuncia el resultado a los participantes."),
                new global::QuicNet.Serialization.VoiceApiModuleMessage17("av1", "AV1", (bool?)null, (int?)null, "3-10 Mbps", "FFmpeg verifica la recepción con vídeo de muestra y anuncia el resultado a los participantes.")
            };

            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage18("FFmpeg nativo · GPU / CPU", (platformCapture != null || FfmpegScreenEncoder.CaptureAvailable) && nativeEncoders.Any(e => e.Available), nativeEncoders, FfmpegScreenEncoder.Sources(displays), displays, new[] { 15, 24, 30, 60, 90, 120, 144 }, new[] { 360, 480, 720, 1080, 1440, 2160 }, 7680, 4320, audio.BackendName, (bool?)null, (int?)null, 30000000, encoders, ffmpeg)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/capabilities" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            uint encoderCodecMask = doc.RootElement.TryGetProperty("encoderCodecMask", out var encEl)
                ? encEl.GetUInt32()
                : 0u;
            uint decoderCodecMask = doc.RootElement.TryGetProperty("decoderCodecMask", out var decEl)
                ? decEl.GetUInt32()
                : 0u;

            var native = NativeVideoRuntime.Instance.Capabilities;
            await VoiceCallHandler.SetLocalScreenCodecCapabilitiesAsync(encoderCodecMask, native.decoderMask, native.hardwareMask).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage19(true, VoiceCallHandler.LocalScreenEncoderCodecMask, VoiceCallHandler.LocalScreenDecoderCodecMask) { decoderHardwarePreferenceMask = VoiceCallHandler.LocalScreenDecoderHardwarePreferenceMask }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/preview" && req.HttpMethod == "GET")
        {
            string source = req.QueryString["source"] ?? "";
            if (!FfmpegScreenEncoder.Sources().Any(s => s.Id == source))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage21("La fuente ya no está disponible."), 404).ConfigureAwait(false);
                return true;
            }
            try
            {
                byte[] jpeg = await FfmpegScreenEncoder.PreviewAsync(source, CancellationToken.None).ConfigureAwait(false);
                resp.ContentType = "image/jpeg";
                resp.Headers["Cache-Control"] = "no-store";
                resp.ContentLength64 = jpeg.Length;
                await resp.OutputStream.WriteAsync(jpeg).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage21("No se puede previsualizar esta fuente. Restaura la ventana e inténtalo de nuevo."), 409).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/voice/screen-share/sources" && req.HttpMethod == "GET")
        {
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage22(FfmpegScreenEncoder.Sources())).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/decoder-check" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            var json = doc.RootElement;
            string codec = json.GetProperty("codec").GetString() ?? "";
            int width = json.GetProperty("width").GetInt32();
            int height = json.GetProperty("height").GetInt32();
            if (codec.Length > 128 || width < 1 || width > 7680 || height < 1 || height > 4320) throw new InvalidDataException("Invalid decoder configuration.");
            string? description = json.TryGetProperty("description", out var descriptionJson) && descriptionJson.ValueKind == JsonValueKind.String
                ? descriptionJson.GetString() : null;
            if (description != null)
            {
                if (description.Length > 65536) throw new InvalidDataException("Oversized decoder description.");
                _ = Convert.FromBase64String(description);
            }
            Guid? target = json.TryGetProperty("targetPeerId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetGuid() : null;
            bool supported = await _voiceHandler.ProbeScreenDecodersAsync(new("web", 0, width, height, 0, codec, description, 0), target, CancellationToken.None).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage23(true, supported)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/platform-start" && req.HttpMethod == "POST")
        {
            await _voiceHandler.StopSystemAudioAsync().ConfigureAwait(false);
            var capture = WebUiServer.Instance?.PlatformScreenCapture ?? throw new NotSupportedException("No hay captura de pantalla del sistema.");
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            var options = WebUiContext.ReadJsonValue<ScreenEncoderOptions>(doc.RootElement, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            options.Validate();
            if (VoiceCallHandler.ActiveCalls.IsEmpty) throw new InvalidOperationException("Inicia una llamada antes de compartir.");
            if (options.TargetPeerId.HasValue && !VoiceCallHandler.ActiveCalls.ContainsKey(options.TargetPeerId.Value)) throw new InvalidOperationException("El contacto ha salido de la llamada.");
            long generation = Interlocked.Increment(ref _platformGeneration);
            await _voiceHandler.ScreenEncoder.StopAsync().ConfigureAwait(false);
            try
            {
                var profile = await capture.StartAsync(options, async packet =>
                {
                    await VoiceCallHandler.SendScreenFrameAsync(packet).ConfigureAwait(false);
                    NativeVideoRuntime.Instance.Enqueue(Guid.Empty, options.CodecId, packet);
                }, reason => { _ = PlatformStoppedAsync(reason, generation); }).ConfigureAwait(false);
                await _voiceHandler.ProbeScreenDecodersAsync(profile, options.TargetPeerId, CancellationToken.None).ConfigureAwait(false);
                if (generation != Interlocked.Read(ref _platformGeneration)) throw new OperationCanceledException("Captura cancelada.");
                await VoiceCallHandler.StartScreenShareAsync(profile.Width, profile.Height, profile.CodecId, (byte)profile.Fps, options.TargetPeerId, options.Source.StartsWith("camera:", StringComparison.Ordinal) ? "camera" : "screen").ConfigureAwait(false);
                _hub.Broadcast("screen_share_started", new global::QuicNet.Serialization.VoiceApiModuleMessage2("local", "Tú · Pantalla", profile.Width, profile.Height, 1, profile.Fps));
                BroadcastVoiceState(); capture.Resume();
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage24(true, profile)).ConfigureAwait(false);
            }
            catch
            {
                if (generation == Interlocked.Read(ref _platformGeneration))
                {
                    await capture.StopAsync().ConfigureAwait(false);
                    await PlatformStoppedAsync(null, generation).ConfigureAwait(false);
                    Interlocked.CompareExchange(ref _platformGeneration, generation + 1, generation);
                }
                throw;
            }
            return true;
        }

        if (path == "/api/voice/screen-share/native-start" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            var options = global::QuicNet.WebUi.WebUiContext.ReadJsonValue<ScreenEncoderOptions>(doc.RootElement, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Missing encoder settings.");
            options.Validate();
            if (VoiceCallHandler.ActiveCalls.IsEmpty) throw new InvalidOperationException("No active call.");
            if (options.TargetPeerId.HasValue && !VoiceCallHandler.ActiveCalls.ContainsKey(options.TargetPeerId.Value))
                throw new InvalidOperationException("The target peer has left the call.");
            var profile = await _voiceHandler.StartNativeScreenAsync(options).ConfigureAwait(false);
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage24(true, profile)).ConfigureAwait(false);
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
            string? error = type.Equals("input", StringComparison.OrdinalIgnoreCase) ? audio.CaptureError : audio.PlaybackError;
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage25(error == null, type, deviceId, error), error == null ? 200 : 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/mute" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool muted = doc.RootElement.TryGetProperty("muted", out var mutedEl) ? mutedEl.GetBoolean() : !audio.IsMuted;
            audio.IsMuted = muted;
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage26(true, audio.IsMuted)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/deafen" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool deafened = doc.RootElement.TryGetProperty("deafened", out var deafenedEl) ? deafenedEl.GetBoolean() : !audio.IsDeafened;
            audio.IsDeafened = deafened;
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage27(true, audio.IsDeafened)).ConfigureAwait(false);
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
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage28(true, audio.MasterVolume)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/test-mic" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            bool enabled = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("enabled", out var enEl)
                ? enEl.GetBoolean()
                : !audio.IsTestLoopback;

            audio.IsTestLoopback = enabled;
            if (enabled && (!audio.IsCaptureRunning || !audio.IsPlaybackRunning))
            {
                audio.Start(audio.CurrentInputDeviceId, audio.CurrentOutputDeviceId);
            }
            else if (VoiceCallHandler.ActiveCalls.IsEmpty)
            {
                audio.Stop();
            }

            string? error = enabled ? audio.CaptureError ?? audio.PlaybackError : null;
            if (error != null) { audio.IsTestLoopback = false; if (VoiceCallHandler.ActiveCalls.IsEmpty) audio.Stop(); }
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage29(error == null, audio.IsTestLoopback, error), error == null ? 200 : 409).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice-hangup" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = doc.RootElement.TryGetProperty("peerId", out var idEl) && Guid.TryParse(idEl.GetString(), out var id)
                ? id
                : Guid.Empty;

            if (peerId != Guid.Empty) WebUiServer.CancelPendingConnection(peerId, _voiceHandler.ProtocolId);
            bool hungUp = peerId != Guid.Empty && _voiceHandler.Hangup(peerId);
            BroadcastVoiceState();
            WebUiServer.BroadcastStatusUpdate();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage30(true, hungUp)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/codec-settings" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("bitrate", out var brEl))
            {
                if (brEl.ValueKind != JsonValueKind.Number || !brEl.TryGetInt32(out int newBitrate) ||
                    newBitrate is < OpusVoiceCodec.MinimumBitrate or > OpusVoiceCodec.MaximumBitrate)
                {
                    await WebUiContext.WriteJsonAsync(resp,
                        new global::QuicNet.Serialization.VoiceApiModuleMessage31(false, $"Voice bitrate must be between {OpusVoiceCodec.MinimumBitrate} and {OpusVoiceCodec.MaximumBitrate} bps."), 400).ConfigureAwait(false);
                    return true;
                }
                _voiceHandler.RequestedBitrate = newBitrate;
                QuicPunch.QuicPunchLog.Info($"[VOICE] Requested microphone bitrate set to {newBitrate} bps via WebUI.");
            }

            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage32(true, _voiceHandler.AudioService.Bitrate, _voiceHandler.RequestedBitrate)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/start" && req.HttpMethod == "POST")
        {
            Interlocked.Increment(ref _platformGeneration);
            if (WebUiServer.Instance?.PlatformScreenCapture is { } platformCapture) await platformCapture.StopAsync().ConfigureAwait(false);
            await _voiceHandler.ScreenEncoder.StopAsync().ConfigureAwait(false);
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            int width = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 1280;
            int height = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 720;
            byte codecId = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("codecId", out var cEl) ? cEl.GetByte() : (byte)1;
            byte fps = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("fps", out var fpsEl) ? fpsEl.GetByte() : (byte)30;
            Guid? targetPeerId = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("targetPeerId", out var targetEl) && targetEl.ValueKind == JsonValueKind.String
                ? Guid.Parse(targetEl.GetString() ?? "") : null;

            string kind = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("kind", out var kindEl) && kindEl.GetString() == "camera" ? "camera" : "screen";
            string? audioShareId = kind == "screen" && doc.RootElement.TryGetProperty("audioSessionId", out var audioSession) &&
                Guid.TryParse(audioSession.GetString(), out var audioId) ? audioId.ToString() : kind == "screen" ? _voiceHandler.SystemAudioShareId : null;
            if (_voiceHandler.SystemAudioShareId != audioShareId) await _voiceHandler.StopSystemAudioAsync().ConfigureAwait(false);
            await VoiceCallHandler.StartScreenShareAsync(width, height, codecId, fps, targetPeerId, kind).ConfigureAwait(false);
            _voiceHandler.SystemAudioShareId = audioShareId;
            BroadcastVoiceState();
            _hub.Broadcast("screen_share_started", new global::QuicNet.Serialization.VoiceApiModuleMessage8("local", "You", width, height, codecId, fps, kind));
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage33(true, true)).ConfigureAwait(false);
            return true;
        }

        if (path is "/api/voice/screen-share/system-audio/start" or "/api/voice/screen-share/system-audio/stop" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            string shareId = WebUiContext.ParseGuid(doc.RootElement, "audioSessionId").ToString();
            try
            {
                if (path.EndsWith("/start", StringComparison.Ordinal)) await _voiceHandler.StartSystemAudioAsync(shareId).ConfigureAwait(false);
                else await _voiceHandler.StopSystemAudioAsync(shareId).ConfigureAwait(false);
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage20(true)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage21("No se pudo compartir el audio del sistema sin incluir la llamada: " + error.Message), 409).ConfigureAwait(false);
            }
            return true;
        }

        if (path == "/api/voice/screen-share/stop" && req.HttpMethod == "POST")
        {
            await NativeVideoRuntime.Instance.StopAsync("local").ConfigureAwait(false);
            using var stopBody = await WebUiContext.ReadJsonAsync(req, path, allowEmpty: true).ConfigureAwait(false);
            string? requestId = stopBody.RootElement.ValueKind == JsonValueKind.Object && stopBody.RootElement.TryGetProperty("requestId", out var request) &&
                request.ValueKind == JsonValueKind.String && Guid.TryParse(request.GetString(), out _) ? request.GetString() : null;
            Interlocked.Increment(ref _platformGeneration);
            if (WebUiServer.Instance?.PlatformScreenCapture is { } platformCapture) await platformCapture.StopAsync().ConfigureAwait(false);
            await _voiceHandler.ScreenEncoder.StopAsync().ConfigureAwait(false);
            await VoiceCallHandler.StopScreenShareAsync().ConfigureAwait(false);
            BroadcastVoiceState();
            _hub.Broadcast("screen_share_stopped", new global::QuicNet.Serialization.VoiceApiModuleMessage3("local", "You") { requestId = requestId });
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage33(true, false)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/peer-toggle" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            bool allowed = doc.RootElement.TryGetProperty("allowed", out var aEl) && aEl.GetBoolean();
            VoiceCallHandler.SetPeerScreenShareAllowed(peerId, allowed);
            BroadcastVoiceState();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage34(true, peerId.ToString(), allowed)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/request" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            var reply = await _voiceHandler.RequestScreenControlAsync(peerId, "start").ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, reply).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/voice/screen-share/remote" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            var json = doc.RootElement;
            Guid peerId = WebUiContext.ParseGuid(json, "peerId");
            string action = json.GetProperty("action").GetString() ?? "";
            long revision = json.TryGetProperty("revision", out var revisionValue) ? revisionValue.GetInt64() : 0;
            var settings = json.TryGetProperty("settings", out var value) ? global::QuicNet.WebUi.WebUiContext.ReadJsonValue<ScreenTransmissionSettings>(value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
            var reply = await _voiceHandler.RequestScreenControlAsync(peerId, action, revision, settings).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, reply).ConfigureAwait(false);
            return true;
        }
        if (path == "/api/voice/screen-share/browser-state" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            var settings = global::QuicNet.WebUi.WebUiContext.ReadJsonValue<ScreenTransmissionSettings>(doc.RootElement.GetProperty("settings"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ArgumentException("Missing screen settings.");
            string[] codecs = global::QuicNet.WebUi.WebUiContext.ReadJsonValue<string[]>(doc.RootElement.GetProperty("codecs")) ?? [];
            _voiceHandler.ReportBrowserScreenSettings(settings, codecs);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage20(true)).ConfigureAwait(false);
            return true;
        }
        if (path == "/api/voice/screen-share/browser-result" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            bool completed = _voiceHandler.CompleteBrowserScreenUpdate(WebUiContext.ParseGuid(doc.RootElement, "requestId"), doc.RootElement.GetProperty("success").GetBoolean());
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.VoiceApiModuleMessage20(completed)).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
