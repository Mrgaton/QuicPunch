using System.Net;
using System.Text.Json;
using QuicPunch;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.WebUi.Modules;

internal sealed class SpeedTestApiModule
{
    private readonly QuicPunch.QuicPunch _qcc;
    private readonly SpeedTestHandler _speedTestHandler;
    private readonly WebUiWebSocketHub _hub;
    private readonly CancellationTokenSource _cts;

    public SpeedTestApiModule(QuicPunch.QuicPunch qcc, SpeedTestHandler speedTestHandler, WebUiWebSocketHub hub, CancellationTokenSource cts)
    {
        _qcc = qcc;
        _speedTestHandler = speedTestHandler;
        _hub = hub;
        _cts = cts;

        _speedTestHandler.OnProgress += (peer, prog) =>
        {
            _hub.Broadcast("speedtest_progress", new
            {
                peerId = peer.Id.ToString(),
                peerName = peer.Name ?? "Peer",
                direction = prog.Direction,
                bytesTransferred = prog.BytesTransferred,
                currentMbps = Math.Round(prog.CurrentMbps, 2),
                averageMbps = Math.Round(prog.AverageMbps, 2),
                elapsedSeconds = prog.ElapsedSeconds,
                totalSeconds = prog.TotalSeconds,
                rttMs = prog.RttMs
            });
        };

        _speedTestHandler.OnCompleted += (peer, summary) =>
        {
            _hub.Broadcast("speedtest_completed", new
            {
                peerId = peer.Id.ToString(),
                peerName = peer.Name ?? "Peer",
                mode = summary.Mode,
                uploadMbps = summary.UploadMbps,
                downloadMbps = summary.DownloadMbps,
                totalBytes = summary.TotalBytes,
                durationSeconds = summary.DurationSeconds,
                rttMs = summary.RttMs
            });
            WebUiServer.LogEvent($"[SPEEDTEST] Completed benchmark with {peer.Name}: Up: {summary.UploadMbps} Mbps, Down: {summary.DownloadMbps} Mbps, RTT: {summary.RttMs} ms");
        };

        _speedTestHandler.OnError += (peer, err) =>
        {
            _hub.Broadcast("speedtest_error", new
            {
                peerId = peer.Id.ToString(),
                peerName = peer.Name ?? "Peer",
                error = err
            });
            WebUiServer.LogEvent($"[SPEEDTEST] Error on benchmark with {peer.Name}: {err}");
        };
    }

    public async Task<bool> HandleRequestAsync(string path, HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (path == "/api/speedtest/start" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            int duration = doc.RootElement.TryGetProperty("duration", out var durEl) ? durEl.GetInt32() : 10;
            string modeStr = doc.RootElement.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "both" : "both";

            if (!_qcc.AvailablePeers.TryGetValue(peerId, out var peer) || !_qcc.IsTrustedPeer(peer))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Trusted peer not found or not responsive." }, 404).ConfigureAwait(false);
                return true;
            }

            // Ensure SpeedTest protocol session is established over QUIC
            if (!SpeedTestHandler.ActiveSessions.ContainsKey(peerId))
            {
                WebUiServer.StartProtocolConnection(peer, _speedTestHandler.ProtocolId);
                for (int i = 0; i < 40 && !SpeedTestHandler.ActiveSessions.ContainsKey(peerId); i++)
                {
                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }
            }

            if (!SpeedTestHandler.ActiveSessions.ContainsKey(peerId))
            {
                await WebUiContext.WriteJsonAsync(resp, new { success = false, error = "Could not establish SpeedTest session with peer." }, 409).ConfigureAwait(false);
                return true;
            }

            SpeedTestHandler.TestMode mode = modeStr.ToLowerInvariant() switch
            {
                "upload" or "up" => SpeedTestHandler.TestMode.Upload,
                "download" or "down" => SpeedTestHandler.TestMode.Download,
                _ => SpeedTestHandler.TestMode.Both
            };

            var run = await _speedTestHandler.StartTestAsync(peer, mode, duration, _cts.Token).ConfigureAwait(false);
            _hub.Broadcast("speedtest_started", new
            {
                peerId = peer.Id.ToString(),
                peerName = peer.Name ?? "Peer",
                mode = mode.ToString(),
                duration = duration
            });

            await WebUiContext.WriteJsonAsync(resp, new
            {
                success = true,
                peerId = peer.Id.ToString(),
                mode = mode.ToString(),
                duration = duration
            }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/speedtest/cancel" && req.HttpMethod == "POST")
        {
            using JsonDocument doc = await WebUiContext.ReadJsonAsync(req, path).ConfigureAwait(false);
            Guid peerId = WebUiContext.ParseGuid(doc.RootElement, "peerId");
            await _speedTestHandler.CancelTestAsync(peerId).ConfigureAwait(false);
            await WebUiContext.WriteJsonAsync(resp, new { success = true }).ConfigureAwait(false);
            return true;
        }

        if (path == "/api/speedtest/status" && req.HttpMethod == "GET")
        {
            var active = _speedTestHandler.CurrentRun;
            var last = _speedTestHandler.LastRun;

            await WebUiContext.WriteJsonAsync(resp, new
            {
                isRunning = active != null && !active.IsCompleted,
                active = active != null ? new
                {
                    peerId = active.PeerId.ToString(),
                    peerName = active.PeerName,
                    mode = active.Mode.ToString(),
                    durationSeconds = active.DurationSeconds,
                    elapsedSeconds = Math.Round(active.Stopwatch.Elapsed.TotalSeconds, 1),
                    bytesUploaded = Interlocked.Read(ref active.BytesUploaded),
                    bytesDownloaded = Interlocked.Read(ref active.BytesDownloaded),
                    lastUploadMbps = Math.Round(active.LastUploadMbps, 2),
                    lastDownloadMbps = Math.Round(active.LastDownloadMbps, 2),
                    rttMs = Math.Round(active.LatencyMs, 1)
                } : null,
                lastResult = last != null && last.IsCompleted ? new
                {
                    peerId = last.PeerId.ToString(),
                    peerName = last.PeerName,
                    mode = last.Mode.ToString(),
                    uploadMbps = last.FinalUploadMbps,
                    downloadMbps = last.FinalDownloadMbps,
                    totalBytes = Interlocked.Read(ref last.BytesUploaded) + Interlocked.Read(ref last.BytesDownloaded),
                    durationSeconds = Math.Round(last.Stopwatch.Elapsed.TotalSeconds, 2),
                    rttMs = Math.Round(last.LatencyMs, 1),
                    error = last.ErrorMessage
                } : null
            }).ConfigureAwait(false);
            return true;
        }

        return false;
    }
}
