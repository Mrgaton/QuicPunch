using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using QuicNet.WebUi;
using QuicNet.WebUi.Modules;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch;
using QuicPunch.Multiplexer;
using QuicPunch.Helpers;
using QuicPunch.Datagrams;
using System.Threading.Channels;
using QuicNet.Protocols;
using QuicConnection = QuicPunch.QuicConnection;
using NativeQuicConnection = System.Net.Quic.QuicConnection;

namespace QuicPunchTests.Tests;

public static class SpeedTestTests
{
    private static X509Certificate2 GenerateTestCert()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=SpeedTestNode", ecdsa, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.Exportable);
    }

    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("        SPEED TEST PROTOCOL END-TO-END TESTS      ");
        Console.WriteLine("==================================================");

        Test_SlidingRateMeter_SmoothsBurstyTraffic();
        Test_QualityStatistics();
        await Test_ProbeLossAsync();
        await Test_FrameCancellationAsync();
        await Test_SpeedTest_EndToEndBenchmarkAsync(false);
        await Test_SpeedTest_EndToEndBenchmarkAsync(true);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(20); }
        if (Volatile.Read(ref _unobservedUploadFailures) != 0)
            throw new Exception("A failed upload pump was abandoned without awaiting its exception.");

        Console.WriteLine("==================================================");
        Console.WriteLine("     ALL SPEED TEST BENCHMARK TESTS PASSED!       ");
        Console.WriteLine("==================================================");
    }
    private static int _unobservedUploadFailures;
    static SpeedTestTests()
    {
        TaskScheduler.UnobservedTaskException += (_, args) => {
            if (args.Exception.Flatten().InnerExceptions.Any(ex => ex.Message == "Injected interrupted upload"))
            { Interlocked.Increment(ref _unobservedUploadFailures); args.SetObserved(); }
        };
    }

    private static void Test_SlidingRateMeter_SmoothsBurstyTraffic()
    {
        Console.Write("[TEST] SpeedTest sliding-window meter smooths bursty traffic... ");

        var meter = new SpeedTestHandler.SlidingRateMeter(2_000);
        var firstBurst = meter.AddSample(250, 0, 64 * 1024);
        var betweenBursts = meter.AddSample(500, 0, 64 * 1024);
        var secondBurst = meter.AddSample(1_000, 0, 128 * 1024);

        if (firstBurst.DownloadMbps <= 0 || betweenBursts.DownloadMbps <= 0 || secondBurst.DownloadMbps <= 0)
            throw new Exception("Sliding-window rate dropped to zero between transport bursts.");

        var genuinelyIdle = meter.AddSample(3_500, 0, 128 * 1024);
        if (genuinelyIdle.DownloadMbps != 0)
            throw new Exception("Sliding-window rate did not return to zero after the full idle window.");

        Console.WriteLine("[OK]");
    }

    private sealed class ProbeChannel : IQuicDatagramChannel
    {
        public bool IsSendEnabled => true;
        public bool IsReceiveEnabled => true;
        public ushort MaxSendDatagramLength => 1200;
        public Channel<byte[]> IncomingDatagrams { get; } = Channel.CreateUnbounded<byte[]>();
        public event Action<byte[]>? OnDatagramReceived { add { } remove { } }
        public event Action<IPEndPoint>? OnPeerAddressChanged { add { } remove { } }
        public bool Send(ReadOnlySpan<byte> data) => true;
        public void Dispose() { }
    }

    private sealed class ProbeTransport(PeerInfo peer) : IQuicConnectionTransport
    {
        private int _attempt;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        public IQuicDatagramChannel DatagramChannel { get; } = new ProbeChannel();
        public event Action<PeerInfo, QuicDatagramMessage>? OnCategorizedDatagramReceived;
        public bool SendCategorizedDatagram(byte category, ReadOnlySpan<byte> payload,
            QuicDatagramFlags flags = QuicDatagramFlags.None, uint sequenceNumber = 0)
        {
            int attempt = ++_attempt;
            if (attempt == 2) return false; // Local queue rejection is not network loss.
            if (attempt == 3) return true; // Accepted datagram deliberately lost.
            byte[] reply = payload.ToArray();
            reply[0] = 1;
            var message = new QuicDatagramMessage(peer, category, flags, sequenceNumber, reply);
            OnCategorizedDatagramReceived?.Invoke(peer, message);
            OnCategorizedDatagramReceived?.Invoke(peer, message); // Duplicate must not count twice.
            return true;
        }
        public ValueTask<IQuicStreamTransport> OpenOutboundStreamAsync(QuicPunch.QuicStreamType type, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IQuicStreamTransport> AcceptInboundStreamAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask CloseAsync(long errorCode, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProbeReplyStream : MemoryStream
    {
        public Action<byte[]>? OnFlush { get; set; }
        public override Task FlushAsync(CancellationToken ct)
        {
            OnFlush?.Invoke(ToArray());
            SetLength(0);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockedWriteStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_first)
            {
                _first = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            await base.WriteAsync(buffer, ct);
        }
    }

    private static async Task Test_FrameCancellationAsync()
    {
        using var cert = GenerateTestCert();
        var peer = new PeerInfo(cert, null);
        using var handler = new SpeedTestHandler();
        await using var connection = QuicConnection.FromTransport(new ProbeTransport(peer));
        using var stream = new BlockedWriteStream();
        using var session = new SpeedTestHandler.SpeedTestSession(handler, peer, stream, connection);
        using var cancelled = new CancellationTokenSource();
        var write = session.SendFrameAsync(SpeedTestHandler.FrameType.DataChunk, new byte[] { 1, 2, 3 }, cancelled.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelled.Cancel();
        // A queued frame may be cancelled; the frame already holding the writer must finish.
        try
        {
            await session.SendFrameAsync(SpeedTestHandler.FrameType.Probe, new byte[16], cancelled.Token);
            throw new Exception("Cancelled queued frame was written.");
        }
        catch (OperationCanceledException) { }
        var reply = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        session.QueueProbeReply(reply);
        Array.Fill(reply, (byte)255); // Simulate reuse of the receive-loop pooled buffer.
        stream.Release.TrySetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 100 && stream.Length < 29; i++) await Task.Delay(5);
        var frame = stream.ToArray();
        if (frame.Length != 29 || frame[0] != (byte)SpeedTestHandler.FrameType.DataChunk ||
            !frame.AsSpan(5, 3).SequenceEqual(new byte[] { 1, 2, 3 }) ||
            frame[8] != (byte)SpeedTestHandler.FrameType.ProbeReply || frame[13] != 0 || frame[28] != 15)
            throw new Exception("Probe cancellation corrupted the stream or asynchronous echo used a reused receive buffer.");
        await session.SendFrameAsync(SpeedTestHandler.FrameType.Complete, new byte[] { 9 });
        Console.WriteLine("[OK] Backpressured frames survive probe/run cancellation; queued replies do not block the reader or reuse pooled memory.");
    }

    private sealed class SlowFaultStream(Stream inner) : Stream
    {
        public bool FailNextWrite;
        public bool FailNextUpload;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (FailNextUpload && buffer.Length == 5 && buffer.Span[0] == (byte)SpeedTestHandler.FrameType.DataChunk)
            { FailNextUpload = false; throw new IOException("Injected interrupted upload"); }
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Injected interrupted connection"); }
            await Task.Delay(1, ct); // Exercise writer contention at realistic lower throughput.
            await inner.WriteAsync(buffer, ct);
        }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static async Task<JsonElement> ReadHistoryApiAsync(SpeedTestHandler handler, bool clear = false)
    {
        using var cancellation = new CancellationTokenSource();
        await using var hub = new WebUiWebSocketHub(cancellation);
        var module = new SpeedTestApiModule(null!, handler, hub, cancellation);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var client = new HttpClient();
        string path = clear ? "/api/speedtest/history/clear" : "/api/speedtest/status";
        var pending = client.SendAsync(new HttpRequestMessage(clear ? HttpMethod.Post : HttpMethod.Get,
            $"http://127.0.0.1:{port}{path}"));
        var request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        if (!await module.HandleRequestAsync(path, request.Request, request.Response))
            throw new Exception("SpeedTest history route was not handled.");
        request.Response.Close();
        using var response = await pending;
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task Test_ProbeLossAsync()
    {
        using var cert = GenerateTestCert();
        var peer = new PeerInfo(cert, null);
        using var handler = new SpeedTestHandler();
        await using var connection = QuicConnection.FromTransport(new ProbeTransport(peer));
        using var stream = new ProbeReplyStream();
        using var session = new SpeedTestHandler.SpeedTestSession(handler, peer, stream, connection);
        stream.OnFlush = frame =>
        {
            if (frame.Length == 21 && frame[0] == (byte)SpeedTestHandler.FrameType.Probe)
                session.Probes.Complete(frame.AsMemory(5));
        };
        var quality = await session.Probes.MeasureAsync(3, CancellationToken.None);
        if (quality.Sent != 2 || quality.Received != 1 || quality.SendFailures != 1 || quality.LossPercent != 50)
            throw new Exception("Probe test failed to distinguish dropped, duplicated and locally rejected datagrams.");
        if (quality.RttAverageMs == null || quality.JitterMs != null)
            throw new Exception("Probe test fabricated missing jitter or discarded its real RTT.");
        // An older peer that cannot echo probes must report unavailable metrics.
        using var legacySession = new SpeedTestHandler.SpeedTestSession(handler, peer, new MemoryStream(), connection);
        var unavailable = await legacySession.Probes.MeasureAsync(3, CancellationToken.None);
        if (unavailable.Sent != 0 || unavailable.LossPercent != null || unavailable.RttAverageMs != null)
            throw new Exception("An older peer was mistaken for 100% datagram loss.");
        Console.WriteLine("[OK] Probes distinguish loss, duplicates, local rejection and unsupported peers.");
    }

    private static void Test_QualityStatistics()
    {
        var result = SpeedTestHandler.SummarizeQuality("QUIC DATAGRAM", 5, 2, [10, 20, 15]);
        if (result.LossPercent != 40 || result.RttMinMs != 10 || result.RttAverageMs != 15 ||
            result.RttMaxMs != 20 || result.JitterMs != 7.5 || result.SendFailures != 2)
            throw new Exception("Incorrect RTT, jitter, loss or send-failure statistics.");
        var reliable = SpeedTestHandler.SummarizeQuality("Stream fiable", 3, 0, [10, 20]);
        if (reliable.LossPercent != null) throw new Exception("Reliable stream must not claim packet-loss measurement.");
        var empty = SpeedTestHandler.SummarizeQuality("QUIC DATAGRAM", 0, 1, []);
        if (empty.LossPercent != null || empty.RttAverageMs != null || empty.JitterMs != null)
            throw new Exception("Missing samples must remain unavailable, not fabricated zeroes.");
        var lost = SpeedTestHandler.SummarizeQuality("QUIC DATAGRAM", 3, 0, []);
        if (lost.LossPercent != 100 || lost.RttAverageMs != null)
            throw new Exception("All unanswered datagrams must report 100% loss without a fabricated RTT.");
        Console.WriteLine("[OK] Quality statistics, loss, empty samples and reliable-stream fallback.");
    }

    private static async Task Test_SpeedTest_EndToEndBenchmarkAsync(bool enableDatagrams)
    {
        Console.Write("[TEST] SpeedTestHandler benchmark over QuicPeerMultiplexer... ");

        using var serverCert = GenerateTestCert();
        using var clientCert = GenerateTestCert();

        var alpn = new SslApplicationProtocol("qpmux");
        var serverEndPoint = new IPEndPoint(IPAddress.Loopback, 0);

        var serverOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 512,
            MaxInboundUnidirectionalStreams = 512,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
                ServerCertificate = serverCert,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = serverEndPoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions)
        };

        await using var listener = await QuicListener.ListenAsync(listenerOptions);
        var boundEndPoint = listener.LocalEndPoint;

        var clientOptions = new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundBidirectionalStreams = 512,
            MaxInboundUnidirectionalStreams = 512,
            RemoteEndPoint = boundEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { alpn },
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { clientCert },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        if (enableDatagrams)
        {
            var warmServerTask = listener.AcceptConnectionAsync();
            await using var warmClient = await NativeQuicConnection.ConnectAsync(clientOptions);
            await using var warmServer = await warmServerTask;
            if (!MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache())
                throw new Exception("Failed to enable datagrams for quality-probe tests.");
        }

        var serverAcceptTask = listener.AcceptConnectionAsync();
        var clientNative = await NativeQuicConnection.ConnectAsync(clientOptions);
        var serverNative = await serverAcceptTask;

        var serverQuicConn = QuicConnection.Wrap(serverNative);
        var clientQuicConn = QuicConnection.Wrap(clientNative);

        using var serverHandler = new SpeedTestHandler();
        using var clientHandler = new SpeedTestHandler();

        var serverPeer = new PeerInfo(serverCert, null) { Name = "ServerNode" };
        var clientPeer = new PeerInfo(clientCert, null) { Name = "ClientNode" };

        var serverMux = new QuicPeerMultiplexer(
            serverQuicConn,
            clientPeer,
            isServer: true,
            protocolLookup: id => id == serverHandler.ProtocolId ? serverHandler : null,
            accessCheck: _ => true
        );

        var clientMux = new QuicPeerMultiplexer(
            clientQuicConn,
            serverPeer,
            isServer: false,
            protocolLookup: id => id == clientHandler.ProtocolId ? clientHandler : null,
            accessCheck: _ => true
        );

        await serverMux.StartAsync();
        await clientMux.StartAsync();

        // 1. Client initiates SpeedTest virtual session
        var clientSessionConn = await clientMux.OpenVirtualSessionAsync(clientHandler.ProtocolId);
        var clientAppStream = new SlowFaultStream(await clientSessionConn.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional));
        
        using var clientHandlerCts = new CancellationTokenSource();
        _ = Task.Run(() => clientHandler.HandleAsync(clientSessionConn, clientAppStream, serverPeer, clientHandlerCts.Token));

        // Wait briefly for both sides to establish session
        for (int i = 0; i < 40 && (!SpeedTestHandler.ActiveSessions.ContainsKey(serverPeer.Id) || !SpeedTestHandler.ActiveSessions.ContainsKey(clientPeer.Id)); i++)
        {
            await Task.Delay(50);
        }

        // Test 1: Upload mode (Client -> Server)
        {
            var completedTcs = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> handler = (_, s) => completedTcs.TrySetResult(s);
            clientHandler.OnCompleted += handler;

            var run = await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Upload, 2);
            var summary = await Task.WhenAny(completedTcs.Task, Task.Delay(5000)) == completedTcs.Task ? await completedTcs.Task : null;
            clientHandler.OnCompleted -= handler;

            if (summary == null) throw new Exception("Upload test timed out.");
            if (summary.UploadMbps <= 0) throw new Exception($"Expected UploadMbps > 0, got {summary.UploadMbps}");
            if (summary.DownloadMbps != 0) throw new Exception($"Upload-only test reported download traffic: {summary.DownloadMbps}");
            if (summary.BytesUploaded <= 0 || summary.BytesDownloaded != 0)
                throw new Exception($"Upload-only byte counters are invalid: Up:{summary.BytesUploaded} Down:{summary.BytesDownloaded}");
            Console.WriteLine($"[Upload OK: {summary.UploadMbps} Mbps]");
        }

        // Test 2: Download mode (Server -> Client)
        {
            var completedTcs = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> handler = (_, s) => completedTcs.TrySetResult(s);
            clientHandler.OnCompleted += handler;

            var run = await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Download, 2);
            var summary = await Task.WhenAny(completedTcs.Task, Task.Delay(5000)) == completedTcs.Task ? await completedTcs.Task : null;
            clientHandler.OnCompleted -= handler;

            if (summary == null) throw new Exception("Download test timed out.");
            if (summary.DownloadMbps <= 0) throw new Exception($"Expected DownloadMbps > 0, got {summary.DownloadMbps}");
            if (summary.UploadMbps != 0) throw new Exception($"Download-only test reported upload traffic: {summary.UploadMbps}");
            if (summary.BytesDownloaded <= 0 || summary.BytesUploaded != 0)
                throw new Exception($"Download-only byte counters are invalid: Up:{summary.BytesUploaded} Down:{summary.BytesDownloaded}");
            Console.WriteLine($"[Download OK: {summary.DownloadMbps} Mbps]");
        }

        // Test 3: Both mode (Bidirectional)
        {
            var completedTcs = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> handler = (_, s) => completedTcs.TrySetResult(s);
            clientHandler.OnCompleted += handler;

            var run = await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Both, 2);
            var summary = await Task.WhenAny(completedTcs.Task, Task.Delay(5000)) == completedTcs.Task ? await completedTcs.Task : null;
            clientHandler.OnCompleted -= handler;

            if (summary == null) throw new Exception("Bidirectional test timed out.");
            if (summary.UploadMbps <= 0 || summary.DownloadMbps <= 0)
                throw new Exception($"Expected UploadMbps > 0 and DownloadMbps > 0, got Up:{summary.UploadMbps} Down:{summary.DownloadMbps}");
            if (summary.BytesUploaded <= 0 || summary.BytesDownloaded <= 0)
                throw new Exception($"Expected directional byte counters, got Up:{summary.BytesUploaded} Down:{summary.BytesDownloaded}");
            Console.WriteLine($"[Bidirectional OK: Up {summary.UploadMbps} Mbps, Down {summary.DownloadMbps} Mbps]");
        }

        // All executes isolated download, isolated upload, then full duplex; it emits one final summary.
        {
            var completed = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<string>();
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> onPhase = (phasePeer, result) => observed.Add(result.Mode);
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> onComplete = (_, result) => completed.TrySetResult(result);
            clientHandler.OnPhaseCompleted += onPhase;
            clientHandler.OnCompleted += onComplete;
            await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.All, 2);
            bool rejected = false;
            try { await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.Upload, 2); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new Exception("A concurrent test replaced an active plan.");
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            clientHandler.OnPhaseCompleted -= onPhase;
            clientHandler.OnCompleted -= onComplete;
            if (!observed.SequenceEqual(new[] { "Download", "Upload", "Both" }) || result.Mode != "All" || result.Stages?.Length != 3)
                throw new Exception("All did not preserve the requested phase order and separate results.");
            var stages = result.Stages;
            if (stages[0].UploadMbps != 0 || stages[1].DownloadMbps != 0 ||
                stages[2].UploadMbps <= 0 || stages[2].DownloadMbps <= 0)
                throw new Exception("Phase results contain incorrect traffic directions.");
            if (result.TotalBytes != stages.Sum(stage => stage.TotalBytes))
                throw new Exception("All did not sum the bytes of its phases.");
            if (result.Baseline?.Received < 2 || result.Baseline?.RttAverageMs is not > 0)
                throw new Exception("No measured idle RTT samples.");
            foreach (var quality in stages.Select(stage => stage.Quality).Append(result.Baseline))
            {
                if (quality?.Received < 2 || quality?.RttAverageMs is not > 0 || quality.JitterMs == null)
                    throw new Exception("Missing loaded RTT/jitter probes.");
                if (enableDatagrams && (quality.Method != "QUIC DATAGRAM" || quality.LossPercent == null))
                    throw new Exception("Native datagram packet-loss probes were not exercised.");
                if (!enableDatagrams && quality.LossPercent != null)
                    throw new Exception("Fallback reported an unreliable packet-loss measurement.");
            }
            Console.WriteLine($"[All OK: phases, quality, concurrency, {(enableDatagrams ? "native datagrams" : "stream fallback")}]");
        }

        // Cancellation between phases must prevent Upload/Both from starting.
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new List<string>();
            Action<PeerInfo, SpeedTestHandler.SummaryPayload> onPhase = (phasePeer, result) =>
            {
                observed.Add(result.Mode);
                _ = clientHandler.CancelTestAsync(serverPeer.Id);
            };
            Action<PeerInfo, string> onError = (_, _) => cancelled.TrySetResult();
            clientHandler.OnPhaseCompleted += onPhase;
            clientHandler.OnError += onError;
            await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.All, 2);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(8));
            clientHandler.OnPhaseCompleted -= onPhase;
            clientHandler.OnError -= onError;
            if (!observed.SequenceEqual(new[] { "Download" }))
                throw new Exception("Cancelled plan continued into another phase.");
            if (clientHandler.CurrentPlan != null)
                throw new Exception("Cancellation left an active plan.");
            if (clientHandler.History.First().Results.Length != 1 || clientHandler.History.First().Baseline == null ||
                clientHandler.History.First().Error == null)
                throw new Exception("Cancelled test lost its completed phases, idle quality or interruption state.");
            Console.WriteLine("[Cancel OK: remaining phases stopped, partial history retained]");
        }

        // Fail at the start of Both after two successful phases. Completed phases must
        // survive an actual broken QUIC stream and remain accessible without peer lookup.
        {
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PeerInfo, string> breakPhase = (_, phase) => { if (phase == "Both") clientAppStream.FailNextUpload = true; };
            Action<PeerInfo, string> failedHandler = (_, _) => failed.TrySetResult();
            clientHandler.OnPhaseStarted += breakPhase;
            clientHandler.OnError += failedHandler;
            await clientHandler.StartTestAsync(serverPeer, SpeedTestHandler.TestMode.All, 2);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(12));
            clientHandler.OnPhaseStarted -= breakPhase;
            clientHandler.OnError -= failedHandler;
            var plan = clientHandler.History.First();
            if (plan.Results.Length != 2 || plan.Baseline?.Received is not > 0 || plan.Error == null || !plan.IsCompleted)
                throw new Exception("A failed duplex phase discarded the completed upload/download results.");
            var status = await ReadHistoryApiAsync(clientHandler);
            if (status.GetProperty("isRunning").GetBoolean() || status.GetProperty("history").GetArrayLength() != 6 ||
                status.GetProperty("lastResult").GetProperty("stages").GetArrayLength() != 2 ||
                status.GetProperty("lastResult").GetProperty("state").GetString() != "interrupted" ||
                status.GetProperty("lastResult").GetProperty("baseline").GetProperty("received").GetInt32() <= 0)
                throw new Exception("History API failed to restore all session results, interrupted phases or idle RTT.");
            await ReadHistoryApiAsync(clientHandler, clear: true);
            status = await ReadHistoryApiAsync(clientHandler);
            if (status.GetProperty("history").GetArrayLength() != 0)
                throw new Exception("Clear did not persist across history reload.");
            Console.WriteLine("[History OK: failed third phase, both earlier phases retained, reload and clear over HTTP]");
        }

        clientHandlerCts.Cancel();
        await clientMux.DisposeAsync();
        await serverMux.DisposeAsync();
    }
}
