namespace QuicNet.Video;

// A shared encode must fit the slowest active recipient. Use local delivery
// counters, a short confirmation window, and a slower recovery than reduction.
internal sealed class ScreenBandwidthController
{
    private readonly Dictionary<Guid, (long Sent, long Lost)> _previous = new();
    private readonly int _requestedBitrate, _requestedFps, _minBitrate, _minFps;
    private int _bitrate, _fps, _badIntervals, _goodIntervals;

    internal ScreenBandwidthController(int requestedBitrate, int requestedFps)
    {
        _requestedBitrate = _bitrate = requestedBitrate;
        _requestedFps = _fps = requestedFps;
        _minBitrate = Math.Max(500_000, requestedBitrate / 3);
        _minFps = Math.Min(15, requestedFps);
    }

    internal (int Bitrate, int Fps) Observe(IEnumerable<(Guid Peer, long Sent, long Lost)> recipients)
    {
        var current = recipients.ToDictionary(value => value.Peer, value => (value.Sent, value.Lost));
        bool congested = false, sampleAvailable = false;
        foreach (var (peer, count) in current)
        {
            if (!_previous.TryGetValue(peer, out var previous)) continue;
            long sent = count.Sent - previous.Sent, lost = count.Lost - previous.Lost;
            if (sent < 0 || lost < 0) continue;
            if (sent + lost < 8 && lost < 3) continue;
            sampleAvailable = true;
            congested |= lost >= 3 && lost * 100.0 / (sent + lost) >= 8;
        }
        _previous.Clear();
        foreach (var item in current) _previous.Add(item.Key, item.Value);

        if (congested)
        {
            _goodIntervals = 0;
            if (++_badIntervals >= 2)
            {
                _badIntervals = 0;
                if (_bitrate > _minBitrate) _bitrate = Math.Max(_minBitrate, _bitrate * 3 / 4);
                else _fps = Math.Max(_minFps, _fps * 3 / 4);
            }
        }
        else
        {
            _badIntervals = 0;
            if (sampleAvailable && ++_goodIntervals >= 3)
            {
                _goodIntervals = 0;
                if (_fps < _requestedFps) _fps = Math.Min(_requestedFps, _fps + Math.Max(2, _requestedFps / 10));
                else _bitrate = Math.Min(_requestedBitrate, _bitrate + Math.Max(100_000, _requestedBitrate / 10));
            }
        }
        return (_bitrate, _fps);
    }
}
