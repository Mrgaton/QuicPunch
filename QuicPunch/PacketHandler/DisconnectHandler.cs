using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using QuicPunch.Helpers;
using TransportType = QuicPunch.QuicPunch.TransportType;

namespace QuicPunch.PacketHandler
{
    internal class DisconnectHandler
    {
        internal static void HandleDisconnect(QuicPunch qc, BinaryReader r, UdpClient? udp, EndPoint remoteEndPoint, byte[] buffer, TransportType transport = TransportType.Wan, TorQuicConnectionManager? torChannel = null)
        {
            try
            {
                int payloadLength = QuicPunch.MagicHeader.Length + 1 + 16 + sizeof(long);
                if (buffer.Length < payloadLength + CertManager.SignatureLength)
                {
                    QuicPunch.WriteLine($"[DisconnectHandler] Packet from {remoteEndPoint} rejected: invalid length.");
                    return;
                }

                var peerId = new Guid(r.ReadBytes(16));
                long ticks = r.ReadInt64();

                long nowTicks = PreciseTime.GetCorrectTime().Ticks;
                long diffTicks = nowTicks - ticks;
                if (Math.Abs(diffTicks) > 300_000_000) // 30 seconds maximum clock drift tolerance
                {
                    QuicPunch.WriteLine($"[DisconnectHandler] Packet from {remoteEndPoint} rejected: timestamp drifted by {diffTicks / 10_000.0}ms.");
                    return;
                }

                if (qc.LastSeenDisconnectTicks.TryGetValue(peerId, out long lastTicks) && ticks <= lastTicks)
                {
                    QuicPunch.WriteLine($"[DisconnectHandler] Replay attack rejected from {remoteEndPoint}: timestamp {ticks} <= last seen {lastTicks}.");
                    return;
                }

                PeerInfo? peer = null;
                if (!qc.AvailablePeers.TryGetValue(peerId, out peer))
                {
                    peer = qc.AvailablePeers.Values.FirstOrDefault(p =>
                        (p.ActiveEndPoint != null && p.ActiveEndPoint.Equals(remoteEndPoint)) ||
                        (p.Addresses != null && remoteEndPoint is IPEndPoint ipEp && p.Addresses.Any(a => a.Equals(ipEp.Address))));
                }

                if (peer == null || peer.Certificate == null)
                {
                    QuicPunch.WriteLine($"[DisconnectHandler] Unrecognized peer or missing certificate for {remoteEndPoint}.");
                    return;
                }

                byte[] signature = new byte[CertManager.SignatureLength];
                Buffer.BlockCopy(buffer, payloadLength, signature, 0, CertManager.SignatureLength);

                using var ecdsa = peer.Certificate.GetECDsaPublicKey();
                if (ecdsa == null || !ecdsa.VerifyData(buffer.AsSpan(0, payloadLength), signature, HashAlgorithmName.SHA3_256))
                {
                    QuicPunch.WriteLine($"[DisconnectHandler] Invalid disconnect signature from {remoteEndPoint}");
                    return;
                }

                qc.LastSeenDisconnectTicks.AddOrUpdate(peerId, ticks, (_, old) => Math.Max(old, ticks));

                if (qc.AvailablePeers.TryRemove(peer.Id, out var removedPeer))
                {
                    QuicPunch.WriteLine($"Peer {removedPeer.Name} ({removedPeer.Id}) sent authenticated disconnect signal.");
                    _ = qc.CloseAllPeerSessionsAsync(removedPeer.Id);
                    removedPeer.Dispose();
                    if (qc.AvailablePeers.IsEmpty)
                    {
                        qc.GetCertManager(transport).RenewSessionEntropy();
                    }
                    qc.RaisePeerDisconnected(removedPeer);
                }
            }
            catch (Exception ex)
            {
                QuicPunch.WriteLine($"[DisconnectHandler] Error processing disconnect: {ex.Message}");
            }
        }
    }
}
