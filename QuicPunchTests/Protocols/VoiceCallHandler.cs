using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using QuicPunch;
using QuicPunch.Helpers;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicPunchTests.Protocols
{
    /// <summary>
    /// Multi-peer voice sessions use QUIC only for call lifecycle/authorization.
    /// Real-time Opus VoIP (or PCM fallback) audio frames travel over QuicPunch's
    /// authenticated encrypted UDP data plane so a lost frame cannot head-of-line block every newer frame.
    /// </summary>
    internal sealed class VoiceCallHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
    {
        private const int MaxVoiceFrameBytes = 64 * 1024;
        private readonly QuicPunch.QuicPunch _qcc;
        private int _disposed;

        public OpusVoiceCodec Codec { get; } = new();
        public QuicPunch.Audio.NativeAudioService AudioService { get; } = new();
        public static VoiceCallHandler? Instance { get; private set; }

        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
        public ushort PreferredPort => 0;
        public string ProtocolName => "VoiceCall";
        public ushort StreamPriority => (ushort)QuicStreamPriority.Critical;
        public ZstandardCompressionOptions? CompressionOptions => null;

        public const byte FrameTypePing = 0x01;
        public const byte FrameTypePong = 0x02;
        public const byte FrameTypeScreenShareStart = 0x20;
        public const byte FrameTypeScreenShareStop = 0x21;
        public const byte FrameTypeScreenShareVideo = 0x22;
        public const byte FrameTypeScreenShareAudio = 0x23;

        public event Action<PeerInfo, byte[]>? OnAudioDatagramReceived;
        public event Action<PeerInfo>? OnCallEstablished;
        public event Action<PeerInfo>? OnCallEnded;
        public event Action<PeerInfo, int, int, byte, byte>? OnScreenShareStarted;
        public event Action<PeerInfo>? OnScreenShareStopped;
        public event Action<PeerInfo, byte, byte[]>? OnScreenFrameReceived;
        public event Action<PeerInfo, byte[]>? OnScreenAudioReceived;

        public VoiceCallHandler(QuicPunch.QuicPunch qcc)
        {
            _qcc = qcc ?? throw new ArgumentNullException(nameof(qcc));
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
                AudioService.Start();
            };

            OnCallEnded += peer =>
            {
                AudioService.RemovePeer(peer.Id);
                if (ActiveCalls.IsEmpty && !AudioService.IsTestLoopback)
                {
                    AudioService.Stop();
                }
            };
        }

        public sealed class VoiceCallSession : IDisposable
        {
            private readonly VoiceCallHandler _owner;
            private readonly CancellationTokenSource _sessionCts = new();
            private readonly SemaphoreSlim _streamWriteLock = new(1, 1);
            private Action? _detachAction;
            private int _disposed;

            public PeerInfo Peer { get; }
            public Stream Stream { get; }
            public QuicConnection Connection { get; }
            public CancellationToken CancellationToken => _sessionCts.Token;
            public bool IsRemoteScreenSharing { get; internal set; }
            public byte RemoteScreenCodecId { get; internal set; } = 1;
            public bool IsOutboundMuted { get; set; }
            public bool IsScreenShareAllowed { get; set; } = true;

            public VoiceCallSession(VoiceCallHandler owner, PeerInfo peer, Stream stream, QuicConnection connection)
            {
                _owner = owner;
                Peer = peer;
                Stream = stream;
                Connection = connection;
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
                // Zero-head-of-line blocking, hardware DSCP 46 prioritized, TLS encrypted.
                if (Connection.DatagramChannel != null)
                {
                    Connection.SendDatagram(audioData);
                }
                return Task.CompletedTask;
            }

            public async Task SendFrameAsync(byte frameType, ReadOnlyMemory<byte> payload = default, CancellationToken ct = default)
            {
                if (_disposed != 0) return;
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token, ct);
                try
                {
                    await _streamWriteLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    try
                    {
                        byte[] header = new byte[5];
                        header[0] = frameType;
                        int length = payload.Length;
                        header[1] = (byte)(length >> 24);
                        header[2] = (byte)(length >> 16);
                        header[3] = (byte)(length >> 8);
                        header[4] = (byte)length;

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
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    QuicPunchLog.Info($"[VOICE CALL] Stream write failed to {Peer.Name}: {ex.Message}");
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _sessionCts.Cancel(); } catch { }
                DetachHandlers();
                try { Stream.Close(); } catch { }
                try { Stream.Dispose(); } catch { }
                try { _ = Connection.DisposeAsync(); } catch { }
                try { _sessionCts.Dispose(); } catch { }
                try { _streamWriteLock.Dispose(); } catch { }
            }
        }

        public static ConcurrentDictionary<Guid, VoiceCallSession> ActiveCalls { get; } = new();

        public static bool IsLocalScreenSharing { get; private set; }
        public static byte LocalScreenCodecId { get; private set; } = 1; // 1 = H.264, 2 = VP8, 3 = VP9, 4 = AV1, 0 = JPEG
        public static int LocalScreenWidth { get; private set; } = 1280;
        public static int LocalScreenHeight { get; private set; } = 720;
        public static byte LocalScreenFps { get; private set; } = 30;

        public static async Task StartScreenShareAsync(int width = 1280, int height = 720, byte codecId = 1, byte fps = 30)
        {
            IsLocalScreenSharing = true;
            LocalScreenWidth = width;
            LocalScreenHeight = height;
            LocalScreenCodecId = codecId;
            LocalScreenFps = fps;

            byte[] startPayload = BuildScreenStartPayload(width, height, codecId, fps);
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                if (!session.IsScreenShareAllowed) continue;
                await session.SendFrameAsync(FrameTypeScreenShareStart, startPayload).ConfigureAwait(false);
            }
        }

        private static byte[] BuildScreenStartPayload(int width, int height, byte codecId, byte fps)
        {
            byte[] startPayload = new byte[10];
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
            return startPayload;
        }

        public static async Task NotifySessionScreenShareStartAsync(VoiceCallSession session)
        {
            if (IsLocalScreenSharing && session != null && session.IsScreenShareAllowed)
            {
                byte[] startPayload = BuildScreenStartPayload(LocalScreenWidth, LocalScreenHeight, LocalScreenCodecId, LocalScreenFps);
                await session.SendFrameAsync(FrameTypeScreenShareStart, startPayload).ConfigureAwait(false);
            }
        }

        public static async Task StopScreenShareAsync()
        {
            IsLocalScreenSharing = false;
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                await session.SendFrameAsync(FrameTypeScreenShareStop).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenFrameAsync(byte[] videoChunk)
        {
            if (videoChunk == null || videoChunk.Length == 0 || !IsLocalScreenSharing) return;
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                if (!session.IsScreenShareAllowed) continue;
                await session.SendFrameAsync(FrameTypeScreenShareVideo, videoChunk).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenFrameToPeerAsync(Guid peerId, byte[] videoChunk)
        {
            if (videoChunk == null || videoChunk.Length == 0) return;
            if (ActiveCalls.TryGetValue(peerId, out var session) && session.IsScreenShareAllowed)
            {
                await session.SendFrameAsync(FrameTypeScreenShareVideo, videoChunk).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenAudioAsync(byte[] audioChunk)
        {
            if (audioChunk == null || audioChunk.Length == 0 || !IsLocalScreenSharing) return;
            foreach (var session in ActiveCalls.Values.ToArray())
            {
                if (!session.IsScreenShareAllowed) continue;
                await session.SendFrameAsync(FrameTypeScreenShareAudio, audioChunk).ConfigureAwait(false);
            }
        }

        public static async Task SendScreenAudioToPeerAsync(Guid peerId, byte[] audioChunk)
        {
            if (audioChunk == null || audioChunk.Length == 0) return;
            if (ActiveCalls.TryGetValue(peerId, out var session) && session.IsScreenShareAllowed)
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
                else if (allowed && IsLocalScreenSharing)
                {
                    _ = NotifySessionScreenShareStartAsync(session);
                }
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
            if (ActiveCalls.TryRemove(peerId, out var session))
            {
                session.Dispose();
                AudioService.RemovePeer(peerId);
                OnCallEnded?.Invoke(session.Peer);
                if (ActiveCalls.IsEmpty && !AudioService.IsTestLoopback)
                {
                    AudioService.Stop();
                }
                Console.WriteLine($"\n[VOICE CALL] Voice call with {session.Peer.Name} hung up locally.");
                return true;
            }
            return false;
        }

        public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[VOICE CALL] Call with {peer.Name} ({peer.ActiveEndPoint}) was rejected or failed.");
            return Task.CompletedTask;
        }

        public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
        {
            if (ActiveCalls.TryGetValue(peer.Id, out var existingSession) && !existingSession.CancellationToken.IsCancellationRequested)
            {
                QuicPunchLog.Info($"[VOICE CALL] Call with {peer.Name} is already active. Preserving current call.");
                return;
            }

            Console.WriteLine($"\n--- P2P VOICE CALL ESTABLISHED with {peer.Name} ({peer.ActiveEndPoint}) ---");

            // 1. Mark socket with QoS DSCP 46 (Expedited Forwarding for voice/audio) and stream priority Critical
            stream.SetQuicPriority(QuicStreamPriority.Critical);
            bool dscpSet = connection.TrySetDscp(QuicDscpPriority.Voice);
            if (dscpSet)
            {
                QuicPunchLog.Info($"[VOICE CALL] Applied QoS DSCP 46 (Voice EF) and Critical stream priority to peer {peer.Name}");
            }

            var session = new VoiceCallSession(this, peer, stream, connection);
            if (ActiveCalls.TryRemove(peer.Id, out var previousSession))
            {
                try { previousSession.Stream.Close(); } catch { }
                try { await previousSession.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                previousSession.Dispose();
            }

            ActiveCalls[peer.Id] = session;

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
                    peer.ActiveEndPoint = newEp;
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
            if (IsLocalScreenSharing)
            {
                _ = NotifySessionScreenShareStartAsync(session);
            }

            // 3. Lightweight background task to monitor telemetry and adapt Opus bitrate dynamically
            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.CancellationToken);
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
                            if (telem.PacketLossRatio > 0.05 || telem.RttMs > 120)
                            {
                                // Under congestion/packet loss: lower bitrate and let FEC protect the audio
                                int newBitrate = Math.Max(16000, Codec.Bitrate - 8000);
                                if (newBitrate != Codec.Bitrate)
                                {
                                    Codec.Bitrate = newBitrate;
                                    QuicPunchLog.Info($"[VOICE CALL] Adaptive QoS: Congestion detected (Loss: {telem.PacketLossRatio:P1}, RTT: {telem.RttMs:F1}ms). Reduced bitrate to {newBitrate} bps.");
                                }
                            }
                            else if (telem.PacketLossRatio == 0 && telem.RttMs < 35 && Codec.Bitrate < 64000)
                            {
                                // Pristine network conditions: bump bitrate
                                Codec.Bitrate = Math.Min(64000, Codec.Bitrate + 8000);
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            }, monitorCts.Token);

            // Lightweight heartbeat to ensure bidirectional stream aliveness and refresh NAT firewall pinholes
            var heartbeatTask = Task.Run(async () =>
            {
                int consecutiveFailures = 0;
                while (!monitorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, monitorCts.Token).ConfigureAwait(false);
                        await session.SendFrameAsync(FrameTypePing, ReadOnlyMemory<byte>.Empty, monitorCts.Token).ConfigureAwait(false);
                        consecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        if (++consecutiveFailures >= 3)
                        {
                            QuicPunchLog.Info($"[VOICE CALL] Stream heartbeat failed 3 times for {peer.Name}, terminating session.");
                            break;
                        }
                    }
                }
            }, monitorCts.Token);

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
                            session.RemoteScreenCodecId = codecId;
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
                                OnScreenFrameReceived?.Invoke(peer, session.RemoteScreenCodecId, payload);
                            }
                            break;

                        case FrameTypeScreenShareAudio:
                            if (payload != null && payload.Length > 0)
                            {
                                OnScreenAudioReceived?.Invoke(peer, payload);
                            }
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
                session.DetachHandlers();
                bool wasCurrent = ActiveCalls.TryRemove(new KeyValuePair<Guid, VoiceCallSession>(peer.Id, session));
                session.Dispose();
                if (wasCurrent)
                {
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

        public async Task SendPcmAsOpusAsync(Guid peerId, short[] pcmSamples, int frameSize = OpusVoiceCodec.DefaultFrameSize)
        {
            byte[] opusPacket = Codec.Encode(pcmSamples, frameSize);
            await SendAudioDatagramAsync(peerId, opusPacket).ConfigureAwait(false);
        }

        public async Task BroadcastPcmAsOpusAsync(short[] pcmSamples, int frameSize = OpusVoiceCodec.DefaultFrameSize)
        {
            byte[] opusPacket = Codec.Encode(pcmSamples, frameSize);
            await BroadcastAudioDatagramAsync(opusPacket).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var session in ActiveCalls.Values.ToArray()) session.Dispose();
            ActiveCalls.Clear();
            AudioService.Dispose();
            Codec.Dispose();
        }
    }
}
