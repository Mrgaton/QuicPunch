using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace UdpTrackerParasite
{
    class OldProgramTrackerExample
    {
        // Generación moderna y directa del Hash y el PeerID
        private static readonly byte[] InfoHash = SHA1.HashData(File.ReadAllBytes(Process.GetCurrentProcess().MainModule.FileName));
        private static readonly byte[] PeerId = GetRandomBytes(20);
        private static int LocalPort = Random.Shared.Next(1,short.MaxValue);

        private static readonly string PublicTrackers =@"
            tracker.opentrackr.org:1337
            exodus.desync.com:6969
            open.stealth.si:80
            tracker.internetwarriors.net:1337
            tracker.torrent.eu.org:451";

        private static readonly ConcurrentDictionary<string, DateTime> DiscoveredPeers = new();

        private static readonly List<IPEndPoint> ResolvedTrackers = new();

        private static async Task LoadTrackersList(string list)
        {
            foreach (var t in list.Split('\n').Select(e => e.Trim()))
            {
                try
                {
                    if (t.StartsWith("udp://", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var uri = new Uri(t);

                        var ips = await Dns.GetHostAddressesAsync(uri.Host);

                        if (ips.Length > 0)
                        {
                            foreach (var ip in ips)
                            {
                                ResolvedTrackers.Add(new IPEndPoint(ip, uri.Port));
                            }
                        }
                    }
                    else
                    {
                        var parts = t.Split(':');

                        var ips = await Dns.GetHostAddressesAsync(parts[0]);

                        if (ips.Length > 0)
                        {
                            foreach (var ip in ips)
                            {
                                ResolvedTrackers.Add(new IPEndPoint(ip, int.Parse(parts[1])));
                            }
                        }
                    }
                }
                catch { }
            }
        }
        static async Task UnMain()
        {
            Console.WriteLine("Iniciando Parasitismo UDP (BEP 15) Optimizado...");
            Console.WriteLine("Resolviendo DNS de Trackers (Solo una vez)...");

            HttpClient client = new HttpClient();

            try
            {
                var trackersContent = await client.GetStringAsync("https://raw.githubusercontent.com/ngosang/trackerslist/refs/heads/master/trackers_all_udp.txt");

                await LoadTrackersList(trackersContent);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al resolver trackers: {ex.Message}\n\nUsing hardcoded list");

                await LoadTrackersList(PublicTrackers);
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            do
            {
                Console.WriteLine($"[ESCANEANDO INTERNET] - {DateTime.Now:HH:mm:ss}");

                // Disparamos usando la lista de Endpoints ya resueltos
                var tasks = ResolvedTrackers.Select(ParasiteTracker);

                await Task.WhenAll(tasks);

                CleanupPeers();

                Console.Clear();
                Console.WriteLine("\n-----------------------------------------------------");
                Console.WriteLine($"[✓] PEERS FANTASMAS ENCONTRADOS: {DiscoveredPeers.Count}");
                foreach (var peer in DiscoveredPeers.Keys)
                {
                    Console.WriteLine($"      -> {peer} Ultima vez: {DiscoveredPeers[peer]}");
                }

            } while (await timer.WaitForNextTickAsync());
        }

        private static async Task ParasiteTracker(IPEndPoint endpoint)
        {
            try
            {
                using var udp = new UdpClient();
                int transactionId = Random.Shared.Next(); // Random estático ultra rápido

                byte[] connectReq = new byte[16];
                BinaryPrimitives.WriteInt64BigEndian(connectReq, 0x41727101980L); // Magic Constant
                BinaryPrimitives.WriteInt32BigEndian(connectReq.AsSpan(8), 0);    // Action: 0 (Connect)
                BinaryPrimitives.WriteInt32BigEndian(connectReq.AsSpan(12), transactionId);

                await udp.SendAsync(connectReq, connectReq.Length, endpoint);

                var connectRes = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));

                // Verificamos Action == 0 (Validación limpia)
                if (connectRes.Buffer.Length < 16 || BinaryPrimitives.ReadInt32BigEndian(connectRes.Buffer) != 0)
                    return;

                long connectionId = BinaryPrimitives.ReadInt64BigEndian(connectRes.Buffer.AsSpan(8));

                // --- PASO 2: REQUEST DE ANUNCIO ---
                byte[] announceReq = new byte[98];
                var span = announceReq.AsSpan(); // Span evita copias redundantes en memoria

                BinaryPrimitives.WriteInt64BigEndian(span, connectionId);
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(8), 1);              // Action: 1 (Announce)
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(12), transactionId);

                InfoHash.CopyTo(span.Slice(16, 20));
                PeerId.CopyTo(span.Slice(36, 20));

                BinaryPrimitives.WriteInt32BigEndian(span.Slice(80), 2);             // Event: 2 (Started)
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(88), Random.Shared.Next()); // Key aleatoria
                BinaryPrimitives.WriteInt32BigEndian(span.Slice(92), -1);            // NumWant: -1
                BinaryPrimitives.WriteInt16BigEndian(span.Slice(96), (short)LocalPort);

                await udp.SendAsync(announceReq, announceReq.Length, endpoint);

                var announceRes = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));

                var resSpan = announceRes.Buffer.AsSpan();

                if (resSpan.Length < 20 || BinaryPrimitives.ReadInt32BigEndian(resSpan) != 1) return;

                int peerCount = (resSpan.Length - 20) / 6;

                for (int i = 0; i < peerCount; i++)
                {
                    var pSpan = resSpan.Slice(20 + (i * 6), 6);
                    var ipSpan = pSpan.Slice(0, 4);

                    if ((byte)ipSpan[0] is 192 or 127 or 172 or 10 or 0)
                    {
                        continue;
                    }
                    
                    var ip = new IPAddress(ipSpan);
                    var port = BinaryPrimitives.ReadUInt16BigEndian(pSpan.Slice(4, 2));


                    var newEntry = $"{ip}:{port}";

                    if (!DiscoveredPeers.ContainsKey(newEntry))
                    {
                        //Event of new user 
                    }

                    DiscoveredPeers[newEntry] = DateTime.UtcNow;
                }
            }
            catch {  }
        }

        private static byte[] GetRandomBytes(int length)
        {
            var b = new byte[length];
            Random.Shared.NextBytes(b);
            return b;
        }

        private static void CleanupPeers()
        {
            var limit = DateTime.UtcNow.AddSeconds(-60);

            foreach (var kvp in DiscoveredPeers)
            {
                if (kvp.Value < limit)
                    DiscoveredPeers.TryRemove(kvp.Key, out _);
            }
        }
    }
}