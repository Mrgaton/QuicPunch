using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using QuicPunch;
using QuicPunch.Helpers;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicNet.Protocols;

/// <summary>
/// Dedicated high-throughput benchmark and bandwidth testing protocol for QuicPunch over QUIC multiplexer.
/// </summary>
public sealed partial class SpeedTestHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    public const int DefaultChunkBytes = 64 * 1024;
    public const int TorChunkBytes = 16 * 1024;
    private const int MinChunkBytes = 4 * 1024;
    private const int DefaultRateWindowMs = 750;
    private const int TorRateWindowMs = 2_000;
    private const int DefaultSampleIntervalMs = 150;
    private const int TorSampleIntervalMs = 250;
    private const int MaxFrameBytes = 256 * 1024;

    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000006");
    public string ProtocolName => "SpeedTest";
    public ushort StreamPriority => (ushort)QuicStreamPriority.High;

    public enum TestMode : byte
    {
        Both = 0,
        Upload = 1,
        Download = 2,
        All = 3
    }

    public enum FrameType : byte
    {
        Config = 1,
        DataChunk = 2,
        Progress = 3,
        Summary = 4,
        Cancel = 5,
        Complete = 6,
        Probe = 7,
        ProbeReply = 8
    }

    public sealed record ConfigPayload(Guid RunId, string Mode, int DurationSeconds, int ChunkSize, int RateWindowMs);
    public sealed record ProgressPayload(Guid RunId, string Direction, long BytesTransferred, double CurrentMbps, double AverageMbps, double ElapsedSeconds, double TotalSeconds, double RttMs, int SampleWindowMs);
    public sealed record CompletePayload(Guid RunId);
    public sealed record SummaryPayload(
        string Mode,
        double UploadMbps,
        double DownloadMbps,
        long TotalBytes,
        double DurationSeconds,
        double RttMs,
        long BytesUploaded = 0,
        long BytesDownloaded = 0,
        Guid RunId = default,
        QualityMetrics? Quality = null,
        QualityMetrics? Baseline = null,
        SummaryPayload[]? Stages = null);

    public sealed class SpeedTestSession : IDisposable
    {
        public SpeedTestHandler Handler { get; }
        public PeerInfo Peer { get; }
        public Stream Stream { get; }
        public QuicConnection Connection { get; }
        internal QualityProbe Probes { get; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;
        private readonly CancellationTokenSource _lifetime = new();
        private int _pendingReplies;

        internal void QueueProbeReply(ReadOnlyMemory<byte> payload)
        {
            // Keep the reader draining data while the other direction is backpressured.
            // Probe replies may wait for the writer, but may never block the reader.
            if (Interlocked.Increment(ref _pendingReplies) > 32)
            {
                Interlocked.Decrement(ref _pendingReplies);
                return;
            }
            _ = ReplyAsync(payload.ToArray());
        }

        private async Task ReplyAsync(byte[] payload)
        {
            try { await SendFrameAsync(FrameType.ProbeReply, payload, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception) when (Volatile.Read(ref _disposed) != 0) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPEEDTEST] Probe reply failed: {ex.Message}");
                Dispose();
            }
            finally { Interlocked.Decrement(ref _pendingReplies); }
        }

        public SpeedTestSession(SpeedTestHandler handler, PeerInfo peer, Stream stream, QuicConnection connection)
        {
            Handler = handler;
            Peer = peer;
            Stream = stream;
            Connection = connection;
            Probes = new QualityProbe(this);
        }

        public async Task SendFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            if (payload.Length > MaxFrameBytes)
                throw new InvalidDataException("Frame too large for SpeedTest protocol.");

            byte[] header = new byte[5];
            header[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                // A run/probe cancellation may cancel waiting for the lock, but must
                // not interrupt a frame after its header has been written. Cancelling
                // QuicStream.WriteAsync aborts the entire stream, including later phases.
                await Stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
                if (!payload.IsEmpty)
                {
                    await Stream.WriteAsync(payload, deadline.Token).ConfigureAwait(false);
                }
                await Stream.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                // A failed partial frame is fatal: never append another frame to it.
                Dispose();
                throw;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            Probes.Dispose();
            try { Stream.Dispose(); } catch { }
            // In-flight writers still release the semaphore as shutdown unwinds.
        }
    }

    public sealed class ActiveRunState
    {
        public Guid PeerId { get; }
        public Guid RunId { get; }
        public string PeerName { get; }
        public TestMode Mode { get; }
        public TestMode RequestedMode { get; }
        public bool IsInitiator { get; }
        public bool IsTorTransport { get; }
        public int DurationSeconds { get; }
        public int ChunkSize { get; }
        public int RateWindowMs { get; }
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
        public CancellationTokenSource Cts { get; } = new();
        public long BytesUploaded;
        public long BytesDownloaded;
        public long ConfirmedBytesUploaded;
        public long FinalBytesUploaded;
        public long FinalBytesDownloaded;
        public double LastUploadMbps;
        public double LastDownloadMbps;
        public double FinalUploadMbps;
        public double FinalDownloadMbps;
        public double LatencyMs;
        public bool IsCompleted;
        public string? ErrorMessage;
        public TaskCompletionSource<SummaryPayload> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> LocalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SummaryPayload> RemoteSummary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveRunState(
            Guid peerId,
            Guid runId,
            string peerName,
            TestMode mode,
            TestMode requestedMode,
            bool isInitiator,
            bool isTorTransport,
            int durationSeconds,
            int chunkSize,
            int rateWindowMs)
        {
            PeerId = peerId;
            RunId = runId;
            PeerName = peerName;
            Mode = mode;
            RequestedMode = requestedMode;
            IsInitiator = isInitiator;
            IsTorTransport = isTorTransport;
            DurationSeconds = durationSeconds;
            ChunkSize = chunkSize;
            RateWindowMs = rateWindowMs;
        }
    }

    /// <summary>
    /// Calculates goodput over a trailing time window. This avoids presenting a
    /// transport's delivery bursts as alternating peak/zero samples.
    /// </summary>
    internal sealed class SlidingRateMeter
    {
        private readonly int _windowMs;
        private readonly Queue<RateSample> _samples = new();

        private readonly record struct RateSample(long TimestampMs, long UploadedBytes, long DownloadedBytes);

        public SlidingRateMeter(int windowMs)
        {
            _windowMs = Math.Max(100, windowMs);
            _samples.Enqueue(new RateSample(0, 0, 0));
        }

        public (double UploadMbps, double DownloadMbps) AddSample(long timestampMs, long uploadedBytes, long downloadedBytes)
        {
            var current = new RateSample(timestampMs, uploadedBytes, downloadedBytes);
            _samples.Enqueue(current);

            long cutoff = timestampMs - _windowMs;
            while (_samples.Count > 1 && _samples.Peek().TimestampMs < cutoff)
                _samples.Dequeue();

            RateSample baseline = _samples.Peek();
            double seconds = Math.Max(0.001, (timestampMs - baseline.TimestampMs) / 1000.0);
            double uploadMbps = Math.Max(0, ((uploadedBytes - baseline.UploadedBytes) * 8.0) / (seconds * 1_000_000.0));
            double downloadMbps = Math.Max(0, ((downloadedBytes - baseline.DownloadedBytes) * 8.0) / (seconds * 1_000_000.0));
            return (uploadMbps, downloadMbps);
        }
    }

    public static ConcurrentDictionary<Guid, SpeedTestSession> ActiveSessions { get; } = new();
    private readonly ConcurrentDictionary<Guid, ActiveRunState> _activeRuns = new();

    public event Action<PeerInfo, ProgressPayload>? OnProgress;
    public event Action<PeerInfo, SummaryPayload>? OnCompleted;
    public event Action<PeerInfo, string>? OnError;

    public ActiveRunState? CurrentRun => _activeRuns.Values.FirstOrDefault(r => !r.IsCompleted);
    public ActiveRunState? LastRun => _activeRuns.Values.OrderByDescending(r => r.StartedAtUtc).FirstOrDefault();

    public SpeedTestHandler()
    {
    }

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
    {
        Console.WriteLine($"[SPEEDTEST] Session with {peer.Name} was denied or failed.");
        OnError?.Invoke(peer, "Speed test request was denied by peer.");
        if (_activeRuns.TryGetValue(peer.Id, out var run))
        {
            run.IsCompleted = true;
            run.ErrorMessage = "Session denied";
            run.Stopwatch.Stop();
        }
        return Task.CompletedTask;
    }

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        MsQuicTuner.TrySetStreamPriority(stream, QuicStreamPriority.High);
        var session = new SpeedTestSession(this, peer, stream, connection);
        if (ActiveSessions.TryRemove(peer.Id, out var prev))
        {
            try { prev.Stream.Close(); } catch { }
            prev.Dispose();
        }
        ActiveSessions[peer.Id] = session;

        Console.WriteLine($"[SPEEDTEST] Benchmark stream connected with {peer.Name}.");
        byte[] header = new byte[5];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                FrameType type = (FrameType)header[0];
                int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));

                if (length < 0 || length > MaxFrameBytes)
                    throw new InvalidDataException($"Invalid frame length: {length}");

                byte[] payload = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (length > 0)
                    {
                        await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct).ConfigureAwait(false);
                    }

                    await ProcessFrameAsync(session, type, payload.AsMemory(0, length), ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_activeRuns.TryGetValue(peer.Id, out var cancelled)) cancelled.Completion.TrySetCanceled();
        }
        catch (EndOfStreamException ex)
        {
            if (_activeRuns.TryGetValue(peer.Id, out var r) && !r.IsCompleted)
            {
                r.IsCompleted = true;
                if (r.IsInitiator) r.Completion.TrySetException(ex);
                r.ErrorMessage = ex.Message;
                r.Stopwatch.Stop();
                if (!_plans.ContainsKey(peer.Id)) OnError?.Invoke(peer, "Speed test stream closed before the test completed.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SPEEDTEST] Error on stream with {peer.Name}: {ex.Message}");
            if (_activeRuns.TryGetValue(peer.Id, out var r) && !r.IsCompleted)
            {
                r.IsCompleted = true;
                if (r.IsInitiator) r.Completion.TrySetException(ex);
                r.ErrorMessage = ex.Message;
                r.Stopwatch.Stop();
                if (!_plans.ContainsKey(peer.Id)) OnError?.Invoke(peer, ex.Message);
            }
        }
        finally
        {
            if (ActiveSessions.TryRemove(new KeyValuePair<Guid, SpeedTestSession>(peer.Id, session)) &&
                _activeRuns.TryGetValue(peer.Id, out var ended))
            {
                ended.Cts.Cancel();
                ended.Completion.TrySetCanceled();
            }
            session.Dispose();
        }
    }

    private async Task ProcessFrameAsync(SpeedTestSession session, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        switch (type)
        {
            case FrameType.Probe:
                if (payload.Length == 16)
                    session.QueueProbeReply(payload);
                break;
            case FrameType.ProbeReply:
                session.Probes.Complete(payload);
                break;
            case FrameType.Config:
                var config = JsonSerializer.Deserialize<ConfigPayload>(payload.Span, QuicNet.Serialization.SharedJson.Options);
                if (config != null)
                {
                    await HandleRemoteConfigRequestAsync(session, config, ct).ConfigureAwait(false);
                }
                break;

            case FrameType.DataChunk:
                HandleIncomingDataChunk(session, payload.Length);
                break;

            case FrameType.Progress:
                var prog = JsonSerializer.Deserialize<ProgressPayload>(payload.Span, QuicNet.Serialization.SharedJson.Options);
                if (prog != null)
                {
                    if (_activeRuns.TryGetValue(session.Peer.Id, out var progressRun) &&
                        progressRun.IsInitiator && progressRun.RunId == prog.RunId)
                    {
                        if (prog.Direction.Equals("Upload", StringComparison.OrdinalIgnoreCase))
                        {
                            progressRun.LastUploadMbps = prog.CurrentMbps;
                            Interlocked.Exchange(ref progressRun.ConfirmedBytesUploaded, prog.BytesTransferred);
                        }
                        OnProgress?.Invoke(session.Peer, prog);
                    }
                }
                break;

            case FrameType.Summary:
                var summary = JsonSerializer.Deserialize<SummaryPayload>(payload.Span, QuicNet.Serialization.SharedJson.Options);
                if (summary != null)
                {
                    if (_activeRuns.TryGetValue(session.Peer.Id, out var run) &&
                        run.IsInitiator && run.RunId == summary.RunId)
                    {
                        run.RemoteSummary.TrySetResult(summary);
                    }
                }
                break;

            case FrameType.Cancel:
                if (_activeRuns.TryGetValue(session.Peer.Id, out var cancelRun))
                {
                    cancelRun.Cts.Cancel();
                    if (_plans.TryGetValue(session.Peer.Id, out var cancelledPlan)) cancelledPlan.Cts.Cancel();
                    cancelRun.IsCompleted = true;
                    cancelRun.ErrorMessage = "Cancelled by peer";
                    cancelRun.Stopwatch.Stop();
                }
                if (!_plans.ContainsKey(session.Peer.Id)) OnError?.Invoke(session.Peer, "Test cancelled by peer");
                break;

            case FrameType.Complete:
                var complete = JsonSerializer.Deserialize<CompletePayload>(payload.Span, QuicNet.Serialization.SharedJson.Options);
                if (complete != null &&
                    _activeRuns.TryGetValue(session.Peer.Id, out var completedRun) &&
                    !completedRun.IsInitiator && completedRun.RunId == complete.RunId)
                {
                    await completedRun.LocalCompleted.Task.WaitAsync(
                        TimeSpan.FromSeconds(completedRun.DurationSeconds + 5), ct).ConfigureAwait(false);

                    SummaryPayload responderSummary = CreateResponderSummary(completedRun);
                    completedRun.FinalBytesUploaded = responderSummary.BytesUploaded;
                    completedRun.FinalBytesDownloaded = responderSummary.BytesDownloaded;
                    byte[] summaryBytes = JsonSerializer.SerializeToUtf8Bytes(responderSummary, QuicNet.Serialization.SharedJson.Options);
                    await session.SendFrameAsync(FrameType.Summary, summaryBytes, ct).ConfigureAwait(false);
                }
                break;
        }
    }

    private void HandleIncomingDataChunk(SpeedTestSession session, int bytes)
    {
        if (_activeRuns.TryGetValue(session.Peer.Id, out var run))
        {
            Interlocked.Add(ref run.BytesDownloaded, bytes);
        }
    }

    private async Task HandleRemoteConfigRequestAsync(SpeedTestSession session, ConfigPayload config, CancellationToken ct)
    {
        TestMode requestedMode = Enum.TryParse<TestMode>(config.Mode, true, out var m) ? m : TestMode.Both;
        int duration = Math.Clamp(config.DurationSeconds, 2, 120);
        bool isTor = IsTorSession(session);
        int chunkSize = Math.Clamp(config.ChunkSize, MinChunkBytes, DefaultChunkBytes);
        int rateWindowMs = Math.Clamp(config.RateWindowMs, 500, 5_000);

        // From the responder's perspective:
        // If initiator requested "Upload", responder only receives (Download).
        // If initiator requested "Download", responder transmits (Upload).
        // If initiator requested "Both", responder does both.
        TestMode responderMode = requestedMode switch
        {
            TestMode.Upload => TestMode.Download,
            TestMode.Download => TestMode.Upload,
            _ => TestMode.Both
        };

        if (_activeRuns.TryGetValue(session.Peer.Id, out var prevRun))
        {
            try { prevRun.Cts.Cancel(); } catch { }
        }

        var run = new ActiveRunState(
            session.Peer.Id,
            config.RunId,
            session.Peer.Name ?? "Peer",
            responderMode,
            requestedMode,
            isInitiator: false,
            isTor,
            duration,
            chunkSize,
            rateWindowMs);
        _activeRuns[session.Peer.Id] = run;
        _ = Task.Run(() => ExecuteRunAsync(session, run, isInitiator: false, ct));
    }

    private async Task<ActiveRunState> StartStageAsync(PeerInfo peer, TestMode mode, int durationSeconds, CancellationToken ct = default)
    {
        durationSeconds = Math.Clamp(durationSeconds, 2, 120);
        if (!ActiveSessions.TryGetValue(peer.Id, out var session))
        {
            throw new InvalidOperationException($"No active SpeedTest session with {peer.Name}. Must establish connection first.");
        }

        if (_activeRuns.TryGetValue(peer.Id, out var prevRun))
        {
            try { prevRun.Cts.Cancel(); } catch { }
        }

        bool isTor = IsTorSession(session);
        int chunkSize = isTor ? TorChunkBytes : DefaultChunkBytes;
        int rateWindowMs = isTor ? TorRateWindowMs : DefaultRateWindowMs;
        var run = new ActiveRunState(
            peer.Id,
            Guid.NewGuid(),
            peer.Name ?? "Peer",
            mode,
            mode,
            isInitiator: true,
            isTor,
            durationSeconds,
            chunkSize,
            rateWindowMs);
        _activeRuns[peer.Id] = run;

        // Send Config to remote peer
        var config = new ConfigPayload(run.RunId, mode.ToString(), durationSeconds, chunkSize, rateWindowMs);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(config, QuicNet.Serialization.SharedJson.Options);
        try { await session.SendFrameAsync(FrameType.Config, payload, ct).ConfigureAwait(false); }
        catch
        {
            run.IsCompleted = true;
            run.Cts.Cancel();
            run.Stopwatch.Stop();
            run.Completion.TrySetCanceled();
            // A simultaneous stream read failure may have faulted this completion
            // before configuration failed. No caller received the run to await it.
            _ = run.Completion.Task.Exception;
            throw;
        }

        _ = Task.Run(() => ExecuteRunAsync(session, run, isInitiator: true, run.Cts.Token));
        return run;
    }

    public async Task CancelTestAsync(Guid peerId)
    {
        if (_plans.TryGetValue(peerId, out var plan))
        {
            try { plan.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (_activeRuns.TryGetValue(peerId, out var run))
        {
            try { run.Cts.Cancel(); } catch { }
            run.IsCompleted = true;
            run.ErrorMessage = "Cancelled by local node";
            run.Stopwatch.Stop();
        }
        if (ActiveSessions.TryGetValue(peerId, out var session))
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await session.SendFrameAsync(FrameType.Cancel, Array.Empty<byte>(), deadline.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ExecuteRunAsync(SpeedTestSession session, ActiveRunState run, bool isInitiator, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Cts.Token);
        var token = linkedCts.Token;

        run.Stopwatch.Restart();
        using var qualityCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<QualityMetrics>? qualityTask = isInitiator ? session.Probes.MeasureAsync(int.MaxValue, qualityCts.Token) : null;

        bool shouldUpload = run.Mode is TestMode.Upload or TestMode.Both;
        bool shouldDownload = run.Mode is TestMode.Download or TestMode.Both;

        Task? uploadTask = null;
        if (shouldUpload)
        {
            uploadTask = Task.Run(() => RunUploadPumpAsync(session, run, token), token);
        }

        // Monitoring and Progress reporting loop
        var sw = run.Stopwatch;
        var rateMeter = new SlidingRateMeter(run.RateWindowMs);
        int sampleIntervalMs = run.IsTorTransport ? TorSampleIntervalMs : DefaultSampleIntervalMs;

        try
        {
            while (!token.IsCancellationRequested && sw.Elapsed.TotalSeconds < run.DurationSeconds)
            {
                double remainingMs = (run.DurationSeconds - sw.Elapsed.TotalSeconds) * 1000.0;
                int delayMs = Math.Max(1, (int)Math.Min(sampleIntervalMs, remainingMs));
                await Task.Delay(delayMs, token).ConfigureAwait(false);

                long currentUpload = Interlocked.Read(ref run.BytesUploaded);
                long currentDownload = Interlocked.Read(ref run.BytesDownloaded);
                long currentTickMs = sw.ElapsedMilliseconds;
                (double curUpMbps, double curDownMbps) = rateMeter.AddSample(currentTickMs, currentUpload, currentDownload);
                double avgUpMbps = Math.Max(0, (currentUpload * 8.0) / (sw.Elapsed.TotalSeconds * 1_000_000.0));
                double avgDownMbps = Math.Max(0, (currentDownload * 8.0) / (sw.Elapsed.TotalSeconds * 1_000_000.0));

                if (isInitiator)
                {
                    if (shouldDownload)
                        run.LastDownloadMbps = curDownMbps;
                }
                else if (shouldDownload)
                {
                    // The responder's download is the initiator's upload. This is
                    // receiver-side goodput, rather than bytes merely queued by the sender.
                    run.LastUploadMbps = curDownMbps;
                }

                double rtt = session.Connection.TryGetTelemetry(out var telemetry) ? telemetry.RttMs : 0;
                if (telemetry != null) session.Peer.LastTelemetry = telemetry;
                run.LatencyMs = rtt;

                if (isInitiator && shouldDownload)
                {
                    var prog = new ProgressPayload(
                        run.RunId,
                        "Download",
                        currentDownload,
                        curDownMbps,
                        avgDownMbps,
                        Math.Round(sw.Elapsed.TotalSeconds, 1),
                        run.DurationSeconds,
                        Math.Round(rtt, 1),
                        run.RateWindowMs
                    );
                    OnProgress?.Invoke(session.Peer, prog);
                }
                else if (!isInitiator && shouldDownload)
                {
                    var prog = new ProgressPayload(
                        run.RunId,
                        "Upload",
                        currentDownload,
                        curDownMbps,
                        avgDownMbps,
                        Math.Round(sw.Elapsed.TotalSeconds, 1),
                        run.DurationSeconds,
                        Math.Round(rtt, 1),
                        run.RateWindowMs);
                    byte[] progBytes = JsonSerializer.SerializeToUtf8Bytes(prog, QuicNet.Serialization.SharedJson.Options);
                    await session.SendFrameAsync(FrameType.Progress, progBytes, token).ConfigureAwait(false);
                }
            }

            if (uploadTask != null)
            {
                await uploadTask.ConfigureAwait(false);
            }

            // Final calculations
            double totalSeconds = Math.Max(0.01, run.DurationSeconds);
            run.FinalUploadMbps = Math.Round((Interlocked.Read(ref run.BytesUploaded) * 8.0) / (totalSeconds * 1_000_000.0), 2);
            run.FinalDownloadMbps = Math.Round((Interlocked.Read(ref run.BytesDownloaded) * 8.0) / (totalSeconds * 1_000_000.0), 2);
            run.LocalCompleted.TrySetResult(true);

            if (isInitiator)
            {
                // This marker is ordered after every upload data frame. The responder
                // can therefore report bytes actually received, and its reply is in
                // turn ordered after every download data frame.
                byte[] completeBytes = JsonSerializer.SerializeToUtf8Bytes(new CompletePayload(run.RunId), QuicNet.Serialization.SharedJson.Options);
                await session.SendFrameAsync(FrameType.Complete, completeBytes, token).ConfigureAwait(false);
                SummaryPayload remote = await run.RemoteSummary.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);

                long uploaded = run.RequestedMode is TestMode.Upload or TestMode.Both ? remote.BytesUploaded : 0;
                long downloaded = run.RequestedMode is TestMode.Download or TestMode.Both
                    ? Interlocked.Read(ref run.BytesDownloaded)
                    : 0;

                run.FinalUploadMbps = run.RequestedMode is TestMode.Upload or TestMode.Both
                    ? remote.UploadMbps
                    : 0;
                run.FinalDownloadMbps = run.RequestedMode is TestMode.Download or TestMode.Both
                    ? Math.Round((downloaded * 8.0) / (totalSeconds * 1_000_000.0), 2)
                    : 0;
                run.FinalBytesUploaded = uploaded;
                run.FinalBytesDownloaded = downloaded;
                run.LatencyMs = remote.RttMs > 0 ? remote.RttMs : run.LatencyMs;
                run.IsCompleted = true;
                run.Stopwatch.Stop();

                var summary = new SummaryPayload(
                    run.RequestedMode.ToString(),
                    run.FinalUploadMbps,
                    run.FinalDownloadMbps,
                    uploaded + downloaded,
                    Math.Round(totalSeconds, 2),
                    Math.Round(run.LatencyMs, 1),
                    uploaded,
                    downloaded,
                    run.RunId);
                qualityCts.Cancel();
                QualityMetrics? quality = qualityTask == null ? null : await qualityTask.ConfigureAwait(false);
                run.Completion.TrySetResult(summary with { Quality = quality, RttMs = quality?.RttAverageMs ?? 0 });
            }
            else
            {
                run.IsCompleted = true;
                run.Stopwatch.Stop();
            }
        }
        catch (OperationCanceledException)
        {
            run.IsCompleted = true;
            run.Completion.TrySetCanceled();
            run.ErrorMessage = "Test cancelled";
            run.Stopwatch.Stop();
        }
        catch (Exception ex)
        {
            run.IsCompleted = true;
            if (run.IsInitiator) run.Completion.TrySetException(ex);
            run.ErrorMessage = ex.Message;
            run.Stopwatch.Stop();
            if (!isInitiator) OnError?.Invoke(session.Peer, ex.Message);
        }
        finally
        {
            // The reader/progress loop may fail before reaching the normal upload
            // await. Stop and join every child before this run releases its resources.
            linkedCts.Cancel();
            if (uploadTask != null) { try { await uploadTask.ConfigureAwait(false); } catch { } }
            qualityCts.Cancel();
            if (qualityTask != null) { try { await qualityTask.ConfigureAwait(false); } catch { } }
            run.LocalCompleted.TrySetResult(true);
        }
    }

    private static SummaryPayload CreateResponderSummary(ActiveRunState run)
    {
        double seconds = Math.Max(0.01, run.DurationSeconds);
        long uploadedByInitiator = run.RequestedMode is TestMode.Upload or TestMode.Both
            ? Interlocked.Read(ref run.BytesDownloaded)
            : 0;
        long downloadedByInitiator = run.RequestedMode is TestMode.Download or TestMode.Both
            ? Interlocked.Read(ref run.BytesUploaded)
            : 0;

        double uploadMbps = Math.Round((uploadedByInitiator * 8.0) / (seconds * 1_000_000.0), 2);
        double downloadMbps = Math.Round((downloadedByInitiator * 8.0) / (seconds * 1_000_000.0), 2);

        return new SummaryPayload(
            run.RequestedMode.ToString(),
            uploadMbps,
            downloadMbps,
            uploadedByInitiator + downloadedByInitiator,
            Math.Round(seconds, 2),
            Math.Round(run.LatencyMs, 1),
            uploadedByInitiator,
            downloadedByInitiator,
            run.RunId);
    }

    private async Task RunUploadPumpAsync(SpeedTestSession session, ActiveRunState run, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(run.ChunkSize);
        new Random(42).NextBytes(buffer.AsSpan(0, run.ChunkSize)); // fill with dummy pseudo-random data once

        try
        {
            while (!ct.IsCancellationRequested && run.Stopwatch.Elapsed.TotalSeconds < run.DurationSeconds)
            {
                await session.SendFrameAsync(FrameType.DataChunk, buffer.AsMemory(0, run.ChunkSize), ct).ConfigureAwait(false);
                Interlocked.Add(ref run.BytesUploaded, run.ChunkSize);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsTorSession(SpeedTestSession session) => session.Connection.IsTor;

    public void Dispose()
    {
        foreach (var plan in _plans.Values) plan.Cts.Cancel();
        foreach (var run in _activeRuns.Values)
        {
            try { run.Cts.Cancel(); } catch { }
            run.Cts.Dispose();
        }
        foreach (var s in ActiveSessions.Values)
        {
            s.Dispose();
        }
        ActiveSessions.Clear();
    }
}
