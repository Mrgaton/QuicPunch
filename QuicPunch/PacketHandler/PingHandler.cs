using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using QuicPunch.Helpers;
using TransportType = QuicPunch.QuicPunch.TransportType;

namespace QuicPunch.PacketHandler
{
    internal class PingHandler
    {
        internal static void HandlePing(QuicPunch qc, BinaryReader r, UdpClient? udp, EndPoint remoteEndPoint, TransportType transport = TransportType.Wan, TorQuicConnectionManager? torChannel = null)
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

                if (!qc.AvailablePeers.TryGetValue(senderPeerId, out var peer))
                {
                    return;
                }

                if (isResponse)
                {
                    if (timestamp <= peer.LastSeenPingTimestamp)
                    {
                        return;
                    }
                    peer.LastSeenPingTimestamp = timestamp;

                    long now = Stopwatch.GetTimestamp();
                    double elapsedMs = (now - timestamp) * 1000.0 / Stopwatch.Frequency;

                    if (elapsedMs >= 0 && elapsedMs < 60000)
                    {
                        peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(elapsedMs, 1)));
                        peer.LastSeen = PreciseTime.GetCorrectTime();
                    }
                }
                else
                {
                    peer.LastSeen = PreciseTime.GetCorrectTime();

                    byte[] pingResp = qc.BuildPingPacket(timestamp, true, transport);
                    _ = qc.SendResponseAsync(pingResp, remoteEndPoint, transport, torChannel);
                }
            }
            catch (Exception ex)
            {
                QuicPunch.WriteLine($"[PingHandler] Error processing ping: {ex.Message}");
            }
        }
    }
}
