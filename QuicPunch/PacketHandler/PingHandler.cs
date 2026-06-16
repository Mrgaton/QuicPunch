using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.Intrinsics.Arm;
using System.Text;

namespace QuicPunch.PacketHandler
{
    internal class PingHandler
    {
        internal static void HandlePing(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result)
        {
            bool secondTimestamp = r.ReadByte() > 0;
            long t1 = r.ReadInt64();

            if (secondTimestamp)
            {
                long t2 = r.ReadInt64();

                if (!qc.AvilablePeers.TryGetValue(result.RemoteEndPoint, out PeerInfo peer))
                    return;

                peer.UpTicks = t2 - t1;
                peer.DownTicks = PreciseTime.GetCorrectTime().Ticks - t2;
                peer.Ping = TimeSpan.FromTicks(PreciseTime.GetCorrectTime().Ticks - t1);
            }
            else
            {
                udp.SendAsync(qc.BuildPingPacket(t1, PreciseTime.GetCorrectTime().Ticks), result.RemoteEndPoint);
            }
        }
    }
}
