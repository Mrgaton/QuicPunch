using QuicPunch.Helpers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TransportType = QuicPunch.QuicPunch.TransportType;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch.PacketHandler
{
    internal static class PacketBuilder
    {
        public static byte[] BuildDisconnectPacket(QuicPunch qp, TransportType transport = TransportType.Wan)
        {
            byte[] payload;
            var peer = qp.GetCurrentPeer(transport);
            var certMgr = qp.GetCertManager(transport);
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunch.MagicHeader);
                w.Write((byte)MessageType.Disconnect);
                w.Write(peer.IdRaw);
                w.Write(PreciseTime.GetCorrectTime().Ticks);

                payload = ms.ToArray();
                var signature = certMgr.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }
            return payload;
        }

        public static byte[] GenerateAck(QuicPunch qp, bool sharePeers, TransportType transport = TransportType.Wan)
        {
            byte[] payload;
            var currentPeer = qp.GetCurrentPeer(transport);
            var certMgr = qp.GetCertManager(transport);

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunch.MagicHeader);
                w.Write((byte)MessageType.Ack);
                w.Write(currentPeer.IdRaw);

                var peersCopy = qp.AvailablePeers.ToArray();

                w.Write(sharePeers ? (ushort)peersCopy.Length : (ushort)0);

                if (sharePeers)
                {
                    foreach (var peer in peersCopy.Select(p => p.Value))
                    {
                        PackedFlags pf = new PackedFlags()
                        {
                            NetworkType = peer.NetworkType
                        };

                        w.Write((byte)pf.RawValue);

                        w.Write((byte)peer.Addresses.Length);
                        foreach (var e in peer.Addresses)
                        {
                            w.Write(e.GetAddressBytes());
                        }

                        w.Write((ushort)peer.MinPort);
                        w.Write((ushort)peer.MaxPort);
                        w.Write(peer.CertHash);
                    }
                }

                w.Write(PreciseTime.GetCorrectTime().Ticks);

                payload = ms.ToArray();

                var signature = certMgr.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }

        public static byte[] GenerateHelloPayload(QuicPunch qp, MessageType type, bool passwordProof, byte[]? challengeNonce = null, TransportType transport = TransportType.Wan, PeerInfo? targetPeer = null)
        {
            byte[] payload;
            var currentPeer = qp.GetCurrentPeer(transport);
            var certMgr = qp.GetCertManager(transport);

            byte[] effectiveChallengeNonce;
            if (challengeNonce != null && challengeNonce.Length == 24)
            {
                effectiveChallengeNonce = challengeNonce;
            }
            else if (type == MessageType.Interrogation)
            {
                effectiveChallengeNonce = qp.CreatePendingChallenge();
            }
            else
            {
                effectiveChallengeNonce = new byte[24];
            }

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunch.MagicHeader);
                w.Write((byte)type);
                w.Write(currentPeer.CertHash);

                PackedFlags pf = new PackedFlags()
                {
                    NetworkType = currentPeer.NetworkType
                };
                w.Write((byte)pf.RawValue);

                var addresses = currentPeer.Addresses ?? Array.Empty<IPAddress>();
                w.Write((byte)addresses.Length);
                foreach (var address in addresses)
                {
                    w.Write(address.GetAddressBytes());
                }

                ushort controlPort = (ushort)(currentPeer.MinPort > 0 ? currentPeer.MinPort : qp.LocalBoundPort);
                ushort minPort = controlPort;
                ushort maxPort = (ushort)(currentPeer.MaxPort > 0 ? currentPeer.MaxPort : controlPort);

                w.Write(minPort);
                w.Write(maxPort);

                var nameBytes = Encoding.UTF8.GetBytes(currentPeer.Name ?? "");
                w.Write((byte)nameBytes.Length);
                w.Write(nameBytes);

                var cert = certMgr.PeerCertificate.Export(X509ContentType.Cert);
                var certBytes = cert.Length;
                w.Write((ushort)certBytes);
                w.Write(cert);

                var ticks = PreciseTime.GetCorrectTime().Ticks;
                w.Write(ticks);

                w.Write(effectiveChallengeNonce);

                w.Write((byte)(qp.PasswordHash != null && passwordProof ? 255 : 0));

                if (qp.PasswordHash != null && passwordProof)
                {
                    var nonce = RandomNumberGenerator.GetBytes(24);
                    w.Write(nonce);

                    var pop = HMACSHA3_256.HashData(Utilities.Combine(effectiveChallengeNonce, nonce, currentPeer.CertHash), qp.PasswordHash);

                    w.Write(pop);
                }

                byte[] sessionNonce = targetPeer?.LocalSessionNonce ?? certMgr.SessionNonce;
                w.Write(sessionNonce);
                var ephemeralEcdhRaw = targetPeer?.LocalEphemeralEcdhPublicKeyRaw ?? certMgr.EphemeralEcdhPublicKeyRaw;
                w.Write((ushort)ephemeralEcdhRaw.Length);
                if (ephemeralEcdhRaw.Length > 0)
                {
                    w.Write(ephemeralEcdhRaw);
                }

                payload = ms.ToArray();

                var signature = certMgr.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }

        public static byte[] BuildPingPacket(QuicPunch qp, long timestamp, bool isResponse = false, TransportType transport = TransportType.Wan)
        {
            var currentPeer = qp.GetCurrentPeer(transport);
            int size = QuicPunch.MagicHeader.Length + 1 + 1 + 16 + 8;
            byte[] packet = new byte[size];

            Buffer.BlockCopy(QuicPunch.MagicHeader, 0, packet, 0, QuicPunch.MagicHeader.Length);
            packet[QuicPunch.MagicHeader.Length] = (byte)MessageType.Ping;
            packet[QuicPunch.MagicHeader.Length + 1] = (byte)(isResponse ? 1 : 0);
            Buffer.BlockCopy(currentPeer.IdRaw, 0, packet, QuicPunch.MagicHeader.Length + 2, 16);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(QuicPunch.MagicHeader.Length + 2 + 16, 8), timestamp);

            return packet;
        }

        public static byte[] GenerateHandshakePayload(QuicPunch qp, HandShakeType type, ushort port, Guid protocolId, Guid connectionGuid, IReadOnlyList<CandidateEndpoint>? candidates = null, TransportType transport = TransportType.Wan)
        {
            var currentPeer = qp.GetCurrentPeer(transport);
            var certMgr = qp.GetCertManager(transport);

            byte[] payload;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunch.MagicHeader);
                w.Write((byte)MessageType.Handshake);
                w.Write(currentPeer.IdRaw);
                w.Write((byte)type);
                w.Write(port);
                w.Write(protocolId.ToByteArray());
                w.Write(connectionGuid.ToByteArray());

                var cList = candidates ?? Array.Empty<CandidateEndpoint>();
                w.Write((byte)cList.Count);
                foreach (var c in cList)
                {
                    w.Write((byte)c.Type);
                    w.Write(c.EndPoint.Address.GetAddressBytes());
                    w.Write((ushort)c.EndPoint.Port);
                    w.Write((uint)c.Priority);
                }

                payload = ms.ToArray();
                var signature = certMgr.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }

        public static byte[] BuildQuicReadyPacket(QuicPunch qp, Guid connectionGuid, ushort listeningPort, TransportType transport = TransportType.Wan)
        {
            var currentPeer = qp.GetCurrentPeer(transport);
            var certMgr = qp.GetCertManager(transport);

            byte[] payload;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(QuicPunch.MagicHeader);
                w.Write((byte)MessageType.QuicReady);
                w.Write(currentPeer.IdRaw);
                w.Write(connectionGuid.ToByteArray());
                w.Write(listeningPort);
                w.Write(PreciseTime.GetCorrectTime().Ticks);

                payload = ms.ToArray();
                var signature = certMgr.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                Array.Resize(ref payload, payload.Length + signature.Length);
                Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
            }

            return payload;
        }
    }
}
