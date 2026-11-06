using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using QuicPunch;
using QuicNet.Protocols;

namespace QuicNet.WebUi.Modules;

internal sealed class SpeedTestApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly SpeedTestHandler _speedTestHandler;
    private readonly WebUiWebSocketHub _hub;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _pendingStarts = new();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static global::QuicNet.Serialization.SpeedTestApiModuleMessage1 ResultView(
        PeerInfo peer, SpeedTestHandler.SummaryPayload summary, SpeedTestHandler.TestPlanState? plan = null) =>
        new(peer.Id.ToString(), plan?.PeerName ?? peer.Name ?? "Peer", summary.Mode,
            summary.UploadMbps, summary.DownloadMbps, summary.TotalBytes, summary.BytesUploaded,
            summary.BytesDownloaded, summary.DurationSeconds, summary.RttMs,
            JsonSerializer.SerializeToElement(summary.Quality, WebUiContext.JsonOptionsFor(WebJson)),
            JsonSerializer.SerializeToElement(plan?.Baseline ?? summary.Baseline, WebUiContext.JsonOptionsFor(WebJson)),
            JsonSerializer.SerializeToElement(summary.Stages, WebUiContext.JsonOptionsFor(WebJson)),
            plan?.TestId.ToString() ?? summary.RunId.ToString(),
            plan == null || (plan.IsCompleted && plan.Error == null) ? "completed" : plan.IsCompleted ? "interrupted" : "running",
            plan?.Error, plan?.StartedAtUtc);

    private static global::QuicNet.Serialization.SpeedTestApiModuleMessage1 PlanView(SpeedTestHandler.TestPlanState plan)
    {
        var last = plan.Results.LastOrDefault();
        var summary = plan.Summary ?? new SpeedTestHandler.SummaryPayload(plan.Mode.ToString(),
            last?.UploadMbps ?? 0, last?.DownloadMbps ?? 0, plan.Results.Sum(r => r.TotalBytes),
            Math.Round(plan.Clock.Elapsed.TotalSeconds, 2), last?.RttMs ?? 0,
            plan.Results.Sum(r => r.BytesUploaded), plan.Results.Sum(r => r.BytesDownloaded),
            plan.TestId, last?.Quality, plan.Baseline, plan.Results);
        // History survives peer disconnection; do not depend on AvailablePeers.
        return new(plan.PeerId.ToString(), plan.PeerName, summary.Mode, summary.UploadMbps,
            summary.DownloadMbps, summary.TotalBytes, summary.BytesUploaded, summary.BytesDownloaded,
            summary.DurationSeconds, summary.RttMs,
            JsonSerializer.SerializeToElement(summary.Quality, WebUiContext.JsonOptionsFor(WebJson)),
            JsonSerializer.SerializeToElement(plan.Baseline, WebUiContext.JsonOptionsFor(WebJson)),
            JsonSerializer.SerializeToElement(plan.Results, WebUiContext.JsonOptionsFor(WebJson)),
            plan.TestId.ToString(), plan.IsCompleted ? plan.Error == null ? "completed" : "interrupted" : "running",
            plan.Error, plan.StartedAtUtc);
    }

    public SpeedTestApiModule(QuicPunch.QuicPunch qcc, SpeedTestHandler speedTestHandler, WebUiWebSocketHub hub, CancellationTokenSource cts)
    {
        _qcc = qcc;
        _speedTestHandler = speedTestHandler;
        _hub = hub;
        _cts = cts;
        _speedTestHandler.OnPhaseStarted += (peer, phase) =>
            _hub.Broadcast("speedtest_phase", new global::QuicNet.Serialization.SpeedTestApiModuleMessage2(peer.Id.ToString(), phase, JsonSerializer.SerializeToElement(_speedTestHandler.FindPlan(peer.Id)?.Baseline, WebUiContext.JsonOptionsFor(WebJson))));
        _speedTestHandler.OnPhaseCompleted += (peer, summary) =>
            _hub.Broadcast("speedtest_phase_completed", ResultView(peer, summary, _speedTestHandler.FindPlan(peer.Id)));
        _speedTestHandler.OnProgress += (peer, prog) =>
            _hub.Broadcast("speedtest_progress", new global::QuicNet.Serialization.SpeedTestApiModuleMessage3(peer.Id.ToString(), peer.Name ?? "Peer", prog.Direction, prog.BytesTransferred, Math.Round(prog.CurrentMbps, 2), Math.Round(prog.AverageMbps, 2), prog.ElapsedSeconds, prog.TotalSeconds, prog.RttMs, prog.SampleWindowMs));
        _speedTestHandler.OnCompleted += (peer, summary) =>
        {
            _hub.Broadcast("speedtest_completed", ResultView(peer, summary, _speedTestHandler.FindPlan(peer.Id)));
            WebUiServer.LogEvent($"[SPEEDTEST] {summary.Mode} completed with {peer.Name}: Up {summary.UploadMbps}, Down {summary.DownloadMbps} Mbps");
        };
        _speedTestHandler.OnError += (peer, err) =>
            _hub.Broadcast("speedtest_error", new global::QuicNet.Serialization.SpeedTestApiModuleMessage4(peer.Id.ToString(), err, _speedTestHandler.FindPlan(peer.Id) is { } failed ? PlanView(failed) : null));
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/speedtest/start" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            int duration = Math.Clamp(doc.RootElement.TryGetProperty("duration", out var d) ? d.GetInt32() : 10, 2, 120);
            string modeText = doc.RootElement.TryGetProperty("mode", out var m) ? m.GetString() ?? "all" : "all";
            SpeedTestHandler.TestMode? mode = modeText.ToLowerInvariant() switch
            {
                "upload" or "up" => SpeedTestHandler.TestMode.Upload,
                "download" or "down" => SpeedTestHandler.TestMode.Download,
                "both" => SpeedTestHandler.TestMode.Both,
                "all" => SpeedTestHandler.TestMode.All,
                _ => null
            };
            if (!mode.HasValue)
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage5(false, "Modo de prueba no válido."), 400).ConfigureAwait(false);
                return true;
            }
            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer) || !_qcc.IsTrustedPeer(peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage5(false, "No se encuentra el contacto de confianza."), 404).ConfigureAwait(false);
                return true;
            }
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            if (!_pendingStarts.TryAdd(peerId, pending))
            {
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage5(false, "Ya se está iniciando una prueba con este contacto."), 409).ConfigureAwait(false);
                return true;
            }
            try
            {
                if (!SpeedTestHandler.ActiveSessions.ContainsKey(peerId))
                {
                    WebUiServer.StartProtocolConnection(peer, _speedTestHandler.ProtocolId);
                    for (int i = 0; i < 200 && !SpeedTestHandler.ActiveSessions.ContainsKey(peerId); i++)
                        await Task.Delay(100, pending.Token).ConfigureAwait(false);
                }
                if (!SpeedTestHandler.ActiveSessions.ContainsKey(peerId))
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage5(false, "No se pudo establecer la sesión de prueba."), 409).ConfigureAwait(false);
                    return true;
                }
                pending.Token.ThrowIfCancellationRequested();
                try
                {
                    var run = await _speedTestHandler.StartTestAsync(peer, mode.Value, duration, _cts.Token).ConfigureAwait(false);
                    pending.Token.ThrowIfCancellationRequested();
                    // Phase events already carry the current phase; this event must not reset its results.
                    _hub.Broadcast("speedtest_started", new global::QuicNet.Serialization.SpeedTestApiModuleMessage6(peer.Id.ToString(), peer.Name ?? "Peer", mode.Value.ToString(), duration, run.IsTorTransport, run.RateWindowMs));
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage7(true, peerId, mode.Value.ToString(), duration)).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage5(false, ex.Message), 409).ConfigureAwait(false);
                }
                return true;
            }
            catch (OperationCanceledException) when (pending.IsCancellationRequested)
            {
                await _speedTestHandler.CancelTestAsync(peerId).ConfigureAwait(false);
                await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage5(false, "Prueba cancelada."), 409).ConfigureAwait(false);
                return true;
            }
            finally { _pendingStarts.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(peerId, pending)); }
        }

        if (path == "/api/speedtest/cancel" && req.HttpMethod == "POST")
        {
            using var doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            if (_pendingStarts.TryGetValue(peerId, out var pending))
            {
                try { pending.Cancel(); } catch (ObjectDisposedException) { }
            }
            await _speedTestHandler.CancelTestAsync(peerId).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage8(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/speedtest/history/clear" && req.HttpMethod == "POST")
        {
            _speedTestHandler.ClearHistory();
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage8(true)).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/speedtest/status" && req.HttpMethod == "GET")
        {
            var plan = _speedTestHandler.CurrentPlan;
            var active = _speedTestHandler.CurrentRun;
            if (active?.PeerId != plan?.PeerId) active = null;
            var last = _speedTestHandler.LastPlan;
            await WebUiContext.WriteJsonAsync(resp, new global::QuicNet.Serialization.SpeedTestApiModuleMessage9(plan != null, plan == null ? null : new global::QuicNet.Serialization.SpeedTestApiModuleMessage10(plan.PeerId.ToString(), plan.PeerName, plan.Mode.ToString(), plan.Phase, plan.DurationSeconds, active?.Stopwatch.Elapsed.TotalSeconds ?? 0, active?.LastUploadMbps ?? 0, active?.LastDownloadMbps ?? 0, active == null ? 0 : Interlocked.Read(ref active.ConfirmedBytesUploaded), active == null ? 0 : Interlocked.Read(ref active.BytesDownloaded), active?.LatencyMs ?? 0, active?.RateWindowMs ?? 0, JsonSerializer.SerializeToElement(plan.Baseline, global::QuicNet.WebUi.WebUiContext.JsonOptionsFor(WebJson)), JsonSerializer.SerializeToElement(plan.Results, global::QuicNet.WebUi.WebUiContext.JsonOptionsFor(WebJson))), last is { IsCompleted: true } ? PlanView(last) : null, last?.Error, _speedTestHandler.History.Select(PlanView).ToArray())).ConfigureAwait(false);
            return true;
        }
        return false;
    }
}
