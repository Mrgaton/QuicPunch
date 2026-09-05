using QuicPunch.Helpers;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TransportType = QuicPunch.QuicPunch.TransportType;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch.PacketHandler
{
    internal class HelloHandler
    {
        private const int MaxHelloAddresses = 32;
        private const int MaxCertificateBytes = 16 * 1024;
        private const int MaxEphemeralKeyBytes = 1024;

        private static byte[] ReadRequiredBytes(BinaryReader reader, int count)
        {
            byte[] value = reader.ReadBytes(count);
            if (value.Length != count)
                throw new InvalidDataException($"Truncated Hello payload: expected {count} bytes, received {value.Length}.");
            return value;
        }

        internal static void HandleHello(QuicPunch qc, BinaryReader r, UdpClient? udp, EndPoint remoteEndPoint, byte[] buffer, byte messageType, TransportType transport = TransportType.Wan, TorQuicConnectionManager? torChannel = null, bool isLanDiscovery = false) {
            try
            {
                if (qc.LifecycleState == QuicPunch.QuicPunchLifecycleState.Disposed || qc.LifecycleState == QuicPunch.QuicPunchLifecycleState.Stopped)
                    return;

                QuicPunchLog.Info($"[HELLO HANDLER] Node '{qc.GetCurrentPeer(transport).Name}' received messageType {(MessageType)messageType} from {remoteEndPoint} over {transport}");
                var certHash = ReadRequiredBytes(r, qc.GetCurrentPeer(transport).CertHash.Length);

                if ((qc.CurrentPeer.CertHash != null && CryptographicOperations.FixedTimeEquals(certHash, qc.CurrentPeer.CertHash)) ||
                    (qc.TorCurrentPeer?.CertHash != null && CryptographicOperations.FixedTimeEquals(certHash, qc.TorCurrentPeer.CertHash)))
                {
                    return;
                }

                var peerId = new Guid(XxHash128.Hash(certHash));
                bool isKnownPeer = qc.AvailablePeers.TryGetValue(peerId, out var peer) && peer?.Certificate != null;

                PackedFlags pf = new PackedFlags(r.ReadByte());

                var addressesAmount = r.ReadByte();
                if (addressesAmount > MaxHelloAddresses)
                    throw new InvalidDataException($"Hello advertises too many addresses: {addressesAmount}.");

                var validAddresses = new List<IPAddress>(addressesAmount);
                for (int i = 0; i < addressesAmount; i++)
                {
                    var ip = new IPAddress(ReadRequiredBytes(r, 4));
                    if (Utilities.IsValidPeerAddress(ip) && !validAddresses.Contains(ip))
                    {
                        validAddresses.Add(ip);
                    }
                }
                IPAddress[] addresses = validAddresses.ToArray();

                ushort minPort = r.ReadUInt16();
                ushort maxPort = r.ReadUInt16();

                int senderControlPort = minPort > 0 ? minPort : (remoteEndPoint is IPEndPoint ipPort ? ipPort.Port : 0);
                if (minPort == 0) minPort = (ushort)senderControlPort;
                if (maxPort == 0) maxPort = (ushort)senderControlPort;

                IPEndPoint targetControlEndPoint;
                if (remoteEndPoint is IPEndPoint remoteIpEp)
                {
                    if (senderControlPort > 0 && (isLanDiscovery || remoteIpEp.Port == qc.LanDiscoveryPort || IPAddress.Broadcast.Equals(remoteIpEp.Address)))
                    {
                        targetControlEndPoint = new IPEndPoint(remoteIpEp.Address, senderControlPort);
                    }
                    else
                    {
                        targetControlEndPoint = remoteIpEp;
                    }
                }
                else
                {
                    targetControlEndPoint = new IPEndPoint(IPAddress.Loopback, senderControlPort);
                }

                byte nameSize = r.ReadByte();
                var nameBytes = ReadRequiredBytes(r, nameSize);

                var certSize = r.ReadUInt16();
                if (certSize == 0 || certSize > MaxCertificateBytes)
                    throw new InvalidDataException($"Invalid certificate size in Hello: {certSize}.");
                var certBytes = ReadRequiredBytes(r, certSize);

                var remoteTicks = r.ReadInt64();
                long nowTicks = PreciseTime.GetCorrectTime().Ticks;
                long diffTicks = nowTicks - remoteTicks;

                if (Math.Abs(diffTicks) > 30_000_000)
                {
                    QuicPunchLog.Info($"HELLO: Packet from {remoteEndPoint} rejected. Timestamp drifted by {diffTicks / 10_000.0}ms.");
                    return;
                }

                var challengeNonce = ReadRequiredBytes(r, 24);
                var passwordConnection = r.ReadByte() > 0;

                byte[]? passwordNonce = null;
                byte[]? remotePop = null;
                if (passwordConnection && qc.PasswordHash == null)
                {
                    QuicPunchLog.Info("Peer has password connection but current instance doesn't");
                    return;
                }
                else if (passwordConnection)
                {
                    passwordNonce = ReadRequiredBytes(r, 24);
                    remotePop = ReadRequiredBytes(r, 256 / 8);
                }
                else if (qc.PasswordHash != null)
                {
                    QuicPunchLog.Info("Instance requires password authentication, but peer didn't send proof. Requesting re-authentication from " + remoteEndPoint);
                    var challengePayload = qc.GenerateHelloPayload(MessageType.Interrogation, true, transport: transport, targetPeer: peer);
                    _ = qc.SendResponseAsync(challengePayload, targetControlEndPoint, transport, torChannel);
                    return;
                }

                byte[] remoteSessionNonce = ReadRequiredBytes(r, 32);
                ushort ephemeralKeyLen = r.ReadUInt16();
                if (ephemeralKeyLen == 0 || ephemeralKeyLen > MaxEphemeralKeyBytes)
                    throw new InvalidDataException($"Invalid ephemeral ECDH key size in Hello: {ephemeralKeyLen}.");
                byte[] remoteEphemeralKey = ReadRequiredBytes(r, ephemeralKeyLen);

                int payloadLength = (int)r.BaseStream.Position;
                byte[] signature = new byte[CertManager.SignatureLength];
                r.ReadExactly(signature);

                X509Certificate2? cert = null;
                bool certTransferred = false;
                try
                {
                    byte[] ecdhKeyRaw;
                    if (isKnownPeer)
                    {
                        ecdhKeyRaw = peer!.EcdhPublicKey ?? Array.Empty<byte>();
                    }
                    else
                    {
                        cert = X509CertificateLoader.LoadCertificate(certBytes);

                        var ecdhExt = cert.Extensions[CertManager.EcdhExtensionOid];
                        if (ecdhExt == null)
                        {
                            QuicPunchLog.Info("Certificate missing ECDH extension from " + remoteEndPoint);
                            return;
                        }
                        ecdhKeyRaw = ecdhExt.RawData;

                        var calculatedHash = SHA3_256.HashData(cert.GetPublicKey());
                        if (!CryptographicOperations.FixedTimeEquals(calculatedHash, certHash))
                        {
                            QuicPunchLog.Info("Corrupted cert hash from " + remoteEndPoint);
                            return;
                        }
                    }

                    using (var ecdsa = (isKnownPeer ? peer!.Certificate : cert!).GetECDsaPublicKey())
                    {
                        if (ecdsa == null || !ecdsa.VerifyData(buffer.AsSpan(0, payloadLength), signature, HashAlgorithmName.SHA3_256))
                        {
                            QuicPunchLog.Info("HELLO: Received invalid signature from " + remoteEndPoint);
                            return;
                        }
                    }

                    if (passwordConnection && qc.PasswordHash != null && passwordNonce != null && remotePop != null)
                    {
                        byte[] pop = HMACSHA3_256.HashData(Utilities.Combine(challengeNonce, passwordNonce, certHash), qc.PasswordHash!);
                        if (!CryptographicOperations.FixedTimeEquals(pop, remotePop))
                        {
                            QuicPunchLog.Info("Error: the peer could not prove password ownership.");
                            return;
                        }
                    }

                    using var pendingChallenge = qc.ConsumePendingChallenge(challengeNonce);
                    bool isChallengeFresh = pendingChallenge != null;
                    if (pendingChallenge?.ExpectedCertHash is { Length: > 0 } expectedCertHash &&
                        !CryptographicOperations.FixedTimeEquals(expectedCertHash, certHash))
                    {
                        QuicPunchLog.Info($"HELLO: certificate identity did not match the token used for interrogation from {remoteEndPoint}.");
                        return;
                    }

                    if (!isKnownPeer)
                    {
                        var peerInfo = new PeerInfo(cert!, ecdhKeyRaw)
                        {
                            ActiveEndPoint = targetControlEndPoint,
                            ActiveTransport = transport,
                            TorChannel = torChannel,
                            OnionAddress = torChannel?.RemoteOnion,
                            Addresses = addresses,
                            MinPort = minPort,
                            MaxPort = maxPort,
                            Name = Encoding.UTF8.GetString(nameBytes),
                            NetworkType = pf.NetworkType,
                            LastSeenHelloTicks = remoteTicks,
                            SessionNonce = remoteSessionNonce,
                            EphemeralEcdhPublicKey = remoteEphemeralKey
                        };

                        try
                        {
                            // If this Hello answers one of our interrogations, reuse the
                            // exact local entropy that was sent in that challenge.
                            pendingChallenge?.ApplyLocalEntropy(peerInfo);
                            peerInfo.InitSession(qc, transport);
                            peerInfo.MarkSeen();

                            if (qc.TryAddAvailablePeer(peerId, peerInfo))
                            {
                                certTransferred = true;
                                QuicPunchLog.Info($"[HELLO HANDLER] Node '{qc.GetCurrentPeer(transport).Name}' added peer {peerId} ({peerInfo.Name}) to AvailablePeers!");
                                qc.RaisePeerAvailable(peerInfo);
                                qc.CancelMatchingInterrogations(peerInfo);
                                qc.UpdateSavedPeerIfPresent(peerInfo);

                                var responseHello = qc.GenerateHelloPayload(MessageType.Hello, true, challengeNonce, transport, targetPeer: peerInfo);
                                _ = qc.SendResponseAsync(responseHello, targetControlEndPoint, transport, torChannel);
                                _ = qc.SendResponseAsync(qc.GenerateAck(qc.SharePeers, transport), targetControlEndPoint, transport, torChannel);
                            }
                            else
                            {
                                peerInfo.Dispose();
                                certTransferred = true;
                            }
                        }
                        catch
                        {
                            peerInfo.Dispose();
                            certTransferred = true;
                            throw;
                        }

                        return;
                    }
                    else
                    {
                        if (!isChallengeFresh && remoteTicks <= peer!.LastSeenHelloTicks)
                        {
                            QuicPunchLog.Info($"HELLO: Replay rejected from {remoteEndPoint}: timestamp {remoteTicks} <= last seen {peer.LastSeenHelloTicks}.");
                            return;
                        }

                        peer!.LastSeenHelloTicks = remoteTicks;

                        // A known peer may be answering a mobility/rebind challenge.
                        // Reuse the exact local entropy that was advertised with that
                        // challenge before deriving the replacement session keys.
                        pendingChallenge?.ApplyLocalEntropy(peer);

                        peer.ActiveEndPoint = targetControlEndPoint;
                        peer.ActiveTransport = transport;
                        if (addresses != null && addresses.Length > 0)
                        {
                            peer.Addresses = addresses;
                        }
                        if (minPort > 0) peer.MinPort = minPort;
                        if (maxPort > 0) peer.MaxPort = maxPort;
                        if (pf.NetworkType != QuicPunch.NetworkType.Unknown) peer.NetworkType = pf.NetworkType;

                        if (torChannel != null && (peer.TorChannel == null || peer.TorChannel.IsClosed || !ReferenceEquals(peer.TorChannel, torChannel)))
                        {
                            if (peer.TorChannel != null && !ReferenceEquals(peer.TorChannel, torChannel))
                            {
                                try { peer.TorChannel.Dispose(); } catch { }
                            }
                            peer.TorChannel = torChannel;
                            peer.OnionAddress = torChannel.RemoteOnion;
                        }

                        if (peer.Name == null || peer.Name.Length != nameBytes.Length || peer.Name != Encoding.UTF8.GetString(nameBytes))
                        {
                            peer.Name = Encoding.UTF8.GetString(nameBytes);
                        }

                        bool remoteChanged = peer.SessionNonce == null || !CryptographicOperations.FixedTimeEquals(peer.SessionNonce, remoteSessionNonce);
                        bool localChanged = peer.ActiveLocalSessionNonce == null || !CryptographicOperations.FixedTimeEquals(peer.ActiveLocalSessionNonce, peer.LocalSessionNonce);
                        bool sessionChanged = remoteChanged || localChanged;

                        peer.SessionNonce = remoteSessionNonce;
                        peer.EphemeralEcdhPublicKey = remoteEphemeralKey;

                        if ((!peer.IsSessionReady || sessionChanged) && peer.EcdhPublicKey != null)
                        {
                            peer.InitSession(qc, transport);
                        }

                        if (messageType == (byte)MessageType.Interrogation || sessionChanged)
                        {
                            var responseHello = qc.GenerateHelloPayload(MessageType.Hello, true, challengeNonce, transport, targetPeer: peer);
                            _ = qc.SendResponseAsync(responseHello, targetControlEndPoint, transport, torChannel);
                        }

                        peer.MarkSeen();
                        qc.UpdateSavedPeerIfPresent(peer);
                    }

                    _ = qc.SendResponseAsync(qc.GenerateAck(qc.SharePeers, transport), remoteEndPoint, transport, torChannel);
                }
                finally
                {
                    if (!certTransferred && cert != null)
                    {
                        cert.Dispose();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                QuicPunchLog.Info($"[HELLO HANDLER] Packet from {remoteEndPoint} ignored: local cryptographic context or peer already disposed.");
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error($"[HELLO HANDLER ERROR] From {remoteEndPoint}", ex);
            }
        }
    }
}
