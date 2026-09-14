using QuicPunch.Helpers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

                var sharedPeers = sharePeers
                    ? peersCopy.Select(p => p.Value)
                        .Where(qp.IsTrustedPeer)
                        .Where(p => transport == TransportType.Tor
                            ? (p.ConnectionFlags.IsTor || p.ActiveTransport == TransportType.Tor || !string.IsNullOrEmpty(p.OnionAddress))
                            : (!p.ConnectionFlags.IsTor && p.ActiveTransport != TransportType.Tor && p.Addresses != null && p.Addresses.Length > 0))
                        .Take(32)
                        .ToArray()
                    : Array.Empty<PeerInfo>();

                w.Write((ushort)sharedPeers.Length);

                if (sharePeers)
                {
                    foreach (var peer in sharedPeers)
                    {
                        w.Write(peer.ConnectionFlags.RawValue);

                        if (transport == TransportType.Tor)
                        {
                            // Over Tor transport, do not leak or advertise WAN addresses
                            w.Write((byte)0);
                        }
                        else
                        {
                            var sharedAddresses = (peer.Addresses ?? Array.Empty<IPAddress>())
                                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && Utilities.IsValidPeerAddress(a))
                                .Distinct()
                                .Take(32)
                                .ToArray();
                            w.Write((byte)sharedAddresses.Length);
                            foreach (var e in sharedAddresses)
                            {
                                w.Write(e.GetAddressBytes());
                            }
                        }

                        ushort pMin = (ushort)(peer.PortArray.Length > 0 ? peer.PortArray.Min() : (peer.ActiveEndPoint?.Port ?? 0));
                        ushort pMax = (ushort)(peer.PortArray.Length > 0 ? peer.PortArray.Max() : (peer.ActiveEndPoint?.Port ?? 0));
                        w.Write(pMin);
                        w.Write(pMax);
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

            using var genericEntropyPeer = targetPeer == null ? new PeerInfo() : null;
            if (targetPeer != null)
                targetPeer.EnsureLocalEntropy();
            else
                certMgr.CopySessionEntropyTo(genericEntropyPeer!);

            PeerInfo entropyPeer = targetPeer ?? genericEntropyPeer!;
            byte[] sessionNonce = entropyPeer.LocalSessionNonce!;
            byte[] ephemeralEcdhRaw = entropyPeer.LocalEphemeralEcdhPublicKeyRaw;
            ECDiffieHellman localEphemeralEcdh = entropyPeer.LocalEphemeralEcdh!;

            byte[] effectiveChallengeNonce;
            if (challengeNonce != null && challengeNonce.Length == 24)
            {
                effectiveChallengeNonce = challengeNonce;
            }
            else if (type == MessageType.Interrogation)
            {
                effectiveChallengeNonce = qp.CreatePendingChallenge(
                    targetPeer?.ActiveEndPoint,
                    sessionNonce,
                    localEphemeralEcdh,
                    targetPeer != null && targetPeer.TryGetCertificateHash(out var expectedCertHash) ? expectedCertHash : null);
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

                w.Write(currentPeer.ConnectionFlags.RawValue);

                var addresses = (currentPeer.Addresses ?? Array.Empty<IPAddress>())
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && Utilities.IsValidPeerAddress(a))
                    .Distinct()
                    .Take(32)
                    .ToArray();
                w.Write((byte)addresses.Length);
                foreach (var address in addresses)
                {
                    w.Write(address.GetAddressBytes());
                }

                ushort controlPort = (ushort)(currentPeer.PortArray.Length > 0 ? currentPeer.PortArray[0] : qp.LocalBoundPort);
                ushort minPort = (ushort)(currentPeer.PortArray.Length > 0 ? currentPeer.PortArray.Min() : controlPort);
                ushort maxPort = (ushort)(currentPeer.PortArray.Length > 0 ? currentPeer.PortArray.Max() : controlPort);

                w.Write(minPort);
                w.Write(maxPort);

                var nameBytes = Encoding.UTF8.GetBytes(currentPeer.Name ?? "");
                if (nameBytes.Length > byte.MaxValue)
                    nameBytes = nameBytes[..byte.MaxValue];
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

                w.Write(sessionNonce);
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

                var cList = (candidates ?? Array.Empty<CandidateEndpoint>())
                    .Where(c => c.EndPoint.Address.AddressFamily == AddressFamily.InterNetwork && c.EndPoint.Port > 0)
                    .Take(32)
                    .ToArray();
                w.Write((byte)cList.Length);
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
