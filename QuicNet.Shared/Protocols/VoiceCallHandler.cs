using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using QuicNet.Video;
using QuicPunch;
using QuicPunch.Helpers;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicNet.Protocols
{
    /// <summary>
    /// Multi-peer voice sessions use reliable QUIC streams for call lifecycle/authorization.
    /// Real-time Opus VoIP (or PCM fallback) audio frames travel over QUIC datagrams
    /// so a lost frame cannot head-of-line block every newer frame.
    /// </summary>
    internal sealed partial class VoiceCallHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
    {
        private const int MaxVoiceFrameBytes = 64 * 1024;
        private readonly QuicPunch.QuicPunch _qcc;
        private int _disposed;

        private readonly object _bitrateLock = new();
        private readonly Dictionary<VoiceCallSession, int> _sessionBitrateLimits = new();
        private int _requestedBitrate = OpusVoiceCodec.DefaultBitrate;

        // One microphone encoder feeds every call. The requested bitrate is a ceiling;
        // the most constrained active session determines the effective bitrate.
        public int RequestedBitrate
        {
            get { lock (_bitrateLock) return _requestedBitrate; }
            set
            {
                lock (_bitrateLock)
                {
                    _requestedBitrate = Math.Clamp(value, OpusVoiceCodec.MinimumBitrate, OpusVoiceCodec.MaximumBitrate);
                    ApplyAudioBitrate();
                }
            }
        }

        private void ApplyAudioBitrate()
        {
            int bitrate = _requestedBitrate;
            foreach (int limit in _sessionBitrateLimits.Values)
                bitrate = Math.Min(bitrate, limit);
            AudioService.Bitrate = bitrate;
        }

        internal void UpdateAudioBitrate(VoiceCallSession session, double packetLossRatio, double rttMs)
        {
            lock (_bitrateLock)
            {
                if (!_sessionBitrateLimits.TryGetValue(session, out int limit)) return;
                if (!double.IsFinite(packetLossRatio) || !double.IsFinite(rttMs) || rttMs <= 0) return;
                session.MinimumRttMs = Math.Min(session.MinimumRttMs, rttMs);
                double queueDelay = rttMs - session.MinimumRttMs;
                if (packetLossRatio > 0.05 || queueDelay > Math.Max(30, session.MinimumRttMs * 0.5))
                    limit = Math.Max(Math.Min(16000, _requestedBitrate), Math.Min(limit, _requestedBitrate) - 8000);
                else if (packetLossRatio < 0.01 && queueDelay < Math.Max(15, session.MinimumRttMs * 0.2))
                    limit = Math.Min(OpusVoiceCodec.MaximumBitrate, limit + 8000);
                _sessionBitrateLimits[session] = limit;
                ApplyAudioBitrate();
            }
        }

        internal void UpdateAudioTelemetry(VoiceCallSession session, QuicConnectionTelemetry telemetry)
        {
            lock (_bitrateLock)
            {
                if (!_sessionBitrateLimits.ContainsKey(session)) return;
                var previous = session.PreviousTelemetry;
                session.PreviousTelemetry = telemetry;
                if (previous == null || telemetry.SendTotalPackets < previous.SendTotalPackets)
                {
                    session.MinimumRttMs = telemetry.RttMs > 0 ? telemetry.RttMs : double.PositiveInfinity;
                    return;
                }
                ulong sent = telemetry.SendTotalPackets - previous.SendTotalPackets;
                if (sent == 0) return;
                double lost = Math.Max(0, (double)telemetry.SendSuspectedLostPackets - previous.SendSuspectedLostPackets -
                    ((double)telemetry.SendSpuriousLostPackets - previous.SendSpuriousLostPackets));
                UpdateAudioBitrate(session, Math.Clamp(lost / sent, 0, 1), telemetry.RttMs);
            }
        }

        private void RemoveAudioSession(VoiceCallSession session)
        {
            lock (_bitrateLock)
            {
                if (_sessionBitrateLimits.Remove(session)) ApplyAudioBitrate();
            }
        }

        public QuicPunch.Audio.NativeAudioService AudioService { get; }
        public FfmpegScreenEncoder ScreenEncoder { get; } = new();
        public static VoiceCallHandler? Instance { get; private set; }

        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
        public string ProtocolName => "VoiceCall";
        public bool SupportsSessionRecovery => true;
        public ushort StreamPriority => (ushort)QuicStreamPriority.Critical;

        public const byte FrameTypePing = 0x01;
        public const byte FrameTypePong = 0x02;
        public const byte FrameTypeScreenShareStart = 0x20;
        public const byte FrameTypeScreenShareStop = 0x21;
        public const byte FrameTypeScreenShareVideo = 0x22;
        public const byte FrameTypeScreenShareAudio = 0x23;
        public const byte FrameTypeScreenShareCapabilities = 0x24;
        public const byte FrameTypeScreenShareRequest = 0x25;
        public const byte FrameTypeScreenShareRequestDenied = 0x26;
        public const byte FrameTypeScreenDecoderProbe = 0x27;
        public const byte FrameTypeScreenDecoderProbeResult = 0x28;
        public const byte FrameTypeScreenControlRequest = 0x29;
        public const byte FrameTypeScreenControlReply = 0x2A;
        private readonly ConcurrentDictionary<(Guid Peer, Guid Request), TaskCompletionSource<bool>> _decoderProbes = new();
        public event Action<PeerInfo, JsonElement>? OnScreenDecoderProbe;
        public event Action<PeerInfo, JsonElement>? OnScreenDecoderDiagnostic;

        public event Action<PeerInfo, byte[]>? OnAudioDatagramReceived;
        public event Action<PeerInfo>? OnCallEstablished;
        public event Action<PeerInfo>? OnCallEnded;
        public event Action<PeerInfo, int, int, byte, byte>? OnScreenShareStarted;
        public event Action<PeerInfo>? OnScreenShareStopped;
        public event Action<PeerInfo>? OnScreenShareRequested;
        public event Action<PeerInfo>? OnScreenShareRequestDenied;
        public event Action<PeerInfo, byte, byte[]>? OnScreenFrameReceived;
        public event Func<PeerInfo, byte, byte[], Task>? OnScreenFrameReceivedAsync;
        public event Action<PeerInfo, byte[]>? OnScreenAudioReceived;
        public event Action<PeerInfo, uint, uint>? OnScreenCodecCapabilitiesReceived;

        public VoiceCallHandler(QuicPunch.QuicPunch qcc, QuicPunch.Audio.INativeAudioBackend? audioBackend = null)
        {
            _qcc = qcc ?? throw new ArgumentNullException(nameof(qcc));
            AudioService = new QuicPunch.Audio.NativeAudioService(audioBackend);
            Instance = this;

            AudioService.OnAudioPacketReady += packet =>
            {
                if (ActiveCalls.Count > 0)
                {
                    _ = BroadcastAudioDatagramAsync(packet);
                }
            };

            OnAudioDatagramReceived += (peer, data) =>
            {
                AudioService.EnqueueIncomingAudio(peer.Id, data);
            };

            OnCallEstablished += peer =>
            {
                if (ActiveCalls.Count == 1) AudioService.Start(AudioService.CurrentInputDeviceId, AudioService.CurrentOutputDeviceId);
            };

            OnCallEnded += peer =>
            {
                AudioService.RemovePeer(peer.Id);
                if (ActiveCalls.IsEmpty) _ = StopSystemAudioAsync();
                if (ActiveCalls.IsEmpty && !AudioService.IsTestLoopback)
                {
                    AudioService.Stop();
                }
            };
        }

        public sealed partial class VoiceCallSession : IDisposable
        {
            internal double MinimumRttMs = double.PositiveInfinity;
            internal QuicConnectionTelemetry? PreviousTelemetry;
            private readonly VoiceCallHandler _owner;
            private readonly CancellationTokenSource _sessionCts = new();
            private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
            private Action? _detachAction;
            private int _disposed;

            public PeerInfo Peer { get; }
            public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
            public Stream Stream { get; }
            public QuicConnection Connection { get; }
            public CancellationToken CancellationToken => _sessionCts.Token;
            public bool IsRemoteScreenSharing { get; internal set; }
            public string RemoteVideoKind { get; internal set; } = "screen";
            public byte RemoteScreenCodecId { get; internal set; } = 1;
            public bool IsOutboundMuted { get; set; }
            public bool IsScreenShareAllowed { get; set; } = true;
            public bool IsScreenDecoderApproved { get; set; } = true;
            public uint RemoteScreenEncoderCodecMask { get; internal set; }
            public uint RemoteScreenDecoderCodecMask { get; internal set; }
            public uint? RemoteScreenDecoderHardwarePreferenceMask { get; internal set; }
            public bool RemoteScreenCapabilitiesReceived { get; internal set; }
            public byte? RemoteScreenControlVersion { get; internal set; }

            public VoiceCallSession(VoiceCallHandler owner, PeerInfo peer, Stream stream, QuicConnection connection)
            {
                _owner = owner;
                Peer = peer;
                Stream = stream;
                Connection = connection;
                lock (owner._bitrateLock)
                    owner._sessionBitrateLimits[this] = OpusVoiceCodec.MaximumBitrate;
            }

            public void SetDetachAction(Action detach) => _detachAction = detach;

            public void DetachHandlers()
            {
                try { _detachAction?.Invoke(); } catch { }
                _detachAction = null;
            }

            public Task SendDatagramAsync(byte[] audioData, CancellationToken ct = default)
            {
                if (audioData == null || audioData.Length == 0 || audioData.Length > MaxVoiceFrameBytes)
                    return Task.CompletedTask;

                // Native QUIC RFC 9221 datagram channel
                // Unreliable, TLS encrypted audio; congestion drops stale packets.
                if (Connection.DatagramChannel != null)
                {
                    Connection.SendDatagram(audioData);
                }
                return Task.CompletedTask;
            }

            public async Task SendFrameAsync(byte frameType, ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
            {
                if (_disposed != 0) return;
                if (frameType is FrameTypeScreenShareVideo or FrameTypeScreenShareAudio && RemoteScreenControlVersion >= 2 &&
                    Connection.IsNativeQuic && Volatile.Read(ref _sendMediaEpoch) != 0)
                { await SendMediaAsync(frameType, payload).ConfigureAwait(false); return; }
                bool writeStarted = false;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token, ct);
                try
                {
                    await _streamWriteLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    try
                    {
                        if (frameType == FrameTypeScreenShareStart && RemoteScreenControlVersion >= 2 && Connection.IsNativeQuic)
                            payload = BeginMediaEpoch(payload);
                        byte[] header = new byte[5];
                        header[0] = frameType;
                        int length = payload.Length;
                        header[1] = (byte)(length >> 24);
                        header[2] = (byte)(length >> 16);
                        header[3] = (byte)(length >> 8);
                        header[4] = (byte)length;

                        writeStarted = true;
                        await Stream.WriteAsync(header.AsMemory(), linkedCts.Token).ConfigureAwait(false);
                        if (length > 0)
                        {
                            await Stream.WriteAsync(payload, linkedCts.Token).ConfigureAwait(false);
                        }
                        await Stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _streamWriteLock.Release();
                    }
                }
                catch (OperationCanceledException) { if (writeStarted) Dispose(); }
                catch (Exception ex)
                {
                    if (writeStarted) Dispose();
                    QuicPunchLog.Info($"[VOICE CALL] Stream write failed to {Peer.Name}: {ex.Message}");
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _owner.RemoveAudioSession(this);
                try { _sessionCts.Cancel(); } catch { }
                DetachHandlers();
                try { Stream.Close(); } catch { }
                try { Stream.Dispose(); } catch { }
                try { _ = Connection.DisposeAsync(); } catch { }
                try { _sessionCts.Dispose(); } catch { }
                // Pending writers may still release the gate while cancellation unwinds.
            }
        }

        public static ConcurrentDictionary<Guid, VoiceCallSession> ActiveCalls { get; } = new();

        public static bool IsLocalScreenSharing { get; private set; }
        public static Guid? LocalScreenTargetPeerId { get; private set; }
        public static byte LocalScreenCodecId { get; private set; } = 1; // 1 = H.264, 3 = VP9, 4 = AV1, 5 = HEVC; 0 is legacy JPEG only.
        public static int LocalScreenWidth { get; private set; } = 1280;
        public static int LocalScreenHeight { get; private set; } = 720;
        public static byte LocalScreenFps { get; private set; } = 30;
        public static string LocalVideoKind { get; private set; } = "screen";
        public static uint LocalScreenEncoderCodecMask { get; private set; }
        public static uint LocalScreenDecoderCodecMask { get; private set; }
        public static uint? LocalScreenDecoderHardwarePreferenceMask { get; private set; }

        private static bool CanReceiveLocalScreen(VoiceCallSession session) =>
            session.IsScreenShareAllowed &&
            (!LocalScreenTargetPeerId.HasValue || session.Peer.Id == LocalScreenTargetPeerId.Value);

        public static async Task StartScreenShareAsync(int width = 1280, int height = 720, byte codecId = 1, byte fps = 30, Guid? targetPeerId = null, string kind = "screen")
        {
            if (ActiveCalls.IsEmpty)
                throw new InvalidOperationException("Inicia una llamada antes de activar el vídeo.");
            if (targetPeerId.HasValue && !ActiveCalls.ContainsKey(targetPeerId.Value))
                throw new InvalidOperationException("The requested screen share peer is no longer in the call.");
            IsLocalScreenSharing = true;
            LocalScreenTargetPeerId = targetPeerId;
            LocalScreenWidth = width;
            LocalScreenHeight = height;
            LocalScreenCodecId = codecId;
            LocalScreenFps = fps;
            LocalVideoKind = kind == "camera" ? "camera" : "screen";

            byte[] startPayload = BuildScreenStartPayload(width, height, codecId, fps);
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                if (!CanReceiveLocalScreen(session)) continue;
                await session.SendFrameAsync(FrameTypeScreenShareStart, startPayload).ConfigureAwait(false);
            }
        }

        private static byte[] BuildScreenStartPayload(int width, int height, byte codecId, byte fps)
        {
            byte[] startPayload = new byte[LocalVideoKind == "camera" ? 11 : 10];
            startPayload[0] = (byte)(width >> 24);
            startPayload[1] = (byte)(width >> 16);
            startPayload[2] = (byte)(width >> 8);
            startPayload[3] = (byte)width;
            startPayload[4] = (byte)(height >> 24);
            startPayload[5] = (byte)(height >> 16);
            startPayload[6] = (byte)(height >> 8);
            startPayload[7] = (byte)height;
            startPayload[8] = codecId;
            startPayload[9] = fps;
            if (startPayload.Length > 10) startPayload[10] = 1; // Optional extension; old receivers ignore it.
            return startPayload;
        }

        private static byte[] BuildScreenCapabilitiesPayload(uint encoderCodecMask, uint decoderCodecMask)
        {
            byte[] payload = new byte[LocalScreenDecoderHardwarePreferenceMask.HasValue ? 13 : 9];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), encoderCodecMask);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), decoderCodecMask);
            payload[8] = 2;
            if (LocalScreenDecoderHardwarePreferenceMask is uint hardware)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9, 4), hardware & decoderCodecMask);
            return payload;
        }

        public static async Task SetLocalScreenCodecCapabilitiesAsync(uint encoderCodecMask, uint decoderCodecMask, uint? decoderHardwarePreferenceMask = null)
        {
            // JPEG remains readable for old peers, but it is intentionally not
            // advertised as a selectable encoder or an automatic fallback.
            LocalScreenEncoderCodecMask = encoderCodecMask & ~1u;
            LocalScreenDecoderCodecMask = decoderCodecMask & ~1u;
            LocalScreenDecoderHardwarePreferenceMask = decoderHardwarePreferenceMask & LocalScreenDecoderCodecMask;
            byte[] payload = BuildScreenCapabilitiesPayload(LocalScreenEncoderCodecMask, LocalScreenDecoderCodecMask);
            var sends = ActiveCalls.Values.Select(session =>
                session.SendFrameAsync(FrameTypeScreenShareCapabilities, payload));
            await Task.WhenAll(sends).ConfigureAwait(false);
        }

        private static Task NotifySessionScreenCodecCapabilitiesAsync(VoiceCallSession session)
        {
            byte[] payload = BuildScreenCapabilitiesPayload(LocalScreenEncoderCodecMask, LocalScreenDecoderCodecMask);
            return session.SendFrameAsync(FrameTypeScreenShareCapabilities, payload);
        }

        public static async Task NotifySessionScreenShareStartAsync(VoiceCallSession session)
        {
            if (IsLocalScreenSharing && session != null && CanReceiveLocalScreen(session))
            {
                byte[] startPayload = BuildScreenStartPayload(LocalScreenWidth, LocalScreenHeight, LocalScreenCodecId, LocalScreenFps);
                await session.SendFrameAsync(FrameTypeScreenShareStart, startPayload).ConfigureAwait(false);
            }
        }

        public static async Task StopScreenShareAsync()
        {
            if (Instance is { } handler) await handler.StopSystemAudioAsync().ConfigureAwait(false);
            IsLocalScreenSharing = false;
            LocalScreenTargetPeerId = null;
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                await session.SendFrameAsync(FrameTypeScreenShareStop).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenFrameAsync(byte[] videoChunk)
        {
            if (videoChunk == null || videoChunk.Length == 0 || !IsLocalScreenSharing) return;
            var sends = ActiveCalls.Values
                .Where(session => CanReceiveLocalScreen(session) && session.IsScreenDecoderApproved)
                .Select(session => session.SendFrameAsync(FrameTypeScreenShareVideo, videoChunk));
            await Task.WhenAll(sends).ConfigureAwait(false);
        }

        public static async Task SendScreenFrameToPeerAsync(Guid peerId, byte[] videoChunk)
        {
            if (videoChunk == null || videoChunk.Length == 0) return;
            if (ActiveCalls.TryGetValue(peerId, out var session) && CanReceiveLocalScreen(session) && session.IsScreenDecoderApproved)
            {
                await session.SendFrameAsync(FrameTypeScreenShareVideo, videoChunk).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenAudioAsync(byte[] audioChunk)
        {
            if (audioChunk == null || audioChunk.Length == 0 || !IsLocalScreenSharing) return;
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                if (!CanReceiveLocalScreen(session)) continue;
                await session.SendFrameAsync(FrameTypeScreenShareAudio, audioChunk).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenAudioToPeerAsync(Guid peerId, byte[] audioChunk)
        {
            if (audioChunk == null || audioChunk.Length == 0) return;
            if (ActiveCalls.TryGetValue(peerId, out var session) && CanReceiveLocalScreen(session))
            {
                await session.SendFrameAsync(FrameTypeScreenShareAudio, audioChunk).ConfigureAwait(false);
            }
        }

        public static void SetPeerScreenShareAllowed(Guid peerId, bool allowed)
        {
            if (ActiveCalls.TryGetValue(peerId, out var session))
            {
                session.IsScreenShareAllowed = allowed;
                if (!allowed && IsLocalScreenSharing)
                {
                    _ = session.SendFrameAsync(FrameTypeScreenShareStop);
                }
                else if (allowed && IsLocalScreenSharing && CanReceiveLocalScreen(session))
                {
                    _ = NotifySessionScreenShareStartAsync(session);
                }
            }
        }

        public static Task<bool> RequestPeerScreenShareAsync(Guid peerId)
        {
            if (!ActiveCalls.TryGetValue(peerId, out var session)) return Task.FromResult(false);
            return SendRequestAsync(session);

            static async Task<bool> SendRequestAsync(VoiceCallSession target)
            {
                await target.SendFrameAsync(FrameTypeScreenShareRequest).ConfigureAwait(false);
                return true;
            }
        }

        public static void InjectScreenShareAudio(short[] samples)
        {
            if (samples == null || samples.Length == 0 || ActiveCalls.IsEmpty) return;
            byte[] audioPayload = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples, 0, audioPayload, 0, audioPayload.Length);
            _ = SendScreenAudioAsync(audioPayload);
        }

        public bool Hangup(Guid peerId)
        {
            bool pending = _qcc.HasLogicalProtocolSession(peerId, ProtocolId);
            _ = _qcc.CloseProtocolSessionAsync(peerId, ProtocolId);
            if (ActiveCalls.TryRemove(peerId, out var session))
            {
                session.Dispose();
                AudioService.RemovePeer(peerId);
                OnCallEnded?.Invoke(session.Peer);
                if (ActiveCalls.IsEmpty && !AudioService.IsTestLoopback)
                {
                    AudioService.Stop();
                }
                if (ActiveCalls.IsEmpty || LocalScreenTargetPeerId == peerId)
                {
                    _ = ScreenEncoder.StopAsync();
                    _ = StopScreenShareAsync();
                }
                Console.WriteLine($"\n[VOICE CALL] Voice call with {session.Peer.Name} hung up locally.");
                return true;
            }
            return pending;
        }

        public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[VOICE CALL] Call with {peer.Name} ({peer.ActiveEndPoint}) was rejected or failed.");
            return Task.CompletedTask;
        }

        public async Task<bool> ProbeScreenDecodersAsync(NativeScreenProfile profile, Guid? targetPeerId, CancellationToken ct)
        {
            var sessions = ActiveCalls.Values.Where(s => s.IsScreenShareAllowed && (!targetPeerId.HasValue || s.Peer.Id == targetPeerId.Value)).ToArray();
            if (sessions.Length == 0) return false;
            return (await Task.WhenAll(sessions.Select(async session =>
            {
                Guid request = Guid.NewGuid();
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _decoderProbes[(session.Peer.Id, request)] = completion;
                try
                {
                    await session.SendFrameAsync(FrameTypeScreenDecoderProbe, JsonSerializer.SerializeToUtf8Bytes(new QuicNet.Serialization.ScreenDecoderProbeMessage(request, profile.Codec, profile.Description, profile.Width, profile.Height), QuicNet.Serialization.SharedJson.Options), ct).ConfigureAwait(false);
                    bool supported = await completion.Task.WaitAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
                    session.IsScreenDecoderApproved = supported;
                    return supported;
                }
                catch (TimeoutException)
                {
                    session.IsScreenDecoderApproved = false;
                    OnScreenDecoderDiagnostic?.Invoke(session.Peer, JsonSerializer.SerializeToElement(
                        new QuicNet.Serialization.ScreenDecoderProbeReplyMessage(request, false,
                            new(profile.Codec, profile.Width, profile.Height, null, "El receptor no respondió a la comprobación en 6 segundos. Abre la llamada en ese dispositivo y vuelve a compartir.")),
                        QuicNet.Serialization.SharedJson.Options));
                    return false;
                }
                finally { _decoderProbes.TryRemove((session.Peer.Id, request), out _); }
            }))).All(supported => supported);
        }

        public Task ReplyScreenDecoderProbeAsync(Guid peerId, Guid requestId, bool supported, JsonElement? diagnostic = null)
            => ActiveCalls.TryGetValue(peerId, out var session)
                ? session.SendFrameAsync(FrameTypeScreenDecoderProbeResult, JsonSerializer.SerializeToUtf8Bytes(new QuicNet.Serialization.ScreenDecoderProbeReplyMessage(requestId, supported,
                    diagnostic is { } detail && detail.GetRawText().Length <= 4096 ? detail.Deserialize<QuicNet.Serialization.ScreenDecoderDiagnostic>(QuicNet.Serialization.SharedJson.Options) : null), QuicNet.Serialization.SharedJson.Options))
                : Task.CompletedTask;

        public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
        {
            if (ActiveCalls.TryGetValue(peer.Id, out var existingSession) && !existingSession.CancellationToken.IsCancellationRequested &&
                !existingSession.Connection.TransportLost)
            {
                QuicPunchLog.Info($"[VOICE CALL] Call with {peer.Name} is already active. Preserving current call.");
                return;
            }

            Console.WriteLine($"\n--- P2P VOICE CALL ESTABLISHED with {peer.Name} ({peer.ActiveEndPoint}) ---");

            // Stream priority is scoped to this session. IP DSCP belongs to the
            // shared physical connection and must not mark VPN/bulk traffic as voice.
            stream.SetQuicPriority(QuicStreamPriority.Critical);

            var session = new VoiceCallSession(this, peer, stream, connection);
            if (ActiveCalls.TryRemove(peer.Id, out var previousSession))
            {
                try { previousSession.Stream.Close(); } catch { }
                try { await previousSession.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                previousSession.Dispose();
            }

            ActiveCalls[peer.Id] = session;
            var nativeProfile = ScreenEncoder.Profile ?? ScreenEncoder.PendingProfile;
            if (nativeProfile != null)
            {
                session.IsScreenDecoderApproved = false;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProbeScreenDecodersAsync(nativeProfile, peer.Id, session.CancellationToken).ConfigureAwait(false);
                        await NotifySessionScreenShareStartAsync(session).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                });
            }

            // 2. Attach native datagram channel for unreliable zero-head-of-line-blocking audio transmission
            if (connection.DatagramChannel != null)
            {
                Action<byte[]> dgramHandler = (data) =>
                {
                    if (data.Length == 0 || data.Length > MaxVoiceFrameBytes) return;
                    OnAudioDatagramReceived?.Invoke(peer, data);
                };

                Action<IPEndPoint> roamHandler = (newEp) =>
                {
                    QuicPunchLog.Info($"[VOICE CALL] Peer {peer.Name} roamed to {newEp} during call. Session maintained!");
                    QuicPunchConnection.UpdateQuicEndPoint(peer, newEp);
                };

                connection.DatagramChannel.OnDatagramReceived += dgramHandler;
                connection.DatagramChannel.OnPeerAddressChanged += roamHandler;

                session.SetDetachAction(() =>
                {
                    try { connection.DatagramChannel.OnDatagramReceived -= dgramHandler; } catch { }
                    try { connection.DatagramChannel.OnPeerAddressChanged -= roamHandler; } catch { }
                });
            }

            OnCallEstablished?.Invoke(peer);
            _ = NotifySessionScreenCodecCapabilitiesAsync(session);
            _ = SendNativeCapabilitiesAsync(session);
            if (IsLocalScreenSharing)
            {
                _ = NotifySessionScreenShareStartAsync(session);
            }

            // 3. Lightweight background task to monitor telemetry and adapt Opus bitrate dynamically
            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);
            var mediaTask = ReceiveMediaAsync(session, monitorCts.Token);
            var telemetryTask = Task.Run(async () =>
            {
                while (!monitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, monitorCts.Token).ConfigureAwait(false);
                        if (connection.TryGetTelemetry(out var telem))
                        {
                            peer.LastTelemetry = telem;
                            UpdateAudioTelemetry(session, telem);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, monitorCts.Token);

            // The shared multiplexer verifies correlated heartbeats for every protocol.
            // A successful stream write alone cannot establish remote liveness.
            Task heartbeatTask = Task.CompletedTask;

            byte[] headerBuffer = new byte[5];
            try
            {
                while (!monitorCts.Token.IsCancellationRequested)
                {
                    // Read 5-byte frame header: [Type (1B)][Payload Length (4B)]
                    try
                    {
                        await stream.ReadExactlyAsync(headerBuffer.AsMemory(0, 5), monitorCts.Token).ConfigureAwait(false);
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }

                    byte frameType = headerBuffer[0];
                    int payloadLength = (headerBuffer[1] << 24) | (headerBuffer[2] << 16) | (headerBuffer[3] << 8) | headerBuffer[4];

                    if (payloadLength < 0 || payloadLength > 10 * 1024 * 1024)
                    {
                        QuicPunchLog.Info($"[VOICE CALL] Invalid frame size ({payloadLength} bytes) received from {peer.Name}. Aborting stream.");
                        break;
                    }

                    byte[]? payload = null;
                    if (payloadLength > 0)
                    {
                        payload = new byte[payloadLength];
                        try
                        {
                            await stream.ReadExactlyAsync(payload.AsMemory(0, payloadLength), monitorCts.Token).ConfigureAwait(false);
                        }
                        catch (EndOfStreamException)
                        {
                            break;
                        }
                    }

                    switch (frameType)
                    {
                        case FrameTypeNativeVideoCapabilities:
                            if (payload != null) ReceiveNativeCapabilities(peer, payload);
                            break;
                        case FrameTypeScreenDecoderProbe:
                            if (payload is { Length: > 0 and < 65536 })
                            {
                                using var probe = JsonDocument.Parse(payload);
                                OnScreenDecoderProbe?.Invoke(peer, probe.RootElement.Clone());
                            }
                            break;
                        case FrameTypeScreenDecoderProbeResult:
                            if (payload is { Length: > 0 and < 65536 })
                            {
                                using var result = JsonDocument.Parse(payload);
                                if (result.RootElement.TryGetProperty("requestId", out var request) && request.TryGetGuid(out var requestId) &&
                                    result.RootElement.TryGetProperty("supported", out var supported) &&
                                    _decoderProbes.TryGetValue((peer.Id, requestId), out var completion))
                                {
                                    if (completion.TrySetResult(supported.GetBoolean()))
                                        OnScreenDecoderDiagnostic?.Invoke(peer, result.RootElement.Clone());
                                }
                            }
                            break;
                        case FrameTypePing:
                            _ = session.SendFrameAsync(FrameTypePong, ReadOnlyMemory<byte>.Empty, monitorCts.Token);
                            break;

                        case FrameTypePong:
                            // Heartbeat pong received, stream is alive
                            break;

                        case FrameTypeScreenShareStart:
                            int width = 1280;
                            int height = 720;
                            byte codecId = 1;
                            byte fps = 30;
                            if (payload != null && payload.Length >= 8)
                            {
                                width = (payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
                                height = (payload[4] << 24) | (payload[5] << 16) | (payload[6] << 8) | payload[7];
                                if (payload.Length >= 10)
                                {
                                    codecId = payload[8];
                                    fps = payload[9];
                                }
                            }
                            session.IsRemoteScreenSharing = true;
                            session.RemoteVideoKind = payload is { Length: > 10 } && payload[10] == 1 ? "camera" : "screen";
                            session.RemoteScreenCodecId = codecId;
                            session.BeginRemoteMediaEpoch(payload is { Length: >= 15 }
                                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(11)) : 0);
                            QuicPunchLog.Info($"[VOICE CALL] Remote screen share started by {peer.Name} ({width}x{height}, codec: {codecId}, {fps} fps)");
                            OnScreenShareStarted?.Invoke(peer, width, height, codecId, fps);
                            break;

                        case FrameTypeScreenShareStop:
                            session.IsRemoteScreenSharing = false;
                            QuicPunchLog.Info($"[VOICE CALL] Remote screen share stopped by {peer.Name}");
                            OnScreenShareStopped?.Invoke(peer);
                            break;

                        case FrameTypeScreenShareVideo:
                            if (payload != null && payload.Length > 0)
                            {
                                if (OnScreenFrameReceivedAsync != null)
                                {
                                    var handlers = OnScreenFrameReceivedAsync.GetInvocationList()
                                        .Cast<Func<PeerInfo, byte, byte[], Task>>()
                                        .Select(handler => handler(peer, session.RemoteScreenCodecId, payload));
                                    await Task.WhenAll(handlers).ConfigureAwait(false);
                                }
                                OnScreenFrameReceived?.Invoke(peer, session.RemoteScreenCodecId, payload);
                            }
                            break;

                        case FrameTypeScreenShareAudio:
                            if (payload != null && payload.Length > 0)
                            {
                                OnScreenAudioReceived?.Invoke(peer, payload);
                            }
                            break;

                        case FrameTypeScreenShareCapabilities:
                            if (payload != null && payload.Length >= 8)
                            {
                                session.RemoteScreenEncoderCodecMask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
                                session.RemoteScreenDecoderCodecMask = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4));
                                session.RemoteScreenControlVersion = payload.Length >= 9 ? payload[8] : (byte)0;
                                // The initial legacy frame is all zeroes before a browser has measured anything.
                                session.RemoteScreenCapabilitiesReceived = payload.Length >= 13 ||
                                    session.RemoteScreenEncoderCodecMask != 0 || session.RemoteScreenDecoderCodecMask != 0;
                                session.RemoteScreenDecoderHardwarePreferenceMask = payload.Length >= 13
                                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(9, 4)) & session.RemoteScreenDecoderCodecMask : null;
                                OnScreenCodecCapabilitiesReceived?.Invoke(
                                    peer,
                                    session.RemoteScreenEncoderCodecMask,
                                    session.RemoteScreenDecoderCodecMask);
                            }
                            break;

                        case FrameTypeScreenShareRequest:
                            _ = ProcessLegacyScreenRequestAsync(session);
                            break;
                        case FrameTypeScreenControlRequest:
                            if (payload != null) _ = ProcessScreenControlFrameAsync(session, payload);
                            break;
                        case FrameTypeScreenControlReply:
                            if (payload != null) ReceiveScreenControlReply(peer, payload);
                            break;

                        case FrameTypeScreenShareRequestDenied:
                            OnScreenShareRequestDenied?.Invoke(peer);
                            break;

                        default:
                            QuicPunchLog.Info($"[VOICE CALL] Unknown frame type 0x{frameType:X2} from {peer.Name}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (monitorCts.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE CALL] Session ended: {ex.Message}");
            }
            finally
            {
                monitorCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch { }
                try { await telemetryTask.ConfigureAwait(false); } catch { }
                try { await mediaTask.ConfigureAwait(false); } catch { }
                await session.DrainMediaAsync().ConfigureAwait(false);
                session.DetachHandlers();
                bool wasCurrent = ActiveCalls.TryRemove(new KeyValuePair<Guid, VoiceCallSession>(peer.Id, session));
                session.Dispose();
                if (wasCurrent)
                {
                    if (ActiveCalls.IsEmpty || LocalScreenTargetPeerId == peer.Id)
                    {
                        _ = ScreenEncoder.StopAsync();
                        _ = StopScreenShareAsync();
                    }
                    AudioService.RemovePeer(peer.Id);
                    OnCallEnded?.Invoke(peer);
                    if (ActiveCalls.IsEmpty && !AudioService.IsTestLoopback)
                    {
                        AudioService.Stop();
                    }
                    Console.WriteLine($"\n[VOICE CALL] Voice call with {peer.Name} ended.");
                }
            }
        }

        public static async Task SendAudioDatagramAsync(Guid peerId, byte[] audioData)
        {
            if (!ActiveCalls.TryGetValue(peerId, out var call)) return;
            try { await call.SendDatagramAsync(audioData).ConfigureAwait(false); }
            catch (Exception ex) { Console.WriteLine($"[VOICE CALL] Error sending audio datagram: {ex.Message}"); }
        }

        public static async Task BroadcastAudioDatagramAsync(byte[] audioData)
        {
            foreach (var call in ActiveCalls.Values.ToArray())
            {
                if (call.IsOutboundMuted) continue;
                try { await call.SendDatagramAsync(audioData).ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[VOICE CALL] Broadcast error to {call.Peer.Name}: {ex.Message}"); }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            DisposeScreenControl();
            foreach (var session in ActiveCalls.Values.ToArray()) session.Dispose();
            ActiveCalls.Clear();
            StopSystemAudioAsync().GetAwaiter().GetResult();
            ScreenEncoder.Dispose();
            AudioService.Dispose();
        }
    }
}
