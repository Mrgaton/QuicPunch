using System.IO.Compression;
using System.Net.Quic;
using System.Text;

namespace QuicPunch
{
    internal class ChatHandler : QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public ushort PreferredPort => 0; 
        public string ProtocolName => "Chat";

        public ZstandardCompressionOptions? CompressionOptions => null; // new ZstandardCompressionOptions() { AppendChecksum = false, EnableLongDistanceMatching = false, Quality = 11 };

        public async Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[P2P CHAT] Peer: {peer.Name} ({peer.ActiveEndPoint}) failed or denied the conection.");
        }
        public async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            Console.WriteLine("\n--- P2P CHAT STARTED (Type a message and press Enter) ---");
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();


                        if (line == null)
                        {
                            Console.WriteLine("Line null detected.");
                            break;
                        }

                        if (line == "\0") continue;

                        Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"[{peer.Name}]: {line}"); Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QUIC] Error reading from peer: {ex.ToString()}");
                }
                Console.WriteLine("\n[QUIC] Peer disconnected. Press Enter to exit."); Environment.Exit(0);
            });

            while (!ct.IsCancellationRequested)
            {
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;
                try { await writer.WriteLineAsync(input); } catch { break; }
            }
        }

    }
}
