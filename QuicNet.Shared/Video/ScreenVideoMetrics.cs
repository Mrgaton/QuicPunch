using System.Diagnostics;

namespace QuicNet.Video;

internal sealed record VideoTimingSummary(int samples, double p50Ms, double p95Ms);

internal sealed record ScreenCaptureStatistics(string mode, double? captureFps, double encodedFps, double submittedFps, long encodedBytes,
    int? targetBitrate, int? targetFps,
    long queueOverflow, long queueExpired, long awaitingKey, VideoTimingSummary capture, VideoTimingSummary composition,
    VideoTimingSummary encode, VideoTimingSummary sendQueue);

internal sealed record ScreenReceiveStatistics(double decodedFps, double presentedFps, long queueOverflow,
    long queueExpired, long awaitingKey, long presentationDropped, VideoTimingSummary receiveQueue,
    VideoTimingSummary decode, VideoTimingSummary presentation);

// Callers serialize writes/snapshots. Keep the last 256 samples per stage.
internal sealed class RecentVideoTimings
{
    private readonly long[] _samples = new long[256];
    private int _next, _count;

    internal void Add(long microseconds)
    {
        _samples[_next] = Math.Max(0, microseconds);
        _next = (_next + 1) % _samples.Length;
        _count = Math.Min(_count + 1, _samples.Length);
    }

    internal VideoTimingSummary Summary()
    {
        if (_count == 0) return new(0, 0, 0);
        var sorted = _samples.AsSpan(0, _count).ToArray();
        Array.Sort(sorted);
        return new(_count, sorted[(int)Math.Ceiling(_count * 0.50) - 1] / 1000.0, sorted[(int)Math.Ceiling(_count * 0.95) - 1] / 1000.0);
    }
}

internal sealed class ScreenCaptureMetrics(string mode)
{
    private readonly object _gate = new();
    private readonly RecentVideoTimings _capture = new(), _compose = new(), _encode = new(), _sendQueue = new();
    private long _captured, _encoded, _submitted, _encodedBytes, _overflow, _expired, _awaitingKey, _sampleCaptured;
    private int? _targetBitrate, _targetFps;
    private long _sampleTime = Stopwatch.GetTimestamp(), _lastFrame, _sampleEncoded, _sampleSubmitted;
    private double _captureFps, _encodedFps, _submittedFps;

    internal void Capture(long captureUs, int bitrate, int fps)
    {
        lock (_gate)
        {
            ++_captured;
            _capture.Add(captureUs);
            _targetBitrate = bitrate;
            _targetFps = fps;
        }
    }

    internal void Encode(EncodedScreenFrame frame, int? encodedBytes = null)
    {
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            ++_encoded;
            _encodedBytes += encodedBytes ?? frame.Data.Length;
            _lastFrame = now;
            if (frame.Timings is { } timing)
            {
                _compose.Add(timing.ComposeUs);
                _encode.Add(timing.EncodeUs);
            }
            UpdateRates(now);
        }
    }

    internal void Submit(TimeSpan queueAge)
    {
        lock (_gate)
        {
            ++_submitted;
            _sendQueue.Add((long)queueAge.TotalMicroseconds);
        }
    }

    internal void Overflow(int count) { lock (_gate) _overflow += count; }
    internal void Expired() { lock (_gate) ++_expired; }
    internal void AwaitingKey() { lock (_gate) ++_awaitingKey; }

    internal ScreenCaptureStatistics Snapshot()
    {
        lock (_gate)
        {
            bool idle = _lastFrame == 0 || Stopwatch.GetElapsedTime(_lastFrame).TotalSeconds >= 2;
            return new(mode, _captured == 0 ? null : idle ? 0 : _captureFps, idle ? 0 : _encodedFps,
                idle ? 0 : _submittedFps, _encodedBytes, _targetBitrate, _targetFps,
                _overflow, _expired, _awaitingKey, _capture.Summary(), _compose.Summary(), _encode.Summary(), _sendQueue.Summary());
        }
    }

    private void UpdateRates(long now)
    {
        double elapsed = Stopwatch.GetElapsedTime(_sampleTime, now).TotalSeconds;
        if (elapsed < 1) return;
        _captureFps = (_captured - _sampleCaptured) / elapsed;
        _encodedFps = (_encoded - _sampleEncoded) / elapsed;
        _submittedFps = (_submitted - _sampleSubmitted) / elapsed;
        (_sampleTime, _sampleEncoded, _sampleSubmitted) = (now, _encoded, _submitted);
        _sampleCaptured = _captured;
    }
}
