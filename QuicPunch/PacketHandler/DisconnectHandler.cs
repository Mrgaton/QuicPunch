using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace QuicPunch.PacketHandler
{
    internal class DisconnectHandler
    {
        internal static void HandleDisconnect(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result)
        {
            try
            {
                var peerId = new Guid(r.ReadBytes(16));
                if (qc.AvailablePeers.TryRemove(peerId, out var peer))
                {
                    QuicPunch.WriteLine($"Peer {peer.Name} ({peerId}) sent disconnect signal.");
                    qc.RaisePeerDisconnected(peer);
                }
                else
                {
                    var matched = qc.AvailablePeers.Values.FirstOrDefault(p =>
                        (p.ActiveEndPoint != null && p.ActiveEndPoint.Equals(result.RemoteEndPoint)) ||
                        (p.Addresses != null && p.Addresses.Any(a => a.Equals(result.RemoteEndPoint.Address))));

                    if (matched != null && qc.AvailablePeers.TryRemove(matched.Id, out var matchedPeer))
                    {
                        QuicPunch.WriteLine($"Peer {matchedPeer.Name} ({matchedPeer.Id}) disconnected via {result.RemoteEndPoint}.");
                        qc.RaisePeerDisconnected(matchedPeer);
                    }
                }
            }
            catch (Exception ex)
            {
                QuicPunch.WriteLine($"[DisconnectHandler] Error processing disconnect: {ex.Message}");
            }
        }
    }
}
