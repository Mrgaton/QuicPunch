using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QP = QuicPunch.QuicPunch;

namespace QuicPunchTests.Tests;

internal static class DiscoveryPresenceTests
{
    // A real loopback WebSocket relay: tests the signed wire exchange without any public relay.
    private sealed class Relay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> _clients = new();
        private readonly ConcurrentBag<Task> _workers = new();
        private readonly Task _accept;
        internal int Events;
        internal readonly ConcurrentDictionary<string, DateTime> AnnouncementTimes = new();
        internal string Url { get; }
        internal Relay()
        {
            _listener.Start();
            Url = "ws://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try { while (!_stop.IsCancellationRequested) _workers.Add(ServeAsync(await _listener.AcceptTcpClientAsync(_stop.Token))); }
            catch (OperationCanceledException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                WebSocket? socket = null;
                try
                {
                    var stream = client.GetStream();
                    var header = new StringBuilder();
                    var one = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        await stream.ReadExactlyAsync(one, _stop.Token); header.Append((char)one[0]);
                        if (header.Length > 8192) throw new InvalidDataException("Oversized upgrade");
                    }
                    string key = header.ToString().Split("\r\n").Single(s => s.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                    string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), _stop.Token);
                    socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10));
                    _clients[socket] = new SemaphoreSlim(1, 1);
                    byte[] buffer = new byte[65536];
                    while (!_stop.IsCancellationRequested)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult part;
                        do
                        {
                            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                            if (part.MessageType == WebSocketMessageType.Close) return;
                            message.Write(buffer, 0, part.Count);
                        } while (!part.EndOfMessage);
                        using var json = JsonDocument.Parse(message.ToArray());
                        var data = json.RootElement;
                        if (data[0].GetString() != "EVENT") continue;
                        AnnouncementTimes[data[1].GetProperty("pubkey").GetString()!] =
                            DateTimeOffset.FromUnixTimeSeconds(data[1].GetProperty("created_at").GetInt64()).UtcDateTime;
                        Interlocked.Increment(ref Events);
                        var senderGate = _clients[socket];
                        await senderGate.WaitAsync(_stop.Token);
                        try { await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("[\"OK\"," + JsonSerializer.Serialize(data[1].GetProperty("id").GetString()) + ",true,\"\"]")), WebSocketMessageType.Text, true, _stop.Token); }
                        finally { senderGate.Release(); }
                        byte[] response = Encoding.UTF8.GetBytes("[\"EVENT\",\"qp-discovery\"," + data[1].GetRawText() + "]");
                        foreach (var other in _clients)
                        {
                            await other.Value.WaitAsync(_stop.Token);
                            try { if (other.Key.State == WebSocketState.Open) await other.Key.SendAsync(new ArraySegment<byte>(response), WebSocketMessageType.Text, true, _stop.Token); }
                            catch (WebSocketException) { }
                            finally { other.Value.Release(); }
                        }
                    }
                }
                catch (Exception) when (_stop.IsCancellationRequested || socket?.State != WebSocketState.Open) { }
                finally { if (socket != null) { _clients.TryRemove(socket, out _); socket.Dispose(); } }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop(); foreach (var socket in _clients.Keys) socket.Abort();
            await _accept; try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-presence-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var relay = new Relay();
            await using var a = new QP(appDataPath: Path.Combine(root, "a")) { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            await using var b = new QP(appDataPath: Path.Combine(root, "b")) { WanEnabled = false, EnableUpnp = false, PeerTransport = new() { AutoConnectDiscoveredPeers = false } };
            a.Discovery.NostrRelays = [relay.Url]; b.Discovery.NostrRelays = [relay.Url];
            a.ReputationFilter?.Dispose(); b.ReputationFilter?.Dispose();
            a.ReputationFilter = null; b.ReputationFilter = null;
            await a.StartAsync(); await b.StartAsync();
            a.CurrentPeer.Addresses = [IPAddress.Parse("198.51.100.31")]; a.CurrentPeer.PortArray = [45678];
            b.CurrentPeer.Addresses = [IPAddress.Parse("198.51.100.32")]; b.CurrentPeer.PortArray = [45679];
            a.SavePeer(b.GetWanToken(), autoConnect: false); b.SavePeer(a.GetWanToken(), autoConnect: false);
            a.Discovery.NostrRelays = [relay.Url]; b.Discovery.NostrRelays = [relay.Url];
            await a.Discovery.SetWanPeerDiscoveryEnabledAsync(true);
            await b.Discovery.SetWanPeerDiscoveryEnabledAsync(true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var elapsed = Stopwatch.StartNew();
            while (a.Discovery.DiscoveredPeers.IsEmpty || b.Discovery.DiscoveredPeers.IsEmpty) await Task.Delay(20, deadline.Token);
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new Exception("Reciprocal discovery waited for the periodic publish interval.");
            long discoveryMs = elapsed.ElapsedMilliseconds;
            await Task.Delay(1300, deadline.Token);
            foreach (var (local, remote) in new[] { (a, b), (b, a) })
            {
                var discovered = local.Discovery.DiscoveredPeers[Convert.ToHexString(remote.CurrentPeer.CertHash)];
                DateTime announced = relay.AnnouncementTimes[remote.CertManager.NostrPublicKeyHex];
                if (discovered.LastNostrAnnouncementUtc != announced ||
                    discovered.DiscoveryAvailableUntilUtc != announced + QuicPunch.NostrDiscovery.AnnouncementMaxAge)
                    throw new Exception("Discovery availability lost the original signed announcement time.");
                using var decoded = QuicPunch.Helpers.Utilities.DecodeEndpointToken(remote.GetWanToken());
                decoded.IsCertVerified = true;
                local.Discovery.RecordDiscoveredPeer(decoded, remote.GetWanToken(), announcedAtUtc: announced.AddMinutes(-1));
                if (discovered.LastNostrAnnouncementUtc != announced)
                    throw new Exception("An older announcement changed the current availability deadline.");
            }
            if (a.Discovery.WanNostrDiscovery?.LastAcknowledgedUtc == null || b.Discovery.WanNostrDiscovery?.LastAcknowledgedUtc == null) throw new Exception("Relay did not acknowledge our announcements.");
            int settled = relay.Events;
            await Task.Delay(1200, deadline.Token);
            if (relay.Events != settled || settled > 6) throw new Exception("Reciprocal presence created an announcement echo loop.");
            var original = a.Discovery.GetWanNostrDiscoveryPayload();
            await a.Discovery.RestartWanNostrDiscoveryAsync(CancellationToken.None);
            if (a.Discovery.GetWanNostrDiscoveryPayload() == original) throw new Exception("Restart did not rotate the presence incarnation.");
            int beforeRestart = settled;
            while (relay.Events < beforeRestart + 2) await Task.Delay(20, deadline.Token);
            Console.WriteLine($"PASS signed reciprocal discovery in {discoveryMs} ms: original timestamp and availability deadline, older announcements, unchanged endpoints, restart request, bounded replies, real loopback relay");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
