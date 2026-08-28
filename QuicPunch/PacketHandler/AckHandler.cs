using QuicPunch.Helpers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TransportType = QuicPunch.QuicPunch.TransportType;

namespace QuicPunch.PacketHandler
{
    internal class AckHandler
    {
        private const int MaxSharedPeersPerAck = 32;

        internal static void HandleAck(QuicPunch qc, BinaryReader r, UdpClient? udp, EndPoint remoteEndPoint, byte[] buffer, TransportType transport = TransportType.Wan, TorQuicConnectionManager? torChannel = null)
        {
            try
            {
                if (!qc.AcceptSharedPeers)
                    return;

                var peerId = new Guid(r.ReadBytes(16));

                if (qc.AvailablePeers.TryGetValue(peerId, out PeerInfo ackPeer))
                {
                    var peersCount = r.ReadUInt16();
                    int clampedCount = Math.Min((int)peersCount, MaxSharedPeersPerAck);
                    List<PeerInfo> remotePeers = new List<PeerInfo>(clampedCount);

                    for (int i = 0; i < peersCount; i++)
                    {
                        PeerInfo pi = new PeerInfo();

                        PackedFlags pf = new PackedFlags(r.ReadByte());
                        pi.NetworkType = pf.NetworkType;

                        byte addressCount = r.ReadByte();

                        IPAddress[] addresses = new IPAddress[addressCount];
                        for (int e = 0 ; e < addressCount; e++)
                        {
                            addresses[e] = new IPAddress(r.ReadBytes(4));
                        }

                        pi.Addresses = addresses;

                        pi.MinPort = r.ReadUInt16();
                        pi.MaxPort = r.ReadUInt16();

                        pi.SetCertificateHash(r.ReadBytes(qc.GetCurrentPeer(transport).CertHash.Length));
                        
                        if (i < clampedCount)
                        {
                            remotePeers.Add(pi);
                        }
                    }

                    long receivedTicks = r.ReadInt64();
                    long nowTicks = PreciseTime.GetCorrectTime().Ticks;

                    long diffTicks = nowTicks - receivedTicks;

                    if (Math.Abs(diffTicks) > 30_000_000)
                    {
                        QuicPunchLog.Info($"ACK: Packet from {remoteEndPoint} rejected. Timestamp drifted by {diffTicks / 10_000.0}ms.");
                        return;
                    }

                    if (receivedTicks <= ackPeer.LastSeenAckTicks)
                    {
                        QuicPunchLog.Info($"ACK: Replay rejected from {remoteEndPoint}: timestamp {receivedTicks} <= last seen {ackPeer.LastSeenAckTicks}.");
                        return;
                    }

                    byte[] signature = new byte[CertManager.SignatureLength];
                    r.ReadExactly(signature);

                    if (!ackPeer.Curve.VerifyData(buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature, HashAlgorithmName.SHA3_256))
                    {
                        QuicPunchLog.Info("ACK: Received invalid signature from " + remoteEndPoint);
                        return;
                    }

                    ackPeer.LastSeenAckTicks = receivedTicks;

                    foreach (var peer in remotePeers)
                    {
                        if (peer.CertHash != null && !qc.AvailablePeers.Values.Any(availablePeer => availablePeer.CertHash != null && CryptographicOperations.FixedTimeEquals(availablePeer.CertHash, peer.CertHash)))
                        {
                            _ = qc.PeerInterrogation(peer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error($"[ACK HANDLER ERROR] From {remoteEndPoint}", ex);
            }
        }
    }
}
