using QuicPunch.Helpers;
using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using TransportType = QuicPunch.QuicPunch.TransportType;
using static QuicPunch.QuicPunch;
using static QuicPunch.QuicPunchStructures;

namespace QuicPunch.PacketHandler
{
    internal class HandshakeHandler
    {
        internal static void HandleHandshake(QuicPunch qc, BinaryReader r, UdpClient? udp, EndPoint remoteEndPoint, byte[] buffer, TransportType transport = TransportType.Wan, TorQuicConnectionManager? torChannel = null)
        {
            if (qc.LifecycleState != QuicPunchLifecycleState.Started || qc.CancellationSource == null || qc.CancellationSource.IsCancellationRequested)
                return;

            var peerId = new Guid(r.ReadBytes(16));
            if (peerId == Guid.Empty) return;

            var handShakeType = (HandShakeType)r.ReadByte();
            var remotePort = r.ReadUInt16();

            var connectionTypeBytes = r.ReadBytes(16);
            var guidBytes = r.ReadBytes(16);

            if (connectionTypeBytes.Length != 16 || guidBytes.Length != 16)
                return;

            var connectionType = new Guid(connectionTypeBytes);
            var guid = new Guid(guidBytes);

            var remoteCandidates = new List<CandidateEndpoint>();
            const int CandidateWireSize = 1 + 4 + 2 + 4;
            int remainingBytes = (int)(r.BaseStream.Length - r.BaseStream.Position);
            if (remainingBytes > CertManager.SignatureLength)
            {
                if (remainingBytes < 1 + CertManager.SignatureLength)
                    return;

                byte cCount = r.ReadByte();
                long requiredBytes = (long)cCount * CandidateWireSize + CertManager.SignatureLength;
                if (r.BaseStream.Length - r.BaseStream.Position != requiredBytes)
                    return;

                for (int i = 0; i < cCount; i++)
                {
                    var cType = (CandidateType)r.ReadByte();
                    byte[] ipBytes = r.ReadBytes(4);
                    if (ipBytes.Length != 4) return;
                    var ip = new IPAddress(ipBytes);
                    var port = r.ReadUInt16();
                    var prio = r.ReadUInt32();
                    if (port > 0 && Utilities.IsValidPeerAddress(ip))
                    {
                        remoteCandidates.Add(new CandidateEndpoint(new IPEndPoint(ip, port), cType, prio));
                    }
                }
            }

            var signatureHandshake = r.ReadBytes(CertManager.SignatureLength);
            if (signatureHandshake.Length != CertManager.SignatureLength)
                return;

            if (!qc.AvailablePeers.TryGetValue(peerId, out PeerInfo? handshakePeer) || handshakePeer == null)
            {
                QuicPunchLog.Info($"Received handshake from unknown peer {remoteEndPoint}");
                return;
            }

            if (!handshakePeer.Curve.VerifyData(buffer.AsSpan(0, (int)r.BaseStream.Position - signatureHandshake.Length), signatureHandshake, HashAlgorithmName.SHA3_256))
            {
                QuicPunchLog.Info("Received invalid signature from " + remoteEndPoint);
                return;
            }

            if (remoteEndPoint is IPEndPoint ipEp)
            {
                handshakePeer.ActiveEndPoint = ipEp;
            }
            handshakePeer.ActiveTransport = transport;
            if (torChannel != null) handshakePeer.TorChannel = torChannel;

            switch (handShakeType)
            {
                case HandShakeType.Request:
                    var session = qc.GetOrAddIncomingHandshakeSession(guid, peerId, connectionType);
                    if (session == null) return;

                    if (session.ConnectionTaskStarted)
                    {
                        ResendCachedResponse(qc, session, remoteEndPoint, transport, torChannel);
                        return;
                    }

                    lock (session)
                    {
                        if (session.ConnectionTaskStarted)
                        {
                            ResendCachedResponse(qc, session, remoteEndPoint, transport, torChannel);
                            return;
                        }
                        session.ConnectionTaskStarted = true;
                    }

                    QuicPunchLog.Info($"[HANDSHAKE SESSION] Starting new incoming handshake session for Guid: {guid} from {remoteEndPoint} (Candidates: {remoteCandidates.Count})");

                    bool wasYielded = false;
                    if (qc.TryGetActiveOutboundNegotiation(peerId, connectionType, out var outboundNegotiation) && outboundNegotiation != null)
                    {
                        var currentPeer = qc.GetCurrentPeer(transport);
                        bool localWins = currentPeer != null && currentPeer.Id.CompareTo(peerId) > 0;

                        if (localWins)
                        {
                            QuicPunchLog.Info($"[Handshake Glare] Simultaneous connection detected. Local peer wins ({currentPeer?.Id} > {peerId}). Authoritative local request takes precedence.");
                            session.MarkRejected();
                            session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                            return;
                        }
                        else
                        {
                            QuicPunchLog.Info($"[Handshake Glare] Simultaneous connection detected. Remote peer wins ({peerId} > {currentPeer?.Id}). Yielding local request to process incoming.");
                            outboundNegotiation.IsYielded = true;
                            wasYielded = true;
                            try { outboundNegotiation.Cts.Cancel(); } catch { }
                        }
                    }

                    long workerGen = qc.LifecycleGeneration;
                    var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(qc.LifecycleToken);
                    handshakeCts.CancelAfter(qc.HandshakePendingTtl);

                    var worker = new IncomingHandshakeWorker(guid, workerGen, handshakeCts);
                    if (!qc.TryRegisterIncomingWorker(worker))
                    {
                        session.MarkRejected();
                        session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                        handshakeCts.Dispose();
                        return;
                    }

                    worker.Task = Task.Run(async () =>
                    {
                        UdpClient? nudp = null;
                        CancellationTokenSource? decisionLinkedCts = null;
                        bool sessionHandedOff = false;
                        try
                        {
                            var token = handshakeCts.Token;
                            token.ThrowIfCancellationRequested();

                            HandShakeType decidedResponse = HandShakeType.Unsupported;
                            ushort decidedPort = 0;

                            if (qc.ProtocolHandlers.TryGetValue(connectionType, out var handler))
                            {
                                HandshakeDecision decision;

                                bool isTrusted = qc.IsTrustedPeer(handshakePeer);
                                bool isAutoAccepted = qc.AutoAcceptUntrustedConnections
                                    || (isTrusted && (
                                        qc.AutoAcceptConnections
                                        || wasYielded
                                        || qc.IsPeerAutoAccepted(peerId)));

                                if (isAutoAccepted)
                                {
                                    decision = new HandshakeDecision(true, (ushort)0, CancellationToken.None);
                                }
                                else if (!isTrusted && !qc.AutoAcceptUntrustedConnections)
                                {
                                    // A UI/callback is not allowed to turn discovery into trust.
                                    // Untrusted identities are rejected by the core before any
                                    // application-level decision handler is invoked.
                                    decision = new HandshakeDecision(false, null, CancellationToken.None);
                                }
                                else
                                {
                                    var epForHandshake = remoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                                    decision = await qc._manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, epForHandshake, peerId, handshakePeer.CertHash), TimeSpan.FromSeconds(30), true, token).ConfigureAwait(false);
                                }

                                if (decision.Accepted)
                                {
                                    decidedResponse = HandShakeType.Accept;
                                    decidedPort = (ushort)(decision.Port ?? 0);
                                    if (decision.Ct.HasValue && decision.Ct.Value.CanBeCanceled)
                                    {
                                        decisionLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, decision.Ct.Value);
                                        token = decisionLinkedCts.Token;
                                    }
                                }
                                else
                                {
                                    decidedResponse = HandShakeType.Decline;
                                    decidedPort = 0;
                                }
                            }

                            token.ThrowIfCancellationRequested();

                            List<CandidateEndpoint>? localCandidates = null;
                            if (decidedResponse == HandShakeType.Accept && transport == TransportType.Wan)
                            {
                                (nudp, decidedPort, localCandidates) = await qc.CreateBoundSocketAndGatherCandidatesAsync(
                                    decidedPort, $"[HANDSHAKE ACCEPT] remote: {remoteEndPoint}", token).ConfigureAwait(false);
                            }

                            token.ThrowIfCancellationRequested();

                            byte[] payload = qc.GenerateHandshakePayload(decidedResponse, decidedPort, connectionType, guid, localCandidates, transport);
                            if (decidedResponse == HandShakeType.Accept)
                            {
                                session.MarkCompleted();
                            }
                            else
                            {
                                session.MarkRejected();
                            }
                            session.ResponsePayloadTcs.TrySetResult(payload);
                            await qc.SendResponseAsync(payload, remoteEndPoint, transport, torChannel).ConfigureAwait(false);

                            if (transport == TransportType.Wan)
                            {
                                _ = Task.Run(async () =>
                                {
                                    for (int i = 0; i < 3; i++)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        try { await Task.Delay(25, token).ConfigureAwait(false); } catch { break; }
                                        await qc.SendResponseAsync(payload, remoteEndPoint, transport, torChannel).ConfigureAwait(false);
                                    }
                                });
                            }

                            token.ThrowIfCancellationRequested();

                            if (decidedResponse == HandShakeType.Accept && handler != null)
                            {
                                if (!qc.AvailablePeers.TryGetValue(peerId, out var targetPeer))
                                {
                                    targetPeer = handshakePeer;
                                }

                                if (transport == TransportType.Wan && nudp != null)
                                {
                                    var connection = await QuicPunchConnection.InitQuicConnectionCore(qc, qc.GetCurrentPeer(transport), nudp, targetPeer, remoteCandidates, remotePort, guid, qc.GetCertManager(transport).PeerCertificate!, handler.CompressionOptions, token).ConfigureAwait(false);

                                    token.ThrowIfCancellationRequested();

                                    if (connection.Connection == null || connection.Stream == null)
                                    {
                                        _ = Task.Run(async () => await handler.DeniedAsync(targetPeer, token).ConfigureAwait(false));
                                    }
                                    else
                                    {
                                        bool registered = await qc.RegisterProtocolSessionAsync(targetPeer.Id, connectionType, connection.Connection, connection.Stream, workerGen).ConfigureAwait(false);
                                        if (registered)
                                        {
                                            sessionHandedOff = true;
                                            handshakeCts.CancelAfter(Timeout.InfiniteTimeSpan);

                                            var capturedCts = handshakeCts;
                                            var capturedDecisionCts = decisionLinkedCts;
                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    await handler.HandleAsync(connection.Connection, connection.Stream, targetPeer, token).ConfigureAwait(false);
                                                }
                                                finally
                                                {
                                                    await qc.UnregisterProtocolSessionAsync(targetPeer.Id, connectionType, connection.Connection).ConfigureAwait(false);
                                                    try { await connection.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
                                                    try { await connection.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
                                                    try { capturedDecisionCts?.Dispose(); } catch { }
                                                    try { capturedCts.Dispose(); } catch { }
                                                }
                                            }, token);
                                        }
                                    }
                                }
                                else if (transport == TransportType.Tor && (torChannel != null || handshakePeer.TorChannel != null))
                                {
                                    var activeChannel = torChannel ?? handshakePeer.TorChannel!;
                                    var dummyConn = activeChannel.CreateQuicConnection();
                                    try
                                    {
                                        var stream = await dummyConn.AcceptInboundStreamAsync(token).ConfigureAwait(false);
                                        token.ThrowIfCancellationRequested();

                                        bool registered = await qc.RegisterProtocolSessionAsync(targetPeer.Id, connectionType, dummyConn, stream, workerGen).ConfigureAwait(false);
                                        if (registered)
                                        {
                                            sessionHandedOff = true;
                                            handshakeCts.CancelAfter(Timeout.InfiniteTimeSpan);

                                            var capturedCts = handshakeCts;
                                            var capturedDecisionCts = decisionLinkedCts;
                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    await handler.HandleAsync(dummyConn, stream, targetPeer, token).ConfigureAwait(false);
                                                }
                                                finally
                                                {
                                                    await qc.UnregisterProtocolSessionAsync(targetPeer.Id, connectionType, dummyConn).ConfigureAwait(false);
                                                    try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
                                                    try { await dummyConn.DisposeAsync().ConfigureAwait(false); } catch { }
                                                    try { capturedDecisionCts?.Dispose(); } catch { }
                                                    try { capturedCts.Dispose(); } catch { }
                                                }
                                            }, token);
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        try { await dummyConn.DisposeAsync().ConfigureAwait(false); } catch { }
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        QuicPunchLog.Error("[TOR QUIC HANDLER ERROR]", ex);
                                        await handler.DeniedAsync(targetPeer, token).ConfigureAwait(false);
                                        try { await dummyConn.DisposeAsync().ConfigureAwait(false); } catch { }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            session.MarkRejected();
                            session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                            try { nudp?.Dispose(); } catch { }
                        }
                        catch (InvalidOperationException)
                        {
                            session.MarkRejected();
                            session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                            try { nudp?.Dispose(); } catch { }
                        }
                        catch (Exception ex)
                        {
                            session.MarkRejected();
                            session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                            QuicPunchLog.Error("[HandshakeHandler] Error processing handshake request", ex);
                            try { nudp?.Dispose(); } catch { }
                        }
                        finally
                        {
                            qc.UnregisterIncomingWorker(guid);
                            if (!sessionHandedOff)
                            {
                                try { decisionLinkedCts?.Dispose(); } catch { }
                                try { handshakeCts.Dispose(); } catch { }
                            }
                        }
                    });
                    return;

                case HandShakeType.Accept:
                    if (qc._manager.Approve(guid, remotePort, remoteCandidates))
                    {
                        QuicPunchLog.Info($"Received handshake ACCEPT from {remoteEndPoint} (Candidates: {remoteCandidates.Count})");
                    }
                    return;

                case HandShakeType.Decline or HandShakeType.Unsupported:
                    QuicPunchLog.Info($"Handshake canceled from {remoteEndPoint}");
                    qc._manager.Reject(guid);
                    return;
            }
        }

        private static void ResendCachedResponse(QuicPunch qc, IncomingHandshakeSession session, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel)
        {
            QuicPunchLog.Info($"[HANDSHAKE IDEMPOTENT] Duplicate Handshake Request received for existing session {session.ConnectionGuid}. Resending cached response.");
            _ = Task.Run(async () =>
            {
                try
                {
                    var cachedPayload = await session.ResponsePayloadTcs.Task.WaitAsync(qc.LifecycleToken).ConfigureAwait(false);
                    if (cachedPayload != null && cachedPayload.Length > 0)
                    {
                        await qc.SendResponseAsync(cachedPayload, remoteEndPoint, transport, torChannel).ConfigureAwait(false);
                    }
                }
                catch { }
            });
        }
    }
}
