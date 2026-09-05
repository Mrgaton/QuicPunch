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

                long nowTicks = PreciseTime.GetCorrectTime().Ticks;
                long diffTicks = nowTicks - timestamp;

                if (isResponse)
                {
                    // Discard responses with future timestamps or taking longer than 5 seconds (50,000,000 ticks)
                    if (diffTicks < 0 || diffTicks > 50_000_000)
                    {
                        return;
                    }

                    if (timestamp <= peer.LastSeenPingTimestamp)
                    {
                        return;
                    }
                    peer.LastSeenPingTimestamp = timestamp;

                    double elapsedMs = diffTicks / 10_000.0;

                    if (elapsedMs >= 0 && elapsedMs <= 5000)
                    {
                        peer.Ping = TimeSpan.FromMilliseconds(Math.Max(0.1, Math.Round(elapsedMs, 1)));
                        peer.LastPingResponseUtc = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Discard incoming ping requests whose timestamp drifts more than 5 seconds from current time
                    if (Math.Abs(diffTicks) > 50_000_000)
                    {
                        return;
                    }

                    // Ping is intentionally lightweight and unauthenticated, so it must
                    // never keep a discovered peer alive. Authenticated Hello/Data/handshake
                    // traffic is responsible for liveness.
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
