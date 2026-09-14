using QuicPunch.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
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

                if (qc.AvailablePeers.TryGetValue(peerId, out PeerInfo? ackPeer) && ackPeer != null)
                {
                    if (!qc.IsTrustedPeer(ackPeer))
                    {
                        QuicPunchLog.Info($"ACK: Ignored shared-peer list from untrusted peer {peerId}.");
                        return;
                    }

                    var peersCount = r.ReadUInt16();
                    if (peersCount > MaxSharedPeersPerAck)
                        throw new InvalidDataException($"ACK shared-peer count exceeds maximum allowed ({peersCount} > {MaxSharedPeersPerAck}).");

                    List<PeerInfo> remotePeers = new List<PeerInfo>(peersCount);

                    for (int i = 0; i < peersCount; i++)
                    {
                        PeerInfo pi = new PeerInfo();

                        ConnectionFlags flags = new ConnectionFlags(r.ReadByte());
                        pi.ConnectionFlags = flags;
                        pi.NetworkType = flags.IsTor ? QuicPunch.NetworkType.Tor : QuicPunch.NetworkType.Static;

                        byte addressCount = r.ReadByte();
                        if (addressCount > 32)
                            throw new InvalidDataException($"Shared peer advertises too many addresses: {addressCount}.");

                        var addresses = new List<IPAddress>(addressCount);
                        for (int e = 0 ; e < addressCount; e++)
                        {
                            byte[] rawAddress = r.ReadBytes(4);
                            if (rawAddress.Length != 4)
                                throw new InvalidDataException("Truncated shared-peer address.");
                            var address = new IPAddress(rawAddress);
                            if (Utilities.IsValidPeerAddress(address) && !addresses.Contains(address))
                                addresses.Add(address);
                        }

                        pi.Addresses = addresses.ToArray();

                        ushort sharedMinPort = r.ReadUInt16();
                        ushort sharedMaxPort = r.ReadUInt16();
                        if (sharedMinPort > 0 && sharedMaxPort >= sharedMinPort)
                        {
                            pi.PortArray = sharedMinPort == sharedMaxPort
                                ? [sharedMinPort]
                                : Enumerable.Range(sharedMinPort, sharedMaxPort - sharedMinPort + 1).Select(p => (ushort)p).ToArray();
                            pi.ConnectionFlags.PortMode = sharedMinPort == sharedMaxPort ? PortMode.Single : PortMode.Range;
                        }
                        else if (sharedMinPort > 0)
                        {
                            pi.PortArray = [sharedMinPort];
                            pi.ConnectionFlags.PortMode = PortMode.Single;
                        }

                        byte[] sharedCertHash = r.ReadBytes(32);
                        if (sharedCertHash.Length != 32)
                            throw new InvalidDataException("Truncated shared-peer certificate hash.");
                        pi.SetCertificateHash(sharedCertHash);
                        remotePeers.Add(pi);
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
                        if (transport == TransportType.Tor)
                        {
                            // If received over Tor, reject any shared peer claiming WAN addresses
                            if (peer.Addresses != null && peer.Addresses.Length > 0)
                                continue;
                            peer.NetworkType = QuicPunch.NetworkType.Tor;
                            peer.ActiveTransport = TransportType.Tor;
                        }
                        else
                        {
                            // If received over WAN, reject Tor peers or peers without valid endpoints
                            if (peer.NetworkType == QuicPunch.NetworkType.Tor || peer.Addresses == null || peer.Addresses.Length == 0)
                                continue;
                            peer.ActiveTransport = TransportType.Wan;
                        }

                        if (peer.TryGetCertificateHash(out var peerHash) &&
                            !qc.AvailablePeers.Values.Any(availablePeer => availablePeer.TryGetCertificateHash(out var availableHash) && CryptographicOperations.FixedTimeEquals(availableHash, peerHash)))
                        {
                            // A trusted introducer may suggest a peer, but the suggested
                            // peer remains untrusted until the user explicitly trusts it.
                            _ = qc.PeerInterrogationDiscovered(peer);
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
