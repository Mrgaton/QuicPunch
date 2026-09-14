using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using QuicPunch;
using QuicPunch.Helpers;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicPunchTests.Protocols;

/// <summary>
/// Dedicated high-throughput benchmark and bandwidth testing protocol for QuicPunch over QUIC multiplexer.
/// </summary>
public sealed class SpeedTestHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    public const int DefaultChunkBytes = 64 * 1024;
    private const int MaxFrameBytes = 256 * 1024;

    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000006");
    public ushort PreferredPort => 0;
    public string ProtocolName => "SpeedTest";
    public ushort StreamPriority => (ushort)QuicStreamPriority.High;
    public ZstandardCompressionOptions? CompressionOptions => null;

    public enum TestMode : byte
    {
        Both = 0,
        Upload = 1,
        Download = 2
    }

    public enum FrameType : byte
    {
        Config = 1,
        DataChunk = 2,
        Progress = 3,
        Summary = 4,
        Cancel = 5
    }

    public sealed record ConfigPayload(string Mode, int DurationSeconds, int ChunkSize);
    public sealed record ProgressPayload(string Direction, long BytesTransferred, double CurrentMbps, double AverageMbps, double ElapsedSeconds, double TotalSeconds, double RttMs);
    public sealed record SummaryPayload(string Mode, double UploadMbps, double DownloadMbps, long TotalBytes, double DurationSeconds, double RttMs);

    public sealed class SpeedTestSession : IDisposable
    {
        public SpeedTestHandler Handler { get; }
        public PeerInfo Peer { get; }
        public Stream Stream { get; }
        public QuicConnection Connection { get; }
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public SpeedTestSession(SpeedTestHandler handler, PeerInfo peer, Stream stream, QuicConnection connection)
        {
            Handler = handler;
            Peer = peer;
            Stream = stream;
            Connection = connection;
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
                await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                if (!payload.IsEmpty)
                {
                    await Stream.WriteAsync(payload, ct).ConfigureAwait(false);
                }
                await Stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Stream.Dispose(); } catch { }
            _writeLock.Dispose();
        }
    }

    public sealed class ActiveRunState
    {
        public Guid PeerId { get; }
        public string PeerName { get; }
        public TestMode Mode { get; }
        public int DurationSeconds { get; }
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
        public CancellationTokenSource Cts { get; } = new();
        public long BytesUploaded;
        public long BytesDownloaded;
        public double LastUploadMbps;
        public double LastDownloadMbps;
        public double FinalUploadMbps;
        public double FinalDownloadMbps;
        public double LatencyMs;
        public bool IsCompleted;
        public string? ErrorMessage;

        public ActiveRunState(Guid peerId, string peerName, TestMode mode, int durationSeconds)
        {
            PeerId = peerId;
            PeerName = peerName;
            Mode = mode;
            DurationSeconds = durationSeconds;
        }
    }

    public static ConcurrentDictionary<Guid, SpeedTestSession> ActiveSessions { get; } = new();
    private readonly ConcurrentDictionary<Guid, ActiveRunState> _activeRuns = new();

    public event Action<PeerInfo, ProgressPayload>? OnProgress;
    public event Action<PeerInfo, SummaryPayload>? OnCompleted;
    public event Action<PeerInfo, string>? OnError;

    public ActiveRunState? CurrentRun => _activeRuns.Values.FirstOrDefault(r => !r.IsCompleted);
    public ActiveRunState? LastRun => _activeRuns.Values.OrderByDescending(r => r.Stopwatch.Elapsed).FirstOrDefault();

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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[SPEEDTEST] Error on stream with {peer.Name}: {ex.Message}");
            if (_activeRuns.TryGetValue(peer.Id, out var r) && !r.IsCompleted)
            {
                r.IsCompleted = true;
                r.ErrorMessage = ex.Message;
                OnError?.Invoke(peer, ex.Message);
            }
        }
        finally
        {
            ActiveSessions.TryRemove(new KeyValuePair<Guid, SpeedTestSession>(peer.Id, session));
            session.Dispose();
        }
    }

    private async Task ProcessFrameAsync(SpeedTestSession session, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        switch (type)
        {
            case FrameType.Config:
                var config = JsonSerializer.Deserialize<ConfigPayload>(payload.Span);
                if (config != null)
                {
                    await HandleRemoteConfigRequestAsync(session, config, ct).ConfigureAwait(false);
                }
                break;

            case FrameType.DataChunk:
                HandleIncomingDataChunk(session, payload.Length);
                break;

            case FrameType.Progress:
                var prog = JsonSerializer.Deserialize<ProgressPayload>(payload.Span);
                if (prog != null)
                {
                    OnProgress?.Invoke(session.Peer, prog);
                }
                break;

            case FrameType.Summary:
                var summary = JsonSerializer.Deserialize<SummaryPayload>(payload.Span);
                if (summary != null)
                {
                    if (_activeRuns.TryGetValue(session.Peer.Id, out var run))
                    {
                        run.IsCompleted = true;
                        run.FinalUploadMbps = summary.UploadMbps > 0 ? summary.UploadMbps : run.FinalUploadMbps;
                        run.FinalDownloadMbps = summary.DownloadMbps > 0 ? summary.DownloadMbps : run.FinalDownloadMbps;
                        run.LatencyMs = summary.RttMs;
                    }
                    OnCompleted?.Invoke(session.Peer, summary);
                }
                break;

            case FrameType.Cancel:
                if (_activeRuns.TryGetValue(session.Peer.Id, out var cancelRun))
                {
                    cancelRun.Cts.Cancel();
                    cancelRun.IsCompleted = true;
                    cancelRun.ErrorMessage = "Cancelled by peer";
                }
                OnError?.Invoke(session.Peer, "Test cancelled by peer");
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
            try { prevRun.Cts.Dispose(); } catch { }
        }

        var run = new ActiveRunState(session.Peer.Id, session.Peer.Name ?? "Peer", responderMode, duration);
        _activeRuns[session.Peer.Id] = run;
        _ = Task.Run(() => ExecuteRunAsync(session, run, isInitiator: false, ct));
    }

    public async Task<ActiveRunState> StartTestAsync(PeerInfo peer, TestMode mode, int durationSeconds, CancellationToken ct = default)
    {
        durationSeconds = Math.Clamp(durationSeconds, 2, 120);
        if (!ActiveSessions.TryGetValue(peer.Id, out var session))
        {
            throw new InvalidOperationException($"No active SpeedTest session with {peer.Name}. Must establish connection first.");
        }

        if (_activeRuns.TryGetValue(peer.Id, out var prevRun))
        {
            try { prevRun.Cts.Cancel(); } catch { }
            try { prevRun.Cts.Dispose(); } catch { }
        }

        var run = new ActiveRunState(peer.Id, peer.Name ?? "Peer", mode, durationSeconds);
        _activeRuns[peer.Id] = run;

        // Send Config to remote peer
        var config = new ConfigPayload(mode.ToString(), durationSeconds, DefaultChunkBytes);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(config);
        await session.SendFrameAsync(FrameType.Config, payload, ct).ConfigureAwait(false);

        _ = Task.Run(() => ExecuteRunAsync(session, run, isInitiator: true, run.Cts.Token));
        return run;
    }

    public async Task CancelTestAsync(Guid peerId)
    {
        if (_activeRuns.TryGetValue(peerId, out var run))
        {
            try { run.Cts.Cancel(); } catch { }
            run.IsCompleted = true;
            run.ErrorMessage = "Cancelled by local node";
        }
        if (ActiveSessions.TryGetValue(peerId, out var session))
        {
            try
            {
                await session.SendFrameAsync(FrameType.Cancel, Array.Empty<byte>()).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task ExecuteRunAsync(SpeedTestSession session, ActiveRunState run, bool isInitiator, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Cts.Token);
        var token = linkedCts.Token;

        run.Stopwatch.Restart();

        bool shouldUpload = run.Mode is TestMode.Upload or TestMode.Both;
        bool shouldDownload = run.Mode is TestMode.Download or TestMode.Both;

        Task? uploadTask = null;
        if (shouldUpload)
        {
            uploadTask = Task.Run(() => RunUploadPumpAsync(session, run, token), token);
        }

        // Monitoring and Progress reporting loop
        var sw = run.Stopwatch;
        long lastUploadBytes = 0;
        long lastDownloadBytes = 0;
        long lastTickMs = 0;

        try
        {
            while (!token.IsCancellationRequested && sw.Elapsed.TotalSeconds < run.DurationSeconds)
            {
                await Task.Delay(150, token).ConfigureAwait(false);

                long currentUpload = Interlocked.Read(ref run.BytesUploaded);
                long currentDownload = Interlocked.Read(ref run.BytesDownloaded);
                long currentTickMs = sw.ElapsedMilliseconds;
                double deltaSec = Math.Max(0.001, (currentTickMs - lastTickMs) / 1000.0);

                double curUpMbps = Math.Max(0, ((currentUpload - lastUploadBytes) * 8.0) / (deltaSec * 1_000_000.0));
                double curDownMbps = Math.Max(0, ((currentDownload - lastDownloadBytes) * 8.0) / (deltaSec * 1_000_000.0));
                double avgUpMbps = Math.Max(0, (currentUpload * 8.0) / (sw.Elapsed.TotalSeconds * 1_000_000.0));
                double avgDownMbps = Math.Max(0, (currentDownload * 8.0) / (sw.Elapsed.TotalSeconds * 1_000_000.0));

                run.LastUploadMbps = curUpMbps;
                run.LastDownloadMbps = curDownMbps;

                double rtt = session.Peer.LastTelemetry?.RttMs ?? (session.Peer.Ping?.TotalMilliseconds ?? 0.0);
                run.LatencyMs = rtt;

                lastUploadBytes = currentUpload;
                lastDownloadBytes = currentDownload;
                lastTickMs = currentTickMs;

                if (isInitiator)
                {
                    var prog = new ProgressPayload(
                        shouldUpload && shouldDownload ? "Both" : (shouldUpload ? "Upload" : "Download"),
                        currentUpload + currentDownload,
                        shouldUpload ? curUpMbps : curDownMbps,
                        shouldUpload ? avgUpMbps : avgDownMbps,
                        Math.Round(sw.Elapsed.TotalSeconds, 1),
                        run.DurationSeconds,
                        Math.Round(rtt, 1)
                    );
                    OnProgress?.Invoke(session.Peer, prog);

                    // Send progress to responder as well
                    byte[] progBytes = JsonSerializer.SerializeToUtf8Bytes(prog);
                    _ = session.SendFrameAsync(FrameType.Progress, progBytes, CancellationToken.None);
                }
            }

            if (uploadTask != null)
            {
                try { await uploadTask.ConfigureAwait(false); } catch { }
            }

            // Final calculations
            double totalSeconds = Math.Max(0.01, sw.Elapsed.TotalSeconds);
            run.FinalUploadMbps = Math.Round((Interlocked.Read(ref run.BytesUploaded) * 8.0) / (totalSeconds * 1_000_000.0), 2);
            run.FinalDownloadMbps = Math.Round((Interlocked.Read(ref run.BytesDownloaded) * 8.0) / (totalSeconds * 1_000_000.0), 2);
            run.IsCompleted = true;

            var summary = new SummaryPayload(
                run.Mode.ToString(),
                run.FinalUploadMbps,
                run.FinalDownloadMbps,
                Interlocked.Read(ref run.BytesUploaded) + Interlocked.Read(ref run.BytesDownloaded),
                Math.Round(totalSeconds, 2),
                Math.Round(run.LatencyMs, 1)
            );

            if (isInitiator)
            {
                byte[] sumBytes = JsonSerializer.SerializeToUtf8Bytes(summary);
                await session.SendFrameAsync(FrameType.Summary, sumBytes, CancellationToken.None).ConfigureAwait(false);
                OnCompleted?.Invoke(session.Peer, summary);
            }
        }
        catch (OperationCanceledException)
        {
            run.IsCompleted = true;
            run.ErrorMessage = "Test cancelled";
        }
        catch (Exception ex)
        {
            run.IsCompleted = true;
            run.ErrorMessage = ex.Message;
            OnError?.Invoke(session.Peer, ex.Message);
        }
    }

    private async Task RunUploadPumpAsync(SpeedTestSession session, ActiveRunState run, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DefaultChunkBytes);
        new Random(42).NextBytes(buffer.AsSpan(0, DefaultChunkBytes)); // fill with dummy pseudo-random data once

        try
        {
            while (!ct.IsCancellationRequested && run.Stopwatch.Elapsed.TotalSeconds < run.DurationSeconds)
            {
                await session.SendFrameAsync(FrameType.DataChunk, buffer.AsMemory(0, DefaultChunkBytes), ct).ConfigureAwait(false);
                Interlocked.Add(ref run.BytesUploaded, DefaultChunkBytes);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        foreach (var s in ActiveSessions.Values)
        {
            s.Dispose();
        }
        ActiveSessions.Clear();
    }
}