using System.Net;
using System.Net.Sockets;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using System.Threading.Channels;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using QuicPunch.Helpers;
using QuicNet.Protocols;
using Q = System.Net.Quic.QuicConnection;

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
            var serverOptions = new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 20,
                IdleTimeout = TimeSpan.FromSeconds(60),
                ServerAuthenticationOptions = new() { ApplicationProtocols = [alpn], ServerCertificate = cert }
            };
            await using var listener = await QuicListener.ListenAsync(new()
            {
                ListenEndPoint = new(IPAddress.Loopback, 0),
                ApplicationProtocols = [alpn],
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    if (prepared && !MsQuicDatagramChannel.EnsureServerConfigurationPatched(serverOptions))
                        throw new InvalidOperationException("Cannot prepare server configuration before handshake.");
                    if (cubicControl) SetCubicBeforeHandshake(serverOptions);
                return ValueTask.FromResult(serverOptions);
                }
            });
            await using var proxy = new DelayProxy(listener.LocalEndPoint, delay);
            var clientOptions = new QuicClientConnectionOptions
            {
                RemoteEndPoint = proxy.EndPoint,
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 20,
                IdleTimeout = TimeSpan.FromSeconds(60),
                ClientAuthenticationOptions = new() { ApplicationProtocols = [alpn], TargetHost = "localhost", RemoteCertificateValidationCallback = (_, _, _, _) => true }
            };
            var accept = listener.AcceptConnectionAsync();
            if (cubicControl)
            {
                if (!MsQuicDatagramChannel.EnsureClientConfigurationPatched(clientOptions))
                    throw new InvalidOperationException("Cannot prepare CUBIC control configuration.");
                SetCubicBeforeHandshake(clientOptions);
            }
            QuicPunch.IQuicConnectionTransport? clientTransport = prepared && !cubicControl
                ? await QuicPunch.NativeQuicConnectionFactory.Instance.ConnectAsync(clientOptions) : null;
            await using var client = clientTransport is QuicPunch.NativeConnectionTransport native
                ? native.InnerConnection : await Q.ConnectAsync(clientOptions);
            await using var server = await accept;
            Console.WriteLine($"Server {Snapshot(server)}");
            Console.WriteLine($"Connection delay {delay * 2}ms configured RTT; {Snapshot(client)}");
            using (var serverHandler = new SpeedTestHandler())
            using (var clientHandler = new SpeedTestHandler())
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
            {
                using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var clientRequest = new CertificateRequest("CN=perf-audit-client", clientKey, HashAlgorithmName.SHA256);
                using var clientCert = clientRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                var sp = new QuicPunch.PeerInfo(cert, null) { Name = "Audit server" };
                var cp = new QuicPunch.PeerInfo(clientCert, null) { Name = "Audit client" };
                await using var cw = clientTransport == null ? QuicPunch.QuicConnection.Wrap(client) : QuicPunch.QuicConnection.FromTransport(clientTransport);
                await using var swrap = QuicPunch.QuicConnection.Wrap(server);
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
    private static unsafe void SetCubicBeforeHandshake(QuicConnectionOptions options)
    {
        var type = typeof(Q).Assembly.GetType("System.Net.Quic.MsQuicConfiguration")!;
        var create = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "Create" && m.GetParameters()[0].ParameterType == options.GetType());
        var arguments = options is QuicServerConnectionOptions ? new object[] { options, "localhost" } : new object[] { options };
        var configuration = (SafeHandle)create.Invoke(null, arguments)!;
        using var lease = new NativeHandleLease(configuration);
        var del = (Delegate)typeof(MsQuicTuner).GetField("SetParam", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var set = (delegate* unmanaged<IntPtr, uint, uint, IntPtr, int>)Marshal.GetFunctionPointerForDelegate(del);
        byte[] settings = new byte[MsQuicTuner.QuicSettingsSize];
        BinaryPrimitives.WriteUInt64LittleEndian(settings, MsQuicTuner.CongestionAlgorithmIsSetBit);
        fixed (byte* bytes = settings)
            if (set(lease.Handle, MsQuicTuner.QuicParamConfigSettings, (uint)settings.Length, (IntPtr)bytes) != 0)
                throw new InvalidOperationException("Cannot set CUBIC on the unstarted configuration.");
    }

    static unsafe string Snapshot(Q connection)
    {
        var t = typeof(MsQuicTuner); var field = t.GetField("GetParam", BindingFlags.Static | BindingFlags.NonPublic)!;
        var del = (Delegate)field.GetValue(null)!;
        var get = (delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)Marshal.GetFunctionPointerForDelegate(del);
        var handle = (SafeHandle)typeof(Q).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
        byte[] bytes = new byte[512]; uint n = 512; int status;
        fixed (byte* p = bytes) status = get(handle.DangerousGetHandle(), MsQuicTuner.QuicParamConnSettings, &n, (IntPtr)p);
        return $"settings_status={status} length={n} send_buffer={(bytes[106] & 1) != 0} connFC={BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56))} bidiFC={BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128))} cc={BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(92))}";
    }

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
                        var packet = await socket.ReceiveAsync(stop.Token); if (fromClient) Interlocked.Increment(ref UpPackets); else Interlocked.Increment(ref DownPackets);
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
