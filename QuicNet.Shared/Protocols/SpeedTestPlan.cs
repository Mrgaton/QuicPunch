using System.Collections.Concurrent;
using System.Diagnostics;
using QuicPunch;

namespace QuicNet.Protocols;

public sealed partial class SpeedTestHandler
{
    public sealed class TestPlanState
    {
        public Guid TestId { get; } = Guid.NewGuid();
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public Guid PeerId { get; init; }
        public string PeerName { get; init; } = "Peer";
        public TestMode Mode { get; init; }
        public int DurationSeconds { get; init; }
        public string Phase { get; internal set; } = "Latencia en reposo";
        public CancellationTokenSource Cts { get; } = new();
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public QualityMetrics? Baseline { get; internal set; }
        public SummaryPayload[] Results { get; internal set; } = [];
        public SummaryPayload? Summary { get; internal set; }
        public bool IsCompleted { get; internal set; }
        public string? Error { get; internal set; }
    }

    private readonly ConcurrentDictionary<Guid, TestPlanState> _plans = new();
    private readonly ConcurrentQueue<TestPlanState> _history = new();
    public TestPlanState[] History => _history.Reverse().ToArray();
    public TestPlanState? FindPlan(Guid peerId) => History.FirstOrDefault(p => p.PeerId == peerId);
    public void ClearHistory()
    {
        _history.Clear();
        foreach (var plan in _plans.Values) _history.Enqueue(plan);
    }
    public TestPlanState? CurrentPlan => _plans.Values.FirstOrDefault(p => !p.IsCompleted);
    public TestPlanState? LastPlan { get; private set; }
    public event Action<PeerInfo, SummaryPayload>? OnPhaseCompleted;
    public event Action<PeerInfo, string>? OnPhaseStarted;

    public async Task<ActiveRunState> StartTestAsync(PeerInfo peer, TestMode mode, int durationSeconds, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!ActiveSessions.TryGetValue(peer.Id, out var session))
            throw new InvalidOperationException("No active SpeedTest session with peer.");
        var plan = new TestPlanState { PeerId = peer.Id, PeerName = peer.Name ?? "Peer", Mode = mode,
            DurationSeconds = Math.Clamp(durationSeconds, 2, 120) };
        if (!_plans.TryAdd(peer.Id, plan))
        {
            plan.Cts.Dispose();
            throw new InvalidOperationException("Ya hay una prueba en curso con este contacto.");
        }
        LastPlan = plan;
        _history.Enqueue(plan);
        while (_history.Count > 50) _history.TryDequeue(out _);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, plan.Cts.Token);
            OnPhaseStarted?.Invoke(peer, plan.Phase);
            plan.Baseline = await session.Probes.MeasureAsync(10, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var modes = mode == TestMode.All ? new[] { TestMode.Download, TestMode.Upload, TestMode.Both } : new[] { mode };
            plan.Phase = modes[0].ToString();
            OnPhaseStarted?.Invoke(peer, plan.Phase);
            var first = await StartStageAsync(peer, modes[0], plan.DurationSeconds, linked.Token).ConfigureAwait(false);
            _ = RunPlanAsync(peer, plan, first, modes, ct);
            return first;
        }
        catch (Exception ex)
        {
            plan.Error = ex is OperationCanceledException ? "Prueba cancelada" : ex.Message;
            plan.IsCompleted = true;
            plan.Clock.Stop();
            _plans.TryRemove(new KeyValuePair<Guid, TestPlanState>(peer.Id, plan));
            plan.Cts.Dispose();
            throw;
        }
    }

    private async Task RunPlanAsync(PeerInfo peer, TestPlanState plan, ActiveRunState first, TestMode[] modes, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, plan.Cts.Token);
        var token = linked.Token;
        try
        {
            var run = first;
            for (int i = 0; i < modes.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (i > 0)
                {
                    plan.Phase = modes[i].ToString();
                    OnPhaseStarted?.Invoke(peer, plan.Phase);
                    run = await StartStageAsync(peer, modes[i], plan.DurationSeconds, token).ConfigureAwait(false);
                }
                var result = await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(plan.DurationSeconds + 20), token).ConfigureAwait(false);
                plan.Results = [.. plan.Results, result];
                OnPhaseCompleted?.Invoke(peer, result);
            }
            var final = plan.Results[^1];
            plan.Summary = final with { Mode = plan.Mode.ToString(), TotalBytes = plan.Results.Sum(r => r.TotalBytes),
                DurationSeconds = Math.Round(plan.Clock.Elapsed.TotalSeconds, 2), Baseline = plan.Baseline, Stages = plan.Results };
            plan.IsCompleted = true;
            plan.Clock.Stop();
            _plans.TryRemove(new KeyValuePair<Guid, TestPlanState>(peer.Id, plan));
            OnCompleted?.Invoke(peer, plan.Summary);
        }
        catch (Exception ex)
        {
            plan.Error = ex is OperationCanceledException ? "Prueba cancelada" : ex.Message;
            plan.IsCompleted = true;
            plan.Clock.Stop();
            if (_activeRuns.TryGetValue(peer.Id, out var run)) run.Cts.Cancel();
            OnError?.Invoke(peer, plan.Error);
        }
        finally
        {
            _plans.TryRemove(new KeyValuePair<Guid, TestPlanState>(peer.Id, plan));
            plan.Cts.Dispose();
        }
    }
}
