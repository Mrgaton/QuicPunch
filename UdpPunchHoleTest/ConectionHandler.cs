using QuicPunch;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace UdpPunchHoleTest
{
    internal class ConectionHandler
    {
       /* public static async Task HandleConnection(PeerInfo pinfo)
        {
            using var mainCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; mainCts.Cancel(); };

            var res = await QuicPunchCore.InitPeerConection(pinfo, mainCts);
            await RunChat(res.Item2, mainCts.Token);
        }*/

        private static async Task RunChat(Stream stream, CancellationToken token)
        {
            Console.WriteLine("\n--- QUIC SECURE P2P CHAT STARTED (Type a message and press Enter) ---");
            var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();


                        if (line == null)
                        {
                            Console.WriteLine("Line null detected.");
                            //break;
                        }

                        if (line == "\0") continue;

                        Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"[Peer]: {line}"); Console.ResetColor();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QUIC] Error reading from peer: {ex.ToString()}");
                }
                Console.WriteLine("\n[QUIC] Peer disconnected. Press Enter to exit."); Environment.Exit(0);
            });

            while (!token.IsCancellationRequested)
            {
                var input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;
                try { await writer.WriteLineAsync(input); } catch { break; }
            }
        }

    }
}
