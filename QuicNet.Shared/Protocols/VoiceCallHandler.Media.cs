using System.Buffers.Binary;
using System.Collections.Concurrent;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicNet.Protocols;

internal sealed partial class VoiceCallHandler
{
    // Version 2 peers send each media unit on its own cancellable stream. Control
    // never sits behind media bytes, and retransmission cannot retain a stale unit.
    internal const int MaxMediaUnitBytes = 2 * 1024 * 1024;
    internal static readonly TimeSpan MediaLifetime = TimeSpan.FromMilliseconds(250);

    public sealed partial class VoiceCallSession
    {
        private readonly object _mediaGate = new();
        private readonly ConcurrentDictionary<long, Task> _mediaTasks = new();
        private long _mediaTaskId;
        private int _mediaBytes, _mediaCount;
        private uint _videoSequence, _audioSequence, _receivedVideo, _receivedAudio;
        private uint _sendMediaEpoch;
        internal uint RemoteMediaEpoch;
        private bool _sendNeedsKey = true, _receiveNeedsKey = true;

        internal byte[] BeginMediaEpoch(ReadOnlyMemory<byte> start)
        {
            byte[] payload = new byte[15];
            start.Span[..Math.Min(start.Length, 11)].CopyTo(payload);
            lock (_mediaGate)
            {
                if (++_sendMediaEpoch == 0) ++_sendMediaEpoch;
                _videoSequence = _audioSequence = 0; _sendNeedsKey = true;
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(11), _sendMediaEpoch);
            }
            return payload;
        }

        internal void BeginRemoteMediaEpoch(uint epoch)
        {
            lock (_mediaGate)
            {
                _receivedVideo = _receivedAudio = 0; _receiveNeedsKey = true;
                Volatile.Write(ref RemoteMediaEpoch, epoch);
            }
        }

        internal Task SendMediaAsync(byte type, ReadOnlyMemory<byte> payload)
        {
            bool video = type == FrameTypeScreenShareVideo;
            bool key = video && payload.Length > 0 && (payload.Span[0] & 1) != 0;
            uint sequence, epoch;
            lock (_mediaGate)
            {
                if (_disposed != 0 || payload.Length == 0) return Task.CompletedTask;
                if (payload.Length > MaxMediaUnitBytes || _mediaCount >= 8 || _mediaBytes + payload.Length > 4 * 1024 * 1024)
                { if (video) _sendNeedsKey = true; return Task.CompletedTask; }
                if (video && _sendNeedsKey && !key) return Task.CompletedTask;
                if (key) _sendNeedsKey = false;
                sequence = video ? ++_videoSequence : ++_audioSequence;
                epoch = _sendMediaEpoch;
                ++_mediaCount; _mediaBytes += payload.Length;
            }
            byte[] copy = payload.ToArray();
            long id = Interlocked.Increment(ref _mediaTaskId);
            var job = SendMediaUnitAsync(type, sequence, epoch, copy);
            _mediaTasks[id] = job;
            _ = job.ContinueWith(_ => _mediaTasks.TryRemove(id, out var ignored), TaskScheduler.Default);
            return Task.CompletedTask;
        }

        private async Task SendMediaUnitAsync(byte type, uint sequence, uint epoch, byte[] payload)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            deadline.CancelAfter(MediaLifetime);
            QuicStream? media = null;
            try
            {
                media = await Connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token).ConfigureAwait(false);
                media.SetQuicPriority(type == FrameTypeScreenShareAudio ? QuicStreamPriority.Critical : QuicStreamPriority.Normal);
                byte[] header = new byte[13]; header[0] = type;
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), sequence);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), payload.Length);
                BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), epoch);
                await media.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                await media.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                media.CompleteWrites();
                await media.WritesClosed.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (type == FrameTypeScreenShareVideo) lock (_mediaGate) { if (epoch == _sendMediaEpoch) _sendNeedsKey = true; }
                try { media?.Abort(QuicAbortDirection.Write, 0x4D454449); } catch { }
                if (ex is not (OperationCanceledException or IOException or System.Net.Quic.QuicException or ObjectDisposedException))
                    QuicPunchLog.Info($"[MEDIA] Send failed: {ex.Message}");
            }
            finally
            {
                if (media != null) try { await media.DisposeAsync().ConfigureAwait(false); } catch { }
                lock (_mediaGate) { --_mediaCount; _mediaBytes -= payload.Length; }
            }
        }

        internal bool AcceptMediaSequence(byte type, uint sequence, ReadOnlySpan<byte> payload)
        {
            lock (_mediaGate)
            {
                if (sequence == 0) return false;
                if (type == FrameTypeScreenShareAudio)
                {
                    if (sequence <= _receivedAudio) return false;
                    _receivedAudio = sequence; return true;
                }
                if (type != FrameTypeScreenShareVideo || sequence <= _receivedVideo) return false;
                bool key = payload.Length > 0 && (payload[0] & 1) != 0;
                if (sequence != _receivedVideo + 1) _receiveNeedsKey = true;
                _receivedVideo = sequence;
                if (_receiveNeedsKey && !key) return false;
                _receiveNeedsKey = false; return true;
            }
        }

        internal async Task DrainMediaAsync()
        { try { await Task.WhenAll(_mediaTasks.Values).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { } }
    }

    private async Task ReceiveMediaAsync(VoiceCallSession session, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(4);
        using var delivery = new SemaphoreSlim(1);
        var workers = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = await session.Connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                if (!await slots.WaitAsync(0, ct).ConfigureAwait(false))
                { stream.Abort(QuicAbortDirection.Read, 0x4D454449); await stream.DisposeAsync(); continue; }
                workers.RemoveAll(t => t.IsCompleted);
                workers.Add(ReadUnitAsync(stream));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Net.Quic.QuicException or ObjectDisposedException or NotSupportedException) { }
        finally { await Task.WhenAll(workers).ConfigureAwait(false); }

        async Task ReadUnitAsync(QuicStream stream)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(MediaLifetime);
            bool delivering = false;
            try
            {
                if (stream.Type != QuicStreamType.Unidirectional) throw new InvalidDataException("Invalid media stream type.");
                byte[] header = new byte[13];
                await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));
                if (header[0] is not (FrameTypeScreenShareVideo or FrameTypeScreenShareAudio) || length is <= 0 or > MaxMediaUnitBytes)
                    throw new InvalidDataException("Invalid media unit.");
                byte[] payload = new byte[length];
                await stream.ReadExactlyAsync(payload, deadline.Token).ConfigureAwait(false);
                // A reset/partial unit is never delivered to the decoder.
                byte[] end = new byte[1];
                if (await stream.ReadAsync(end, deadline.Token).ConfigureAwait(false) != 0) throw new InvalidDataException("Trailing media data.");
                uint epoch = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(9));
                if (epoch == 0) return;
                // Independent streams may overtake Start on the control stream.
                // Wait only within this unit's lifetime; old epochs never reach a new decoder.
                while (Volatile.Read(ref session.RemoteMediaEpoch) != epoch)
                    await Task.Delay(5, deadline.Token).ConfigureAwait(false);
                await delivery.WaitAsync(deadline.Token).ConfigureAwait(false);
                delivering = true;
                if (!session.IsRemoteScreenSharing || Volatile.Read(ref session.RemoteMediaEpoch) != epoch) return;
                if (!session.AcceptMediaSequence(header[0], BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1)), payload)) return;
                if (header[0] == FrameTypeScreenShareAudio) OnScreenAudioReceived?.Invoke(session.Peer, payload);
                else
                {
                    if (OnScreenFrameReceivedAsync != null)
                    {
                        var consumers = Task.WhenAll(OnScreenFrameReceivedAsync.GetInvocationList().Cast<Func<PeerInfo, byte, byte[], Task>>()
                            .Select(h => h(session.Peer, session.RemoteScreenCodecId, payload)));
                        try { await consumers.WaitAsync(deadline.Token).ConfigureAwait(false); }
                        catch when (!consumers.IsCompleted)
                        {
                            // An observer has no cancellation API. Keep its delivery
                            // slot until it finishes, rather than accumulating abandoned
                            // decoder tasks whenever the media deadline expires.
                            delivering = false;
                            _ = consumers.ContinueWith(t =>
                            {
                                _ = t.Exception;
                                try { delivery.Release(); } catch (ObjectDisposedException) { }
                            }, TaskScheduler.Default);
                            throw;
                        }
                    }
                    OnScreenFrameReceived?.Invoke(session.Peer, session.RemoteScreenCodecId, payload);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or System.Net.Quic.QuicException or ObjectDisposedException) { }
            catch (Exception ex) { QuicPunchLog.Info($"[MEDIA] Receiver failed: {ex.Message}"); }
            finally
            {
                if (delivering) delivery.Release();
                try { stream.Abort(QuicAbortDirection.Read, 0x4D454449); } catch { }
                try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                slots.Release();
            }
        }
    }
}
