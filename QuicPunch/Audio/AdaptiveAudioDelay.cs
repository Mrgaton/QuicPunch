namespace QuicPunch.Audio;

/// <summary>One minute of arrival history; all times are local monotonic milliseconds.</summary>
internal sealed class AdaptiveAudioDelay
{
    internal const int HistoryMs = 60_000;
    internal const int MinimumMs = 10, MaximumMs = 100;
    private readonly Queue<(double At, double Transit)> _history = new();
    private readonly List<Episode> _episodes = new();
    private sealed class Episode(double at, double peak)
    {
        public double First = at, Last = at, Peak = peak;
    }
    private double _nextEvaluation, _lastDecrease;
    public int TargetMs { get; private set; } = 20;
    public int HistoryCount => _history.Count;
    public int EpisodeCount => _episodes.Count;

    public void Observe(double now, double transit)
    {
        Expire(now);
        // At most 100 observations/s. Keep the actual one-minute wall-clock window,
        // including late packets, but never let a packet flood grow it without bound.
        if (_history.Count >= 6001) _history.Dequeue();
        // A permanent route-delay change (or clock drift) is not permanent jitter.
        // Track the floor over two seconds; retain event recurrence for a minute.
        double baseline = _history.Where(p => now - p.At <= 2000).Select(p => p.Transit).DefaultIfEmpty(transit).Min();
        double excess = Math.Max(0, transit - baseline);
        _history.Enqueue((now, transit));
        if (excess > 15)
        {
            var last = _episodes.LastOrDefault();
            // A burst of delayed packets is ONE event, not evidence of recurrence.
            if (last == null || now - last.Last > 250) _episodes.Add(new(now, excess));
            else { last.Last = now; last.Peak = Math.Max(last.Peak, excess); }
        }
        Evaluate(now);
    }

    public void Evaluate(double now)
    {
        Expire(now);
        if (now < _nextEvaluation) return;
        _nextEvaluation = now + 250;
        if (_history.Count == 0) return;
        double[] recent = _history.Where(p => now - p.At <= 2000).Select(p => p.Transit).Order().ToArray();
        if (recent.Length == 0) return;
        double baseline = recent[0];
        for (int i = 0; i < recent.Length; i++) recent[i] -= baseline;
        double p95 = recent[(recent.Length - 1) * 95 / 100];
        int wanted = Math.Clamp(RoundUp(p95 + 5), MinimumMs, 20);
        var last = _episodes.LastOrDefault();
        bool sustained = last != null && last.Last - last.First >= 500 && now - last.Last < 1000;
        double recurrenceHold = 5000;
        if (_episodes.Count >= 3)
        {
            var intervals = _episodes.Zip(_episodes.Skip(1), (a, b) => b.First - a.First).Order().ToArray();
            recurrenceHold = Math.Clamp(intervals[intervals.Length / 2] * 2 + 1000, 5000, 30_000);
        }
        bool recurring = _episodes.Count >= 3 && now - last!.Last < recurrenceHold;
        if (sustained) wanted = Math.Max(wanted, RoundUp(p95 + 10));
        if (recurring)
        {
            // Median event severity: one huge outlier must not set everyone's delay.
            double[] peaks = _episodes.Select(e => e.Peak).Order().ToArray();
            wanted = Math.Max(wanted, RoundUp(peaks[(peaks.Length - 1) / 2] + 10));
        }
        wanted = Math.Clamp(wanted, MinimumMs, MaximumMs);
        if (wanted > TargetMs) { TargetMs = wanted; _lastDecrease = now; }
        else if (wanted < TargetMs && now - _lastDecrease >= 1000 &&
                 (last == null || now - last.Last >= (recurring ? recurrenceHold : 5000)))
        {
            TargetMs = Math.Max(wanted, TargetMs - 10);
            _lastDecrease = now;
        }
    }

    private void Expire(double now)
    {
        while (_history.TryPeek(out var first) && now - first.At >= HistoryMs) _history.Dequeue();
        _episodes.RemoveAll(e => now - e.Last >= HistoryMs);
    }
    private static int RoundUp(double ms) => (int)Math.Ceiling(Math.Min(ms, MaximumMs) / 10) * 10;
}
