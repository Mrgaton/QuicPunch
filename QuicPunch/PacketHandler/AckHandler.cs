using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace QuicPunch.PacketHandler
{
    internal class AckHandler
    {
        internal static void HandleAck(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result)
        {
            if (!qc.AcceptSharedPeers)
                return;

            if (qc.AvilablePeers.TryGetValue(result.RemoteEndPoint, out PeerInfo ackPeer))
            {
                var peersCount = r.ReadUInt16();
                List<PeerInfo> remotePeers = new List<PeerInfo>(peersCount);

                for (int i = 0; i < peersCount; i++)
                {
                    PeerInfo pi = new PeerInfo();
                    
                    pi.NetworkType = (QuicPunch.NetworkType)r.ReadByte();
                    byte addressCount = r.ReadByte();

                    IPAddress[] addresses = new IPAddress[addressCount];
                    for (int e = 0 ; e < addressCount; e++)
                    {
                        addresses[e] = new IPAddress(r.ReadBytes(4));
                    }

                    pi.Addresses = addresses;
                    
                    pi.MinPort = r.ReadUInt16();
                    pi.MaxPort = r.ReadUInt16();
                    
                    pi.CertHash = r.ReadBytes(qc.CurrentPeer.CertHash.Length); 
                    remotePeers.Add(pi);
                }

                long receivedTicks = r.ReadInt64();
                long nowTicks = PreciseTime.GetCorrectTime().Ticks;

                long diffTicks = nowTicks - receivedTicks;

                if (Math.Abs(diffTicks) > 30_000_000)
                {
                    Console.WriteLine($"ACK: Packet from {result.RemoteEndPoint} rejected. Timestamp drifted by {diffTicks / 10_000.0}ms.");
                    return;
                }

                byte[] signature = new byte[64];
                r.ReadExactly(signature);

                if (!ackPeer.Curve.VerifyData(result.Buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature, HashAlgorithmName.SHA3_256))
                {
                    Console.WriteLine("ACK: Received invalid signature from " + result.RemoteEndPoint);
                    return;
                }

                bool peersUpdated = false;

                foreach (var peer in remotePeers)
                {
                    if (!qc.AvilablePeers.Any(avilablePeer => avilablePeer.Value.CertHash.SequenceEqual(peer.CertHash)))
                    {
                        foreach (var address in peer.Addresses)
                        { 
                          
                        }
 
                        _ = qc.PeerInterogation(peer, default);
                        peersUpdated = true;
                    }
                }

                if (peersUpdated && qc.SharePeers)
                {
                    var ack = qc.GenerateAck(qc.SharePeers);

                    foreach (var peer in qc.AvilablePeers.Select(p => p.Value))
                    {
                        udp.SendAsync(ack, peer.ActiveEndPoint);
                    }
                }
            }
        }
    }
}
