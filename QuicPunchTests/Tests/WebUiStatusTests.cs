using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuicNet.WebUi.Modules;
using QuicPunch;

namespace QuicPunchTests.Tests;

internal static class WebUiStatusTests
{
    public static async Task RunAsync()
    {
        using var fixture = new StatusFixture();
        fixture.SetContacts(0);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.Server.Url) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using (JsonDocument empty = await GetAsync(client, "api/status", deadline.Token))
            CheckEnvelope(empty.RootElement, 0, 0, expanded: false);

        fixture.SetContacts(3);
        var timestamp = DateTime.UtcNow;
        WebUiServer.ChatMessages.Enqueue(new("peer", "test-message", "Fixture", "hello", timestamp, false, true));
        WebUiServer.ClipboardItems.Enqueue(new("peer", "Fixture", "test clipboard", timestamp, false));

        using (JsonDocument http = await GetAsync(client, "api/status", deadline.Token))
            CheckEnvelope(http.RootElement, 3, 3, expanded: false);
        using (JsonDocument expanded = await GetAsync(client, "api/status?include=CHAT,%20clipboard", deadline.Token))
            CheckEnvelope(expanded.RootElement, 3, 3, expanded: true);

        using var socket = new ClientWebSocket();
        var address = new UriBuilder(fixture.Server.Url) { Scheme = "ws", Path = "/ws", Query = "csrf=" + fixture.Server.CsrfToken };
        await socket.ConnectAsync(address.Uri, deadline.Token);
        await socket.SendAsync("""{"type":"request","id":"status-contract","command":"status.get"}"""u8.ToArray(), WebSocketMessageType.Text, true, deadline.Token);
        using (JsonDocument reply = await ReadSocketAsync(socket, "response", deadline.Token))
        {
            var root = reply.RootElement;
            Check(root.GetProperty("id").GetString() == "status-contract" && root.GetProperty("success").GetBoolean(), "status.get failed.");
            CheckEnvelope(root.GetProperty("data"), 3, 3, expanded: false);
        }

        await socket.SendAsync("""{"type":"get_status"}"""u8.ToArray(), WebSocketMessageType.Text, true, deadline.Token);
        using (JsonDocument update = await ReadSocketAsync(socket, "status_updated", deadline.Token))
            CheckEnvelope(update.RootElement.GetProperty("data"), 3, 3, expanded: false);

        Check(QuicNet.Serialization.SharedJsonContext.Default.GetTypeInfo(fixture.ProjectStatus().GetType()) != null,
            "The status DTO is missing from the application's source-generated JSON context.");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
    }

    public static Task BenchmarkAsync()
    {
        using var fixture = new StatusFixture();
        foreach (int count in new[] { 0, 50, 500 })
        {
            fixture.SetContacts(count);
            Measure(count, "projection", fixture.ProjectStatus);
            Measure(count, "serialize", () => JsonSerializer.SerializeToUtf8Bytes(fixture.ProjectStatus(), WebUiContext.JsonOptions));
        }

        return Task.CompletedTask;
    }

    private static void Measure(int contactsPerList, string operation, Func<object> action)
    {
        const int sampleCount = 35;
        for (int i = 0; i < 10; i++) action();

        var samples = new double[sampleCount];
        object result = null!;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            result = action();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(samples);
        Console.WriteLine(FormattableString.Invariant(
            $"STATUS {operation} peers={contactsPerList} discovered={contactsPerList} p50_ms={samples[17]:F3} p95_ms={samples[33]:F3} allocated_bytes={allocated / samples.Length} json_bytes={(result is byte[] json ? json.Length : 0)}"));
    }

    private static async Task<JsonDocument> GetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static async Task<JsonDocument> ReadSocketAsync(ClientWebSocket socket, string type, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                Check(frame.MessageType == WebSocketMessageType.Text, "Expected a text WebSocket message.");
                message.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);

            var json = JsonDocument.Parse(message.ToArray());
            if (json.RootElement.GetProperty("type").GetString() == type) return json;
            json.Dispose();
        }
    }

    private static void CheckEnvelope(JsonElement root, int available, int discovered, bool expanded)
    {
        Check(root.GetProperty("node").GetProperty("id").ValueKind == JsonValueKind.String, "Missing node identity.");
        foreach (var (current, alias) in new[] {
            ("peers", "availablePeers"), ("quicTelemetry", "quicSessions"), ("discovery", "peerDiscovery"),
            ("lan", "lanStatus"), ("tor", "torStatus"), ("wan", "wanStatus"), ("token", "wanToken") })
            Check(JsonElement.DeepEquals(root.GetProperty(current), root.GetProperty(alias)), $"Snapshot alias changed: {alias}.");

        var peers = root.GetProperty("peers");
        var discoveredPeers = root.GetProperty("discoveredPeers");
        Check(peers.GetArrayLength() == available && discoveredPeers.GetArrayLength() == discovered, "Contact counts changed.");
        Check(root.GetProperty("connectedPeers").GetArrayLength() == 0, "Available peers were reported as connected.");
        if (available > 0)
        {
            Check(peers[0].GetProperty("activeTransport").ValueKind == JsonValueKind.Null, "Offline peer transport must be null.");
            Check(discoveredPeers[0].GetProperty("savedName").ValueKind == JsonValueKind.Null, "Unsaved discovered peer must have a null savedName.");
        }

        var messages = root.GetProperty("chatMessages");
        var clipboard = root.GetProperty("clipboardItems");
        Check(messages.GetArrayLength() == (expanded ? 1 : 0) && clipboard.GetArrayLength() == (expanded ? 1 : 0),
            "Chat/clipboard inclusion changed.");
        if (expanded)
            Check(messages[0].GetProperty("msgId").GetString() == "test-message" &&
                clipboard[0].GetProperty("text").GetString() == "test clipboard", "Expanded status returned unexpected data.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class StatusFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "quicnet-status-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _lifetime = new();
        private readonly QuicPunch.QuicPunch _node;
        private readonly VoiceCallHandler _voice;
        private readonly SpeedTestHandler _speedTest = new();
        private readonly StatusApiModule _status;
        public WebUiServer Server { get; }

        public StatusFixture()
        {
            Fixtures.LoopbackOnlyNetwork.Configure();
            _node = new QuicPunch.QuicPunch(_lifetime.Token, updateIpReputationOnlyWithWan: true, appDataPath: _path, listeningPort: 0);
            _voice = new VoiceCallHandler(_node);
            var preferences = new AppPreferencesStore(Path.Combine(_path, "preferences.json"));
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Server = new WebUiServer(_node, new ChatHandler(), null, _voice, _speedTest, preferences, _lifetime, port: port);
            Server.Start();
            _status = (StatusApiModule)typeof(WebUiServer).GetField("_statusModule", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Server)!;
        }

        public void SetContacts(int count)
        {
            ClearContacts();
            for (int i = 0; i < count; i++)
            {
                byte[] activeHash = SHA256.HashData(Encoding.UTF8.GetBytes($"active-{i}"));
                var active = new PeerInfo { Name = $"Peer {i:D4}" }.SetCertificateHash(activeHash);
                _node.AvailablePeers[active.Id] = active;
                byte[] discoveredHash = SHA256.HashData(Encoding.UTF8.GetBytes($"discovered-{i}"));
                string hex = Convert.ToHexString(discoveredHash);
                _node.Discovery.DiscoveredPeers[hex] = new DiscoveredPeerInfo
                {
                    Id = hex,
                    Name = "Discovered Peer",
                    CertHash = discoveredHash,
                    CertHashBase64 = Convert.ToBase64String(discoveredHash),
                    Addresses = ["192.0.2.1"],
                    PortArray = [45001]
                };
            }
        }

        public object ProjectStatus() => _status.GetStatusObject();

        private void ClearContacts()
        {
            foreach (var peer in _node.AvailablePeers.Values) peer.Dispose();
            _node.AvailablePeers.Clear();
            _node.Discovery.DiscoveredPeers.Clear();
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            Server.Stop();
            ClearContacts();
            _speedTest.Dispose();
            _voice.Dispose();
            _node.Dispose();
            _lifetime.Dispose();
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
