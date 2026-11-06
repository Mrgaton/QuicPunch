using System.Collections.Concurrent;

namespace QuicPunch.Datagrams;

/// <summary>
/// Thread-safe, bounded frame reassembler for multi-datagram video, screen sharing, and camera frames.
/// Includes a sliding time-to-live (TTL) window to discard dropped frames in real-time without stalling.
/// </summary>
public sealed class DatagramFrameReassembler
{
    private sealed class FrameBuffer
    {
        public ushort FrameId;
        public byte Category;
        public bool IsKeyFrame;
        public ushort TotalFragments;
        public int ReceivedFragmentsCount;
        public byte[]?[] Fragments;
        public int TotalPayloadLength;
        public long CreatedTimestamp;

        public FrameBuffer(ushort frameId, byte category, bool isKeyFrame, ushort totalFragments)
        {
            FrameId = frameId;
            Category = category;
            IsKeyFrame = isKeyFrame;
            TotalFragments = totalFragments;
            ReceivedFragmentsCount = 0;
            Fragments = new byte[totalFragments][];
            TotalPayloadLength = 0;
            CreatedTimestamp = Environment.TickCount64;
        }
    }

    private readonly ConcurrentDictionary<(byte Category, ushort Id), FrameBuffer> _pendingFrames = new();
    private readonly object _gate = new();
    private const int MaxPendingFrames = 32;
    private const int MaxFrameBytes = 2 * 1024 * 1024;
    private readonly long _frameTtlMs;
    private long _lastCleanupMs;

    public DatagramFrameReassembler(TimeSpan? frameTtl = null)
    {
        _frameTtlMs = (long)(frameTtl?.TotalMilliseconds ?? 250);
        _lastCleanupMs = Environment.TickCount64;
    }

    /// <summary>
    /// Ingests a datagram fragment. If this completes the entire frame, returns <c>true</c> along with
    /// the reassembled contiguous payload, category, and keyframe indicator.
    /// </summary>
    public bool TryProcessFragment(
        in QuicDatagramEnvelope envelope,
        out byte category,
        out ReadOnlyMemory<byte> framePayload,
        out bool isKeyFrame)
    {
        category = envelope.Category;
        isKeyFrame = envelope.IsKeyFrame;
        framePayload = default;

        if (!envelope.IsFragmented)
        {
            // Single packet frame
            framePayload = envelope.Payload.ToArray();
            return true;
        }

        ushort frameId = envelope.FrameId;
        ushort fragIndex = envelope.FragmentIndex;
        ushort totalFrags = envelope.TotalFragments;
        byte cat = envelope.Category;
        bool key = envelope.IsKeyFrame;

        if (totalFrags == 0 || fragIndex >= totalFrags || totalFrags > 1024 || envelope.Payload.Length > 65535)
        {
            return false; // Malformed fragment index
        }

        lock (_gate)
        {
            MaybeCleanupStaleFrames();
            var frameKey = (cat, frameId);
            if (!_pendingFrames.TryGetValue(frameKey, out var fb))
            {
                if (_pendingFrames.Count >= MaxPendingFrames) return false;
                fb = new FrameBuffer(frameId, cat, key, totalFrags);
                _pendingFrames[frameKey] = fb;
            }
            if (fb.TotalFragments != totalFrags || fb.IsKeyFrame != key ||
                fb.TotalPayloadLength + envelope.Payload.Length > MaxFrameBytes)
            {
                _pendingFrames.TryRemove(frameKey, out _);
                return false;
            }
            if (fb.Fragments[fragIndex] == null)
            {
                byte[] fragData = envelope.Payload.ToArray();
                fb.Fragments[fragIndex] = fragData;
                fb.TotalPayloadLength += fragData.Length;
                fb.ReceivedFragmentsCount++;

                if (fb.ReceivedFragmentsCount == fb.TotalFragments)
                {
                    _pendingFrames.TryRemove(frameKey, out _);

                    byte[] complete = new byte[fb.TotalPayloadLength];
                    int offset = 0;
                    for (int i = 0; i < fb.TotalFragments; i++)
                    {
                        var part = fb.Fragments[i];
                        if (part != null)
                        {
                            Buffer.BlockCopy(part, 0, complete, offset, part.Length);
                            offset += part.Length;
                        }
                    }

                    category = fb.Category;
                    isKeyFrame = fb.IsKeyFrame;
                    framePayload = complete;
                    return true;
                }
            }
        }

        return false;
    }

    private void MaybeCleanupStaleFrames()
    {
        long now = Environment.TickCount64;
        long last = Volatile.Read(ref _lastCleanupMs);
        if (now - last < 100)
            return;

        if (Interlocked.CompareExchange(ref _lastCleanupMs, now, last) == last)
        {
            foreach (var kvp in _pendingFrames)
            {
                if (now - kvp.Value.CreatedTimestamp > _frameTtlMs)
                {
                    _pendingFrames.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public void Clear() { lock (_gate) _pendingFrames.Clear(); }
}
