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
        internal static async void HandleAck(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result)
        {
            if (!qc.AcceptSharedPeers)
                return;

            if (qc.AvilablePeers.TryGetValue(result.RemoteEndPoint, out PeerInfo ackPeer))
            {
                var peersCount = r.ReadUInt16();
                Dictionary<IPEndPoint, byte[]> remotePeersCertHashes = new Dictionary<IPEndPoint, byte[]>(peersCount);

                for (int i = 0; i < peersCount; i++)
                {
                    IPAddress ip = new IPAddress(r.ReadBytes(4));
                    ushort port = r.ReadUInt16();
                    var peerCertHash = r.ReadBytes(qc.CurrentPeer.CertHash.Length);
                    remotePeersCertHashes.Add(new IPEndPoint(ip, port), peerCertHash);
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

                foreach (var newPeer in remotePeersCertHashes)
                {
                    if (!qc.AvilablePeers.TryGetValue(newPeer.Key, out _))
                    {
                        //TODO use the cert hashes
                        qc.ExpectedPeerCert.TryAdd(newPeer.Key, newPeer.Value);

                        _ = qc.PeerInterogation(newPeer.Key, default);

                        peersUpdated = true;
                    }
                }

                if (peersUpdated && qc.SharePeers)
                {
                    var ack = qc.GenerateAck(qc.SharePeers);

                    foreach (var peer in qc.AvilablePeers.Select(p => p.Value))
                    {
                        udp.SendAsync(ack, peer.EndPoint);
                    }
                }
            }
        }
    }
}
