using System.Collections.Concurrent;
using System.IO.Compression;
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
        private const ushort VoicePacketType = 0x7601;
        private const int MaxVoiceFrameBytes = 64 * 1024;
        private readonly QuicPunch.QuicPunch _qcc;
        private readonly CancellationTokenSource _receiveCts = new();
        private readonly Task _receiveTask;
        private int _disposed;

        public OpusVoiceCodec Codec { get; } = new();

        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
        public ushort PreferredPort => 0;
        public string ProtocolName => "VoiceCall";
        public ushort StreamPriority => (ushort)QuicStreamPriority.Critical;
        public ZstandardCompressionOptions? CompressionOptions => null;

        public event Action<PeerInfo, byte[]>? OnAudioDatagramReceived;
        public event Action<PeerInfo>? OnCallEstablished;
        public event Action<PeerInfo>? OnCallEnded;

        public VoiceCallHandler(QuicPunch.QuicPunch qcc)
        {
            _qcc = qcc ?? throw new ArgumentNullException(nameof(qcc));
            _receiveTask = Task.Run(() => ReceiveAudioLoopAsync(_receiveCts.Token));
        }

        public sealed class VoiceCallSession : IDisposable
        {
            private readonly VoiceCallHandler _owner;
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            private int _disposed;

            public PeerInfo Peer { get; }
            public Stream Stream { get; }
            public QuicConnection Connection { get; }

            public VoiceCallSession(VoiceCallHandler owner, PeerInfo peer, Stream stream, QuicConnection connection)
            {
                _owner = owner;
                Peer = peer;
                Stream = stream;
                Connection = connection;
            }

            public async Task SendDatagramAsync(byte[] audioData, CancellationToken ct = default)
            {
                if (audioData == null || audioData.Length == 0 || audioData.Length > MaxVoiceFrameBytes)
                    return;

                // Priority 1: Native QUIC RFC 9221 datagram channel
                // Zero-head-of-line blocking, hardware DSCP 46 prioritized, TLS encrypted.
                if (Connection.DatagramChannel != null && Connection.DatagramChannel.IsSendEnabled)
                {
                    bool sent = Connection.SendDatagram(audioData);
                    if (sent) return;
                }

                // Fallback: QuicPunch UDP control plane
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _owner._qcc.SendPayloadAsync(Peer, VoicePacketType, audioData).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _sendLock.Dispose();
            }
        }

        public static ConcurrentDictionary<Guid, VoiceCallSession> ActiveCalls { get; } = new();

        public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[VOICE CALL] Call with {peer.Name} ({peer.ActiveEndPoint}) was rejected or failed.");
            return Task.CompletedTask;
        }

        public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
        {
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
                connection.DatagramChannel.OnDatagramReceived += (data) =>
                {
                    if (data.Length == 0 || data.Length > MaxVoiceFrameBytes) return;
                    OnAudioDatagramReceived?.Invoke(peer, data);
                };

                connection.DatagramChannel.OnPeerAddressChanged += (newEp) =>
                {
                    QuicPunchLog.Info($"[VOICE CALL] Peer {peer.Name} roamed to {newEp} during call. Session maintained!");
                    peer.ActiveEndPoint = newEp;
                };
            }

            OnCallEstablished?.Invoke(peer);

            // 3. Lightweight background task to monitor telemetry and adapt Opus bitrate dynamically
            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

            // The reliable stream is deliberately idle. Keeping a read pending gives
            // us a lightweight lifecycle signal without placing live audio on QUIC.
            byte[] closeProbe = new byte[1];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(closeProbe.AsMemory(0, 1), ct).ConfigureAwait(false);
                    if (read == 0) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[VOICE CALL] Session ended: {ex.Message}");
            }
            finally
            {
                monitorCts.Cancel();
                try { await telemetryTask.ConfigureAwait(false); } catch { }
                bool wasCurrent = ActiveCalls.TryRemove(new KeyValuePair<Guid, VoiceCallSession>(peer.Id, session));
                session.Dispose();
                if (wasCurrent)
                {
                    OnCallEnded?.Invoke(peer);
                    Console.WriteLine($"\n[VOICE CALL] Voice call with {peer.Name} ended.");
                }
            }
        }

        private async Task ReceiveAudioLoopAsync(CancellationToken ct)
        {
            var reader = _qcc.GetPacketReader(VoicePacketType);
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        if (item.Payload.Length == 0 || item.Payload.Length > MaxVoiceFrameBytes)
                            continue;
                        if (ActiveCalls.TryGetValue(item.Peer, out var session))
                            OnAudioDatagramReceived?.Invoke(session.Peer, item.Payload);
                        else if (_qcc.AvailablePeers.TryGetValue(item.Peer, out var peer))
                            OnAudioDatagramReceived?.Invoke(peer, item.Payload);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
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
            try { _receiveCts.Cancel(); } catch { }
            try { _receiveTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
            foreach (var session in ActiveCalls.Values.ToArray()) session.Dispose();
            ActiveCalls.Clear();
            _receiveCts.Dispose();
            Codec.Dispose();
        }
    }
}
