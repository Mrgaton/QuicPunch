using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace QuicPunch.PacketHandler
{
    internal class PingHandler
    {
        internal static void HandlePing(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result)
        {
            try
            {
                if (r.BaseStream.Position >= r.BaseStream.Length) return;

                bool isResponse = r.ReadByte() > 0;

                byte[] idBytes = r.ReadBytes(16);
                if (idBytes.Length < 16) return;
                var senderPeerId = new Guid(idBytes);

                if (r.BaseStream.Position + 8 > r.BaseStream.Length) return;
                long timestamp = r.ReadInt64();

                if (isResponse)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsedMs = (now - timestamp) * 1000.0 / Stopwatch.Frequency;

                    if (elapsedMs >= 0 && elapsedMs < 60000)
                    {
                        if (qc.AvailablePeers.TryGetValue(senderPeerId, out var peer))
                        {
                            peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(elapsedMs, 1)));
                            peer.LastSeen = DateTime.Now;
                        }
                        else
                        {
                            var matched = qc.AvailablePeers.Values.FirstOrDefault(p =>
                                (p.ActiveEndPoint != null && p.ActiveEndPoint.Equals(result.RemoteEndPoint)) ||
                                (p.Addresses != null && p.Addresses.Any(a => a.Equals(result.RemoteEndPoint.Address))));

                            if (matched != null)
                            {
                                matched.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(elapsedMs, 1)));
                                matched.LastSeen = DateTime.Now;
                            }
                        }
                    }
                }
                else
                {
                    if (qc.AvailablePeers.TryGetValue(senderPeerId, out var peer))
                    {
                        peer.LastSeen = DateTime.Now;
                    }

                    byte[] pingResp = qc.BuildPingPacket(timestamp, true);
                    _ = udp.SendAsync(pingResp, result.RemoteEndPoint);
                }
            }
            catch (Exception ex)
            {
                QuicPunch.WriteLine($"[PingHandler] Error processing ping: {ex.Message}");
            }
        }
    }
}
