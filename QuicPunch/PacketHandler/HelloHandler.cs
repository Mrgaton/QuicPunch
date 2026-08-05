using System.IO.Hashing;
using System.Net;
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
            var certHash = r.ReadBytes(qc.CurrentPeer.CertHash.Length);

            if (qc.CurrentPeer.CertHash != null && certHash.SequenceEqual(qc.CurrentPeer.CertHash))
            {
                // Imaginate conocerte a ti mismo
                return;
            }

            var foundExpectedCert = qc.ExpectedPeerCerts.Contains(certHash);

            if (messageType != (byte)MessageType.Interrogation && !foundExpectedCert && !qc.PeerStore.SavedPeers.Any(sp => sp.CertHash.SequenceEqual(certHash)))
            {
                Console.WriteLine("HELLO INIT: Peer presented unexpected certificate");
                return;
            }

            if (!foundExpectedCert)
            {
                qc.ExpectedPeerCerts.Add(certHash);
            }

            if (messageType == (byte)MessageType.Interrogation)
            {
                udp.SendAsync(qc.GenerateHelloPayload(MessageType.Hello, true), result.RemoteEndPoint);
            }

            PackedFlags pf = new PackedFlags(r.ReadByte());

            var addressesAmount = r.ReadByte();
            IPAddress[]  addresses = new IPAddress[addressesAmount];
            for (int i = 0; i < addressesAmount; i++)
            {
                addresses[i] =  new IPAddress(r.ReadBytes(4));
            }

            ushort minPort = r.ReadUInt16();
            ushort maxPort = r.ReadUInt16();

            if (minPort == 0) minPort = (ushort)result.RemoteEndPoint.Port;
            if (maxPort == 0) maxPort = (ushort)result.RemoteEndPoint.Port;
            
            byte nameSize = r.ReadByte();
            var nameBytes = r.ReadBytes(nameSize);

            var certSize = r.ReadUInt16();
            var certBytes = r.ReadBytes(certSize);

            var cert = X509CertificateLoader.LoadCertificate(certBytes);

            var ecdhExt = cert.Extensions[CertManager.EcdhExtensionOid];
            if (ecdhExt == null)
            {
                Console.WriteLine("Certificate missing ECDH extension from " + result.RemoteEndPoint);
                return;
            }
            var ecdhKeyRaw = ecdhExt.RawData;

            if (!SHA3_256.HashData(cert.GetPublicKey()).SequenceEqual(certHash))
            {
                Console.WriteLine("Corrupted cert hash from " + result.RemoteEndPoint);
                return;
            }

            var peerId = new Guid(XxHash128.Hash(certHash));

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

                if (Math.Abs(diffTicks) > 1_200_000_000)
                {
                    Console.WriteLine($"HELLO NEW: Packet from {result.RemoteEndPoint} rejected. Timestamp drifted by {diffTicks / 10_000.0}ms.");
                    return;
                }

                byte[] nonce = r.ReadBytes(24);
                byte[] pop = HMACSHA3_256.HashData(Helpers.Combine(BitConverter.GetBytes(remoteTicks), nonce), qc.PasswordHash);
                byte[] remotePop = r.ReadBytes(256 / 8);

                if (!pop.SequenceEqual(remotePop))
                {
                    Console.WriteLine("Error the peer could not proof the ownership of the password");
                    return;
                }
            }

            int payloadLength = (int)r.BaseStream.Position;
            byte[] signature = new byte[64];
            r.ReadExactly(signature);

            var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa == null || !ecdsa.VerifyData(result.Buffer.AsSpan(0, payloadLength), signature, HashAlgorithmName.SHA3_256))
            {
                Console.WriteLine("HELLO: Received invalid signature from " + result.RemoteEndPoint);
                return;
            }

            if (!qc.AvailablePeers.ContainsKey(peerId))
            {
                if (qc.PasswordHash != null && !passwordConnection)
                {
                    Console.WriteLine("Instance requires password authentication, but peer didn't send proof. Requesting re-authentication from " + result.RemoteEndPoint);
                    var challengePayload = qc.GenerateHelloPayload(MessageType.Interrogation, true);
                    _ = udp.SendAsync(challengePayload, result.RemoteEndPoint);
                    return;
                }

                var peerInfo = new PeerInfo(cert, ecdhKeyRaw)
                {
                    ActiveEndPoint = result.RemoteEndPoint,
                    NetworkType =  pf.NetworkType,
                    
                    Addresses =  addresses,
                    MaxPort = maxPort,
                    MinPort =  minPort,
                    
                    Name = Encoding.UTF8.GetString(nameBytes),
                    LastSeen = PreciseTime.GetCorrectTime()
                };

                peerInfo.InitSession(qc);

                qc.AvailablePeers[peerId] = peerInfo;
                qc.RaisePeerAvailable(peerInfo);
                qc.PeerStore.AddOrUpdate(peerInfo);
                
                if (qc.SharePeers)
                {
                    foreach (var peer in qc.AvailablePeers)
                    {
                        if (peer.Value.ActiveEndPoint.Address.Equals(result.RemoteEndPoint.Address))
                            continue;

                        udp.SendAsync(qc.GenerateAck(qc.SharePeers), result.RemoteEndPoint);
                    }
                }
            }
            else
            {
                var peer = qc.AvailablePeers[peerId];

                if (qc.PasswordHash != null && !passwordConnection)
                {
                    Console.WriteLine("Existing peer sent unauthenticated Hello, requesting re-authentication from " + result.RemoteEndPoint);
                    var challengePayload = qc.GenerateHelloPayload(MessageType.Interrogation, true);
                    _ = udp.SendAsync(challengePayload, result.RemoteEndPoint);
                    return;
                }
                
                if (!certHash.SequenceEqual(peer.CertHash))
                {
                    Console.WriteLine("HELLO OLD: Received corrupted cert hash from " + result.RemoteEndPoint);
                    return;
                }
                
                peer.ActiveEndPoint = result.RemoteEndPoint;
             
                if (peer.Name.Length != nameBytes.Length || peer.Name != Encoding.UTF8.GetString(nameBytes))
                {
                    peer.Name = Encoding.UTF8.GetString(nameBytes);
                }

                peer.LastSeen = PreciseTime.GetCorrectTime();
            }

            udp.SendAsync(qc.GenerateAck(qc.SharePeers), result.RemoteEndPoint);
        }
    }
}
