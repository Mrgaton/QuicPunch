using System.Net;
using System.Net.Sockets;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using Q = QuicPunch.QuicConnection;

using QuicPunch.Multiplexer;

namespace QuicPunchTests.Tests;

public static class QuicPerformanceTests
{
    public static Task RunAsync() => RunCoreAsync(prepared: true, multiplexed: true);
    public static Task RunBaselineAsync() => RunCoreAsync(prepared: false, multiplexed: false);
    public static Task RunCubicAsync() => RunCoreAsync(prepared: true, multiplexed: true, cubicControl: true);
    private static async Task RunCoreAsync(bool prepared, bool multiplexed, bool cubicControl = false)
    {
        Console.WriteLine($"MsQuic native tuning supported: {MsQuicTuner.IsSupported}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=perf-audit", key, HashAlgorithmName.SHA256);
        using var cert0 = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(cert0.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        var alpn = new SslApplicationProtocol("qp-perf-audit");

        Console.WriteLine($"CASE prepared={prepared} multiplexed={multiplexed} cubic_control={cubicControl} loss_every=500 configured_rtt_ms=80; artificial loopback path, not NIC capacity");
        foreach (int delay in new[] { 40 })
        {
            MsQuicTuner.EnsureOptimalConfiguration(minMtu: 1248, congestionAlgorithm: cubicControl || !prepared ? QuicCongestionAlgorithm.Cubic : QuicCongestionAlgorithm.Bbr);
            using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var clientRequest = new CertificateRequest("CN=perf-audit-client", clientKey, HashAlgorithmName.SHA256);
            using var clientCert = clientRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            ushort port = (ushort)((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
            reservation.Dispose();
            await using var serverTransport = QuicPunch.MsQuicBridgeTransport.Create(cert, Sha3Crypto.Hash256(clientCert.GetPublicKey()));
            serverTransport.Listen(port);
            await using var proxy = new DelayProxy(new IPEndPoint(IPAddress.Loopback, port), delay);
            await using var clientTransport = QuicPunch.MsQuicBridgeTransport.Create(clientCert, Sha3Crypto.Hash256(cert.GetPublicKey()), proxy.EndPoint);
            clientTransport.Connect(proxy.EndPoint, 0);
            using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(serverTransport.WaitConnectedAsync(setup.Token), clientTransport.WaitConnectedAsync(setup.Token));
            await using var client = Q.FromTransport(clientTransport);
            await using var server = Q.FromTransport(serverTransport);
            Console.WriteLine($"Server {Snapshot(server)}");
            Console.WriteLine($"Connection delay {delay * 2}ms configured RTT; {Snapshot(client)}");
            using (var serverHandler = new SpeedTestHandler())
            using (var clientHandler = new SpeedTestHandler())
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
            {
                var sp = new QuicPunch.PeerInfo(cert, null) { Name = "Audit server" };
                var cp = new QuicPunch.PeerInfo(clientCert, null) { Name = "Audit client" };
                var cw = client;
                var swrap = server;
                await using var serverMux = multiplexed ? new QuicPeerMultiplexer(swrap, cp, true, id => id == serverHandler.ProtocolId ? serverHandler : null, _ => true) : null;
                await using var clientMux = multiplexed ? new QuicPeerMultiplexer(cw, sp, false, id => id == clientHandler.ProtocolId ? clientHandler : null, _ => true) : null;
                if (multiplexed) await Task.WhenAll(serverMux!.StartAsync(), clientMux!.StartAsync());
                var incoming = multiplexed ? Task.CompletedTask : Task.Run(async () => { var st = await swrap.AcceptInboundStreamAsync(stop.Token); await serverHandler.HandleAsync(swrap, st, cp, stop.Token); });
                var sessionConnection = multiplexed ? await clientMux!.OpenVirtualSessionAsync(clientHandler.ProtocolId, stop.Token) : cw;
                var os = await sessionConnection.OpenOutboundStreamAsync(QuicPunch.QuicStreamType.Bidirectional, stop.Token);
                var outgoing = clientHandler.HandleAsync(sessionConnection, os, sp, stop.Token);
                var done = new TaskCompletionSource<SpeedTestHandler.SummaryPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                clientHandler.OnCompleted += (_, r) => done.TrySetResult(r);
                clientHandler.OnError += (_, e) => done.TrySetException(new Exception(e));
                await clientHandler.StartTestAsync(sp, SpeedTestHandler.TestMode.Both, 10, stop.Token);
                var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(25));
                Console.WriteLine($"SPEEDTEST configured_rtt={delay * 2} up_Mbps={result.UploadMbps:F2} down_Mbps={result.DownloadMbps:F2} baseline_ms={result.Baseline?.RttAverageMs:F2} loaded_ms={result.Quality?.RttAverageMs:F2}");
                stop.Cancel(); await Task.WhenAll(incoming, outgoing);
            }
            Console.WriteLine($"PROXY up_packets={proxy.UpPackets} down_packets={proxy.DownPackets} up_dropped={proxy.UpDropped} down_dropped={proxy.DownDropped}");
        }

    }

    // Control case: identical prepared flow-control windows, pacing and datagrams,
    // with CUBIC selected on the configuration BEFORE either endpoint starts.
    static string Snapshot(Q connection) => connection.TryGetTelemetry(out var t)
        ? $"cc={t.CongestionAlgorithm} rtt={t.RttMs:F1} mtu={t.PathMtu} tls={t.NegotiatedTlsGroupName}"
        : "telemetry unavailable";

    sealed class DelayProxy : IAsyncDisposable
    {
        readonly UdpClient front = new(new IPEndPoint(IPAddress.Loopback, 0)), back = new(new IPEndPoint(IPAddress.Loopback, 0));
        readonly CancellationTokenSource stop = new();
        readonly Task[] tasks; IPEndPoint? client;
        public long UpPackets, DownPackets, UpDropped, DownDropped;
        public IPEndPoint EndPoint => (IPEndPoint)front.Client.LocalEndPoint!;
        public DelayProxy(IPEndPoint server, int delay)
        {
            back.Connect(server);
            var up = Channel.CreateUnbounded<(byte[] Bytes, long Due)>(); var down = Channel.CreateUnbounded<(byte[] Bytes, long Due)>();
            tasks = [Read(front, up.Writer, true), Read(back, down.Writer, false), Write(up.Reader, back, false), Write(down.Reader, front, true)];
            async Task Read(UdpClient socket, ChannelWriter<(byte[], long)> queue, bool fromClient)
            {
                int packetCount = 0; try
                {
                    while (true)
                    {
                        UdpReceiveResult packet;
                        try { packet = await socket.ReceiveAsync(stop.Token); }
                        // Windows reports ICMP port-unreachable as ConnectionReset when
                        // the QUIC endpoint closes before this UDP delay proxy.
                        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
                        { stop.Token.ThrowIfCancellationRequested(); continue; }
                        if (fromClient) Interlocked.Increment(ref UpPackets); else Interlocked.Increment(ref DownPackets);
                        if (++packetCount % 500 == 0) { if (fromClient) Interlocked.Increment(ref UpDropped); else Interlocked.Increment(ref DownDropped); continue; }
                        if (fromClient) client = packet.RemoteEndPoint; await queue.WriteAsync((packet.Buffer, Stopwatch.GetTimestamp() + (long)(delay / 1000.0 * Stopwatch.Frequency)), stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                finally { queue.TryComplete(); }
            }
            async Task Write(ChannelReader<(byte[] Bytes, long Due)> queue, UdpClient socket, bool toClient)
            {
                try { await foreach (var packet in queue.ReadAllAsync(stop.Token)) { var left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), packet.Due); if (left > TimeSpan.Zero) await Task.Delay(left, stop.Token); if (toClient) { if (client != null) await socket.SendAsync(packet.Bytes, client, stop.Token); } else await socket.SendAsync(packet.Bytes, stop.Token); } }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); front.Dispose(); back.Dispose(); await Task.WhenAll(tasks); stop.Dispose(); }
    }

}
