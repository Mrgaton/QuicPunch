using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch.PacketHandler
{
    internal class HelloHandler
    {
        internal static void HandleHello(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result, byte messageType) {
            if (messageType == (byte)MessageType.Interogation)
            {
                udp.SendAsync(qc.GenerateHelloPayload(MessageType.Hello, true), result.RemoteEndPoint);
            }

            var certHash = r.ReadBytes(qc.CurrentPeer.CertHash.Length);

            var foundExpectedCert = qc.ExpectedPeerCerts.TryGetValue(result.RemoteEndPoint.Address, out var helloPeerCertHashes);

            if (!foundExpectedCert || !helloPeerCertHashes.Any(c => c.SequenceEqual(certHash)))
            {
                Console.WriteLine("HELLO INIT: Peer presented unexpected certificate");
                return;
            }

            byte nameSize = r.ReadByte();
            var nameBytes = r.ReadBytes(nameSize);

            var certSize = r.ReadUInt16();
            var certBytes = r.ReadBytes(certSize);

            var cert = new X509Certificate2(certBytes);

            if (!SHA3_384.HashData(cert.GetPublicKey()).SequenceEqual(certHash))
            {
                Console.WriteLine("Corrupted cert hash from " + result.RemoteEndPoint);
                return;
            }

            var passwordConnection = r.ReadByte() > 0;

            if (passwordConnection && qc.PasswordHash == null)
            {
                Console.WriteLine("Peer has password connection but current instant doenst");
                return;
            }
            else if (passwordConnection)
            {
                var remoteTicks = r.ReadInt64();
                long nowTicks = PreciseTime.GetCorrectTime().Ticks;

                long diffTicks = nowTicks - remoteTicks;

                if (Math.Abs(diffTicks) > 30_000_000)
                {
                    Console.WriteLine($"HELLO NEW: Packet from {result.RemoteEndPoint} rejected. Timestamp drifted by {diffTicks / 10_000.0}ms.");
                    return;
                }

                var nonce = r.ReadBytes(32);

                var pop = HMACSHA3_256.HashData(Helpers.Combine(BitConverter.GetBytes(remoteTicks), nonce, result.RemoteEndPoint.Address.GetAddressBytes(), BitConverter.GetBytes((ushort)result.RemoteEndPoint.Port)), qc.PasswordHash);

                var remotePop = r.ReadBytes(256 / 8);

                if (!pop.SequenceEqual(remotePop))
                {
                    Console.WriteLine("Error the peer could not proof the ownership of the password");
                    return;
                }
            }

            byte[] signature = new byte[64];
            r.ReadExactly(signature);

            if (!qc.AvilablePeers.ContainsKey(result.RemoteEndPoint))
            {
                if (qc.PasswordHash != null && !passwordConnection)
                {
                    Console.WriteLine("Error instance has password configured but peer didnt sended one");
                    return;
                }

                var ecdsa = cert.GetECDsaPublicKey();

                var peerInfo = new PeerInfo
                {
                    ActiveEndPoint = result.RemoteEndPoint,
                    CertHash = certHash,
                    Name = Encoding.UTF8.GetString(nameBytes),
                    LastSeen = PreciseTime.GetCorrectTime(),
                    Curve = ecdsa
                };

                if (!peerInfo.Curve.VerifyData(result.Buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature, HashAlgorithmName.SHA3_256))
                {
                    Console.WriteLine("HELLO NEW: Received invalid signature from " + result.RemoteEndPoint);
                    return;
                }

                qc.AvilablePeers[peerInfo.ActiveEndPoint] = peerInfo;
                qc.RaisePeerAvailable(peerInfo);

                if (qc.SharePeers)
                {
                    foreach (var peer in qc.AvilablePeers)
                    {
                        if (peer.Value.ActiveEndPoint.Address.Equals(result.RemoteEndPoint.Address))
                            continue;

                        udp.SendAsync(qc.GenerateAck(qc.SharePeers), result.RemoteEndPoint);
                    }
                }
            }
            else
            {
                var peer = qc.AvilablePeers[result.RemoteEndPoint];

                if (!peer.Curve.VerifyData(result.Buffer.AsSpan(0, (int)r.BaseStream.Position - signature.Length), signature, HashAlgorithmName.SHA3_256))
                {
                    Console.WriteLine("HELLO OLD: Received invalid signature from " + result.RemoteEndPoint);
                    return;
                }

                if (!certHash.SequenceEqual(peer.CertHash))
                {
                    //TODO: IDK what to do enter in panick cause someone is spoofing connections!=!="!"?=)i3?_="!
                    Console.WriteLine("HELLO OLD: Received corrupted cert hash from " + result.RemoteEndPoint);
                    return;
                }
                else
                {
                    if (peer.Name.Length != nameBytes.Length || peer.Name != Encoding.UTF8.GetString(nameBytes))
                    {
                        peer.Name = Encoding.UTF8.GetString(nameBytes);
                    }

                    peer.LastSeen = PreciseTime.GetCorrectTime();
                }
            }

            udp.SendAsync(qc.GenerateAck(qc.SharePeers), result.RemoteEndPoint);
        }
    }
}
