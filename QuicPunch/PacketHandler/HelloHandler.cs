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
            if (messageType == (byte)MessageType.Interogation)
            {
                udp.SendAsync(qc.GenerateHelloPayload(MessageType.Hello, true), result.RemoteEndPoint);
            }

            var certHash = r.ReadBytes(qc.CurrentPeer.CertHash.Length);

            var foundExpectedCert = qc.ExpectedPeerCerts.Contains(certHash);

            if (!foundExpectedCert && !qc.PeerStore.SavedPeers.Any(sp => sp.CertHash.SequenceEqual(certHash)))
            {
                Console.WriteLine("HELLO INIT: Peer presented unexpected certificate");
                return;
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

            if (!qc.AvailablePeers.ContainsKey(certHash))
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
                    NetworkType =  pf.NetworkType,
                    
                    Addresses =  addresses,
                    MaxPort = maxPort,
                    MinPort =  minPort,
                    
                    CertHash = certHash,
                    Name = Encoding.UTF8.GetString(nameBytes),
                    LastSeen = PreciseTime.GetCorrectTime(),
                    Curve = ecdsa
                };

                if (!peerInfo.Curve.VerifyData(result.Buffer.AsSpan(0, payloadLength), signature, HashAlgorithmName.SHA3_256))
                {
                    Console.WriteLine("HELLO NEW: Received invalid signature from " + result.RemoteEndPoint);
                    return;
                }

                qc.AvailablePeers[certHash] = peerInfo;
                qc.RaisePeerAvailable(peerInfo);
                qc.PeerStore.AddOrUpdate(peerInfo.Addresses,peerInfo.MinPort, peerInfo.MaxPort, peerInfo.CertHash);
                
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
                var peer = qc.AvailablePeers[certHash];
                
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
                    peer.ActiveEndPoint = result.RemoteEndPoint;
                 
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
