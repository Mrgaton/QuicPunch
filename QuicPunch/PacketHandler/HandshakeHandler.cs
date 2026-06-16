using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using static QuicPunch.QuicPunch;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch.PacketHandler
{
    internal class HandshakeHandler
    {
        internal static void HandleHandshake(QuicPunch qc, BinaryReader r, UdpClient udp, UdpReceiveResult result)
        {
            var handShakeType = (HandShakeType)r.ReadByte();
            var remotePort = r.ReadUInt16();

            var connectionType = new Guid(r.ReadBytes(16));
            var guid = new Guid(r.ReadBytes(16));

            var signatureHandshake = r.ReadBytes(64); //Signature data

            if (!qc.AvilablePeers.TryGetValue(result.RemoteEndPoint, out PeerInfo handshakePeer))
            {
                Console.WriteLine($"Received handshake from unknown peer {result.RemoteEndPoint}");
                return;
            }

            if (!handshakePeer.Curve.VerifyData(result.Buffer.AsSpan(0, (int)r.BaseStream.Position - signatureHandshake.Length), signatureHandshake, HashAlgorithmName.SHA3_256))
            {
                Console.WriteLine("Received invalid signature from " + result.RemoteEndPoint);
                return;
            }

            switch (handShakeType)
            {
                case HandShakeType.Request:
                    Console.WriteLine($"Received handshake request from {result.RemoteEndPoint}");

                    _ = Task.Run(async () =>
                    {
                        HandShakeType decidedResponse = HandShakeType.Unsuported;
                        ushort decidedPort = 0;
                        CancellationToken ct = CancellationToken.None;

                        if (qc.ProtocolHandlers.TryGetValue(connectionType, out var handler))
                        {
                            HandshakeDecision decision;

                            if (qc.AutoAcceptConnections)
                            {
                                decision = new HandshakeDecision(true, (ushort)Random.Shared.Next(ushort.MaxValue / 2, ushort.MaxValue), CancellationToken.None);
                            }
                            else
                            {
                                decision = await qc.Manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, result.RemoteEndPoint), TimeSpan.FromSeconds(30), true, CancellationToken.None);
                            }

                            if (decision.Accepted)
                            {
                                if (decision.Port == null || decision.Port == 0)
                                    throw new Exception("Invalid port in handshake decision.");

                                decidedResponse = HandShakeType.Accept;
                                decidedPort = (ushort)decision.Port;
                                ct = decision.Ct ?? CancellationToken.None;
                            }
                            else
                            {
                                decidedResponse = HandShakeType.Decline;
                                decidedPort = 0;
                            }
                        }

                        UdpClient? nudp = null;

                        if (decidedResponse == HandShakeType.Accept)
                        {
                            nudp = new UdpClient();
                            if (OperatingSystem.IsWindows())
                                nudp.Client.IOControl(-1744830452, [0], null);

                            nudp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            nudp.Client.Bind(new IPEndPoint(IPAddress.Any, decidedPort));
                        }

                        byte[] payload;

                        using (MemoryStream ms = new MemoryStream())
                        using (BinaryWriter w = new BinaryWriter(ms))
                        {
                            w.Write(MagicHeader);
                            w.Write((byte)MessageType.Handshake);
                            w.Write((byte)(decidedResponse));
                            w.Write(decidedPort);
                            w.Write(connectionType.ToByteArray());
                            w.Write(guid.ToByteArray());

                            payload = ms.ToArray();
                            var signature = qc.CertManager.Curve.SignData(payload, HashAlgorithmName.SHA3_256);
                            Array.Resize(ref payload, payload.Length + signature.Length);
                            Buffer.BlockCopy(signature, 0, payload, payload.Length - signature.Length, signature.Length);
                        }

                        await udp.SendAsync(payload, result.RemoteEndPoint);

                        Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                Thread.Sleep(500);
                                udp.Send(payload, result.RemoteEndPoint);
                            }
                        });

                        if (decidedResponse == HandShakeType.Accept)
                        {
                            var connection = await QuicConection.InitQuicConnectionCore(qc.IPEndpoint, nudp, qc.AvilablePeers[result.RemoteEndPoint], remotePort, qc.CertManager.PeerCertificate!, handler.CompressionOptions, ct);

                            if (connection.Conection == null || connection.Stream == null)
                            {
                                Task.Run(async () => await handler.DeniedAsync(qc.AvilablePeers[result.RemoteEndPoint], ct));
                            }
                            else
                            {
                                Task.Run(async () => await handler.HandleAsync(connection.Conection, connection.Stream, qc.AvilablePeers[result.RemoteEndPoint], ct));
                            }
                        }
                    });
                    return;

                case HandShakeType.Accept:
                    Console.WriteLine($"Received handshake ACCEPT from {result.RemoteEndPoint}");
                    qc.Manager.Approve(guid, remotePort, null);
                    return;

                case HandShakeType.Decline or HandShakeType.Unsuported:
                    Console.WriteLine($"Handshake canceled from {result.RemoteEndPoint}");
                    qc.Manager.Reject(guid);
                    return;
            }
        }
    }
}
