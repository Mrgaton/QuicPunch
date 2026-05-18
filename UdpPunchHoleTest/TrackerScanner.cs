using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UdpPunchHoleTest
{
    public class TrackerScanner
    {
        private readonly byte[] _infoHash;
        private readonly byte[] _peerId = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).Take(20).ToArray();
        private readonly int _localPort;

        private readonly ConcurrentDictionary<IPEndPoint, DateTime> _peers = new();
        private readonly List<IPEndPoint> _trackers = new();
        private CancellationTokenSource? _cts;

        public event Action<IPEndPoint>? OnPeerFound;

        public IEnumerable<IPEndPoint> ActivePeers => _peers.Keys;

        public TrackerScanner(byte[] infoHash, int localPort)
        {
            if (infoHash.Length != 20) throw new ArgumentException("Hash must be 20 bytes");
            _infoHash = infoHash;

            if (localPort < 0 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort), "Port must be an ushort");
          
            _localPort = localPort;
        }

        public async Task Start(string[]? customTrackers = null)
        {
            _cts = new CancellationTokenSource();

            var list = customTrackers ?? await GetPublicTrackers();

            foreach (var url in list)
            {
                try
                {
                    var uri = url.StartsWith("udp://") ? new Uri(url) : new Uri($"udp://{url}");
                    var addresses = await Dns.GetHostAddressesAsync(uri.Host);
                    foreach (var ip in addresses) _trackers.Add(new IPEndPoint(ip, uri.Port));
                }
                catch {  }
            }

            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                do
                {
                    var tasks = _trackers.OrderBy(e => Random.Shared.Next()).Take(_trackers.Count / 4).Select(ParasiteTracker);

                    await Task.WhenAll(tasks);

                    Cleanup();
                } while (await timer.WaitForNextTickAsync(_cts.Token) && !_cts.IsCancellationRequested);
            });
        }

        public void Stop() => _cts?.Cancel();

        private async Task ParasiteTracker(IPEndPoint endpoint)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.SendTimeout = 3000;
                udp.Client.ReceiveTimeout = 3000;

                int transactionId = Random.Shared.Next();

                // --- CONNECT ---
                byte[] connectReq = new byte[16];
                BinaryPrimitives.WriteInt64BigEndian(connectReq, 0x41727101980L);
                BinaryPrimitives.WriteInt32BigEndian(connectReq.AsSpan(8), 0);
                BinaryPrimitives.WriteInt32BigEndian(connectReq.AsSpan(12), transactionId);

                await udp.SendAsync(connectReq, endpoint);
                var res = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));

                if (res.Buffer.Length < 16)
                    return;

                long connectionId = BinaryPrimitives.ReadInt64BigEndian(res.Buffer.AsSpan(8));

                // --- ANNOUNCE ---
                byte[] announceReq = new byte[98];
                var span = announceReq.AsSpan();
                BinaryPrimitives.WriteInt64BigEndian(span, connectionId);
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(8), 1); // Action: Announce
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(12), transactionId);
                _infoHash.CopyTo(span.Slice(16));
                _peerId.CopyTo(span.Slice(36));
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(80), 2); // Event: Started
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(92), -1); // NumWant
                BinaryPrimitives.WriteInt16BigEndian(span.Slice(96), (short)_localPort);

                await udp.SendAsync(announceReq, endpoint);
                var annRes = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));

                ProcessPeers(annRes.Buffer);
            }
            catch { /* Timeout o error de red */ }
        }

        private void ProcessPeers(byte[] buffer)
        {
            if (buffer.Length < 20) return;
            var peerData = buffer.AsSpan(20);
            for (int i = 0; i < peerData.Length / 6; i++)
            {
                var p = peerData.Slice(i * 6, 6);
                if ((p[0] is 0 or 127 or 10 or 192 or 127) || p[0] >=224) continue;

                var ip = new IPAddress(p.Slice(0, 4).ToArray());
                
                var endpoint = new IPEndPoint(ip, BinaryPrimitives.ReadUInt16BigEndian(p.Slice(4)));

                if (endpoint.Address.Equals(QuicPunchCore.IPv4Address) && endpoint.Port == _localPort) 
                    continue;

                if (_peers.TryAdd(endpoint, DateTime.UtcNow))
                    OnPeerFound?.Invoke(endpoint);
                else
                    _peers[endpoint] = DateTime.UtcNow;
            }
        }

        private void Cleanup()
        {
            var limit = DateTime.UtcNow.AddMinutes(-1);
            foreach (var (key, value) in _peers)
                if (value < limit) _peers.TryRemove(key, out _);
        }

        private async Task<string[]> GetPublicTrackers()
        {
            try
            {
                using var http = new HttpClient();
                var data = await http.GetStringAsync("https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_all_udp.txt");
                return data.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch { return Array.Empty<string>(); }
        }
    }
}
