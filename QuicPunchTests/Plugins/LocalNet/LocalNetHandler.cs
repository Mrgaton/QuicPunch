using QuicPunch;
using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UdpPunchHoleTest;

namespace UdpPunchHoleTest.Plugins.LocalNet
{
    internal sealed class LocalNetConfig
    {
        public string ListenAddress { get; set; } = "127.0.0.1";
        public int ConnectTimeoutMs { get; set; } = 7000;
        public List<LocalNetService> Services { get; set; } = [];

        public static LocalNetConfig LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var config = JsonSerializer.Deserialize(json, LocalNetJsonContext.Default.LocalNetConfig);

                if (config != null)
                    return config;
            }

            var defaultConfig = CreateDefault();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);

            string defaultJson = JsonSerializer.Serialize(
                defaultConfig,
                LocalNetJsonContext.Default.LocalNetConfig);

            File.WriteAllText(path, defaultJson, Encoding.UTF8);
            return defaultConfig;
        }

        private static LocalNetConfig CreateDefault()
        {
            return new LocalNetConfig
            {
                ListenAddress = "127.0.0.1",
                ConnectTimeoutMs = 7000,
                Services =
                [
                    new LocalNetService
                    {
                        Name = "Minecraft",
                        LocalPort = 25566,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 25565,
                        Type = "tcp"
                    },
                    new LocalNetService
                    {
                        Name = "SSH",
                        LocalPort = 2222,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 22,
                        Type = "tcp"
                    },
                    new LocalNetService
                    {
                        Name = "API",
                        LocalPort = 8080,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 3000,
                        Type = "tcp"
                    }
                ]
            };
        }
    }

    internal sealed class LocalNetService
    {
        public string Name { get; set; } = "";
        public int LocalPort { get; set; }
        public string RemoteHost { get; set; } = "127.0.0.1";
        public int RemotePort { get; set; }
        public string Type { get; set; } = "tcp";
    }

    internal sealed class LocalNetHandler : QuicPunchCore.IProtocolHandler
    {
        private readonly LocalNetConfig _config;
        private readonly Dictionary<string, LocalNetService> _servicesByName;

        public LocalNetHandler(LocalNetConfig config)
        {
            _config = config;
            _servicesByName = config.Services
                .Where(service => !string.IsNullOrWhiteSpace(service.Name))
                .ToDictionary(service => service.Name, StringComparer.OrdinalIgnoreCase);
        }

        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public string ProtocolName => "LocalNet TCP tunnel";

        public async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            Console.WriteLine();
            Console.WriteLine($"[LocalNet] Connected with {peer.Name} ({peer.EndPoint}).");
            PrintServices();

            var keepInitialStreamTask = KeepInitialStreamOpenAsync(stream, linkedCts.Token);
            var acceptRemoteStreamsTask = AcceptRemoteStreamsAsync(connection, linkedCts.Token);

            var listenerTasks = _config.Services
                .Where(service => string.Equals(service.Type, "tcp", StringComparison.OrdinalIgnoreCase))
                .Select(service => RunLocalTcpListenerAsync(connection, service, linkedCts.Token))
                .ToArray();

            try
            {
                await acceptRemoteStreamsTask;
            }
            finally
            {
                linkedCts.Cancel();

                try
                {
                    await Task.WhenAll(listenerTasks);
                }
                catch
                {
                }

                try
                {
                    await keepInitialStreamTask;
                }
                catch
                {
                }
            }
        }

        private void PrintServices()
        {
            Console.WriteLine("[LocalNet] Local forwarders:");

            foreach (var service in _config.Services)
            {
                Console.WriteLine(
                    $"  {service.Name}: {_config.ListenAddress}:{service.LocalPort} -> peer:{service.RemoteHost}:{service.RemotePort}/{service.Type}");
            }

            Console.WriteLine();
        }

        private static async Task KeepInitialStreamOpenAsync(Stream stream, CancellationToken ct)
        {
            try
            {
                byte[] hello = [(byte)'L'];
                await stream.WriteAsync(hello, ct);
                await stream.FlushAsync(ct);

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }

        private async Task RunLocalTcpListenerAsync(
            QuicConnection connection,
            LocalNetService service,
            CancellationToken ct)
        {
            if (!IPAddress.TryParse(_config.ListenAddress, out var listenAddress))
                listenAddress = IPAddress.Loopback;

            var listener = new TcpListener(listenAddress, service.LocalPort);

            try
            {
                listener.Server.NoDelay = true;
                listener.Start();

                Console.WriteLine(
                    $"[LocalNet] Listening on {_config.ListenAddress}:{service.LocalPort} for {service.Name}.");

                while (!ct.IsCancellationRequested)
                {
                    TcpClient tcpClient;

                    try
                    {
                        tcpClient = await listener.AcceptTcpClientAsync(ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    tcpClient.NoDelay = true;

                    _ = Task.Run(
                        () => HandleLocalTcpClientAsync(connection, service, tcpClient, ct),
                        CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                Console.WriteLine(
                    $"[LocalNet] Could not listen on {_config.ListenAddress}:{service.LocalPort} for {service.Name}: {ex.Message}");
            }
            finally
            {
                listener.Stop();
            }
        }

        private async Task HandleLocalTcpClientAsync(
            QuicConnection connection,
            LocalNetService service,
            TcpClient tcpClient,
            CancellationToken ct)
        {
            using var tcpClientScope = tcpClient;

            try
            {
                await using QuicStream quicStream = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    ct);

                await TunnelProtocol.WriteOpenRequestAsync(
                    quicStream,
                    service.Name,
                    service.RemoteHost,
                    service.RemotePort,
                    ct);

                var response = await TunnelProtocol.ReadOpenResponseAsync(quicStream, ct);

                if (!response.Success)
                {
                    Console.WriteLine($"[LocalNet] Peer rejected {service.Name}: {response.Error}");
                    return;
                }

                Console.WriteLine(
                    $"[LocalNet] Tunnel open: localhost:{service.LocalPort} -> peer:{service.RemoteHost}:{service.RemotePort}");

                using NetworkStream tcpStream = tcpClient.GetStream();
                await PipeBothWaysAsync(tcpStream, quicStream, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalNet] TCP tunnel error for {service.Name}: {ex.Message}");
            }
        }

        private async Task AcceptRemoteStreamsAsync(QuicConnection connection, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                QuicStream quicStream;

                try
                {
                    quicStream = await connection.AcceptInboundStreamAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LocalNet] QUIC stream accept loop ended: {ex.Message}");
                    break;
                }

                _ = Task.Run(
                    () => HandleRemoteQuicStreamAsync(quicStream, ct),
                    CancellationToken.None);
            }
        }

        private async Task HandleRemoteQuicStreamAsync(QuicStream quicStream, CancellationToken ct)
        {
            try
            {
                OpenRequest request;

                try
                {
                    request = await TunnelProtocol.ReadOpenRequestAsync(quicStream, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LocalNet] Invalid open request: {ex.Message}");
                    return;
                }

                if (!IsAllowedTarget(request))
                {
                    string error =
                        $"Target not allowed: {request.ServiceName} -> {request.RemoteHost}:{request.RemotePort}";

                    await TunnelProtocol.WriteOpenResponseAsync(quicStream, false, error, ct);
                    Console.WriteLine($"[LocalNet] Rejected remote request: {error}");
                    return;
                }

                using var tcpClient = new TcpClient
                {
                    NoDelay = true
                };

                try
                {
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectCts.CancelAfter(TimeSpan.FromMilliseconds(_config.ConnectTimeoutMs));

                    await tcpClient.ConnectAsync(
                        request.RemoteHost,
                        request.RemotePort,
                        connectCts.Token);

                    await TunnelProtocol.WriteOpenResponseAsync(quicStream, true, null, ct);

                    Console.WriteLine(
                        $"[LocalNet] Peer connected to local {request.RemoteHost}:{request.RemotePort} for {request.ServiceName}.");

                    using NetworkStream tcpStream = tcpClient.GetStream();
                    await PipeBothWaysAsync(quicStream, tcpStream, ct);
                }
                catch (OperationCanceledException)
                {
                    await TrySendErrorAsync(quicStream, "Connection timed out or was cancelled.");
                }
                catch (Exception ex)
                {
                    await TrySendErrorAsync(quicStream, ex.Message);
                    Console.WriteLine(
                        $"[LocalNet] Could not connect to {request.RemoteHost}:{request.RemotePort}: {ex.Message}");
                }
            }
            finally
            {
                await quicStream.DisposeAsync();
            }
        }

        private bool IsAllowedTarget(OpenRequest request)
        {
            if (!_servicesByName.TryGetValue(request.ServiceName, out var service))
                return false;

            if (!string.Equals(service.Type, "tcp", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(service.RemoteHost, request.RemoteHost, StringComparison.OrdinalIgnoreCase))
                return false;

            return service.RemotePort == request.RemotePort;
        }

        private static async Task TrySendErrorAsync(QuicStream stream, string error)
        {
            try
            {
                await TunnelProtocol.WriteOpenResponseAsync(stream, false, error, CancellationToken.None);
            }
            catch
            {
            }
        }

        private static async Task PipeBothWaysAsync(Stream left, Stream right, CancellationToken ct)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var leftToRight = CopyOneWayAsync(left, right, linkedCts.Token);
            var rightToLeft = CopyOneWayAsync(right, left, linkedCts.Token);

            var finished = await Task.WhenAny(leftToRight, rightToLeft);
            linkedCts.Cancel();

            try
            {
                await finished;
            }
            catch
            {
            }
        }

        private static async Task CopyOneWayAsync(Stream source, Stream destination, CancellationToken ct)
        {
            try
            {
                await source.CopyToAsync(destination, 64 * 1024, ct);
                await destination.FlushAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    internal readonly record struct OpenRequest(
        string ServiceName,
        string RemoteHost,
        int RemotePort);

    internal readonly record struct OpenResponse(
        bool Success,
        string? Error);

    internal static class TunnelProtocol
    {
        private static readonly byte[] Magic =
        [
            (byte)'Q',
            (byte)'L',
            (byte)'N',
            (byte)'1'
        ];

        public static async Task WriteOpenRequestAsync(
            Stream stream,
            string serviceName,
            string remoteHost,
            int remotePort,
            CancellationToken ct)
        {
            await stream.WriteAsync(Magic, ct);
            await WriteStringAsync(stream, serviceName, ct);
            await WriteStringAsync(stream, remoteHost, ct);
            await WriteUInt16Async(stream, checked((ushort)remotePort), ct);
            await stream.FlushAsync(ct);
        }

        public static async Task<OpenRequest> ReadOpenRequestAsync(Stream stream, CancellationToken ct)
        {
            var magic = new byte[Magic.Length];
            await stream.ReadExactlyAsync(magic.AsMemory(), ct);

            if (!magic.SequenceEqual(Magic))
                throw new InvalidDataException("Invalid LocalNet stream magic.");

            string serviceName = await ReadStringAsync(stream, ct);
            string remoteHost = await ReadStringAsync(stream, ct);
            ushort remotePort = await ReadUInt16Async(stream, ct);

            return new OpenRequest(serviceName, remoteHost, remotePort);
        }

        public static async Task WriteOpenResponseAsync(
            Stream stream,
            bool success,
            string? error,
            CancellationToken ct)
        {
            await WriteByteAsync(stream, success ? (byte)0 : (byte)1, ct);

            if (!success)
                await WriteStringAsync(stream, error ?? "Unknown error.", ct);

            await stream.FlushAsync(ct);
        }

        public static async Task<OpenResponse> ReadOpenResponseAsync(Stream stream, CancellationToken ct)
        {
            byte code = await ReadByteAsync(stream, ct);

            if (code == 0)
                return new OpenResponse(true, null);

            string error = await ReadStringAsync(stream, ct);
            return new OpenResponse(false, error);
        }

        private static async Task WriteByteAsync(Stream stream, byte value, CancellationToken ct)
        {
            byte[] buffer = [value];
            await stream.WriteAsync(buffer, ct);
        }

        private static async Task<byte> ReadByteAsync(Stream stream, CancellationToken ct)
        {
            byte[] buffer = new byte[1];
            await stream.ReadExactlyAsync(buffer.AsMemory(), ct);
            return buffer[0];
        }

        private static async Task WriteUInt16Async(Stream stream, ushort value, CancellationToken ct)
        {
            byte[] buffer = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
            await stream.WriteAsync(buffer, ct);
        }

        private static async Task<ushort> ReadUInt16Async(Stream stream, CancellationToken ct)
        {
            byte[] buffer = new byte[2];
            await stream.ReadExactlyAsync(buffer.AsMemory(), ct);
            return BinaryPrimitives.ReadUInt16BigEndian(buffer);
        }

        private static async Task WriteStringAsync(Stream stream, string value, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);

            if (bytes.Length > ushort.MaxValue)
                throw new InvalidDataException("String is too long for LocalNet protocol.");

            await WriteUInt16Async(stream, (ushort)bytes.Length, ct);
            await stream.WriteAsync(bytes, ct);
        }

        private static async Task<string> ReadStringAsync(Stream stream, CancellationToken ct)
        {
            ushort length = await ReadUInt16Async(stream, ct);
            byte[] bytes = new byte[length];

            await stream.ReadExactlyAsync(bytes.AsMemory(), ct);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    [JsonSerializable(typeof(LocalNetConfig))]
    [JsonSourceGenerationOptions(WriteIndented = true)]
    internal sealed partial class LocalNetJsonContext : JsonSerializerContext
    {
    }
}
