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

            if (r.BaseStream.Length - r.BaseStream.Position < 8 + 1 + CertManager.SignatureLength)
                return;
            long timestamp = r.ReadInt64();
            long now = PreciseTime.GetCorrectTime().Ticks;
            if (timestamp < now - TimeSpan.FromSeconds(25).Ticks || timestamp > now + TimeSpan.FromSeconds(5).Ticks)
                return;

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

            if (!handshakePeer.VerifySignature(buffer.AsSpan(0, (int)r.BaseStream.Position - signatureHandshake.Length), signatureHandshake))
            {
                QuicPunchLog.Info("Received invalid signature from " + remoteEndPoint);
                return;
            }

            // Discovery owns control routing. A signed datagram does not prove that
            // its source address belongs to the signer, even during negotiation.
            if (transport == TransportType.Wan && !Equals(handshakePeer.ActiveEndPoint, remoteEndPoint))
            {
                QuicPunchLog.Info($"[HANDSHAKE ROUTING] Ignored authenticated {handShakeType} from {handshakePeer.Name} at {remoteEndPoint}: discovery control endpoint is {handshakePeer.ActiveEndPoint} (session {guid}).");
                return;
            }

            handshakePeer.MarkSeen();

            switch (handShakeType)
            {
                case HandShakeType.Request:
                    lock (qc.HandshakeCoordinationLock)
                    {
                        var session = qc.GetOrAddIncomingHandshakeSession(guid, peerId, connectionType, transport);
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

                        long transportGeneration = qc.LifecycleGeneration;
                        bool transportReserved = connectionType == Guid.Empty;
                        if (transportReserved && (qc.IncomingHandshakeSessions.Values.Any(other =>
                            other.ConnectionGuid != guid && other.PeerId == peerId && other.ProtocolId == Guid.Empty &&
                            qc.HasActiveIncomingWorker(other.ConnectionGuid)) || !qc.TryReservePeerTransport(peerId, transportGeneration)))
                        {
                            session.MarkRejected();
                            var decline = qc.GenerateHandshakePayload(HandShakeType.Decline, 0, connectionType, guid, null, transport);
                            session.ResponsePayloadTcs.TrySetResult(decline);
                            _ = qc.SendResponseAsync(decline, remoteEndPoint, transport, torChannel);
                            return;
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
                                if (transportReserved) qc.ReleasePeerTransport(peerId, transportGeneration);
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
                        var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(qc.LifecycleToken, qc.TransportLifetime(transport));
                        handshakeCts.CancelAfter(qc.HandshakePendingTtl);

                        var worker = new IncomingHandshakeWorker(guid, workerGen, handshakeCts);
                        if (!qc.TryRegisterIncomingWorker(worker))
                        {
                            session.MarkRejected();
                            session.ResponsePayloadTcs.TrySetResult(Array.Empty<byte>());
                            handshakeCts.Dispose();
                            if (transportReserved) qc.ReleasePeerTransport(peerId, transportGeneration);
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

                                if (connectionType == Guid.Empty && qc.TryGetPeerMultiplexer(peerId, out var existingMux) &&
                                    existingMux != null && !existingMux.IsDisposed)
                                {
                                    // The remote app may have restarted while our old QUIC
                                    // connection still looks open. Only a correlated reply
                                    // justifies declining its replacement request.
                                    // Idle tunnels have no application state to preserve;
                                    // do not spend a full heartbeat window blocking restart.
                                    TimeSpan? probeTimeout = existingMux.HasApplicationSessions ? null : TimeSpan.FromSeconds(1);
                                    if (await existingMux.ProbeLivenessAsync(token, probeTimeout).ConfigureAwait(false))
                                    {
                                        QuicPunchLog.Info($"[Handshake] Existing tunnel with {peerId} answered its liveness probe; preserving active sessions.");
                                        session.MarkRejected();
                                        var decline = qc.GenerateHandshakePayload(HandShakeType.Decline, 0, connectionType, guid, null, transport);
                                        session.ResponsePayloadTcs.TrySetResult(decline);
                                        await qc.SendResponseAsync(decline, remoteEndPoint, transport, torChannel).ConfigureAwait(false);
                                        return;
                                    }
                                    QuicPunchLog.Info($"[Handshake] Previous tunnel with {peerId} did not answer; accepting its replacement without a decline/backoff cycle.");
                                }

                                HandShakeType decidedResponse = HandShakeType.Unsupported;
                                ushort decidedPort = 0;

                                IProtocolHandler? handler = null;
                                bool isMultiplexerTunnel = connectionType == Guid.Empty;
                                if (isMultiplexerTunnel || qc.ProtocolHandlers.TryGetValue(connectionType, out handler))
                                {
                                    HandshakeDecision decision;

                                    bool isTrusted = qc.IsTrustedPeer(handshakePeer);
                                    bool policyDenied = handler?.IncomingSessionPolicy(handshakePeer) == false;
                                    bool acceptAutomatically = !policyDenied && (isMultiplexerTunnel ||
                                        (isTrusted && (wasYielded || qc.HasAutomaticProtocolPermission(handshakePeer, connectionType))));

                                    if (acceptAutomatically)
                                    {
                                        decision = new HandshakeDecision(true, (ushort)0, CancellationToken.None);
                                    }
                                    else if (!isTrusted || policyDenied)
                                    {
                                        // A UI/callback is not allowed to turn discovery into trust.
                                        // Untrusted identities are rejected by the core before any
                                        // application-level decision handler is invoked.
                                        decision = new HandshakeDecision(false, null, CancellationToken.None);
                                    }
                                    else
                                    {
                                        var epForHandshake = remoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
                                        decision = await qc._manager.WaitForDecisionAsync(new HandshakeRequest(guid, connectionType, epForHandshake, peerId, handshakePeer.CertHash, transport), TimeSpan.FromSeconds(30), true, token).ConfigureAwait(false);
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
                                try { await qc.SendResponseAsync(payload, remoteEndPoint, transport, torChannel).ConfigureAwait(false); }
                                catch (SocketException ex) when (transport == TransportType.Wan && !token.IsCancellationRequested)
                                {
                                    // A transient UDP send error must not abandon an accepted
                                    // handshake. Follow-up sends and duplicate requests can recover it.
                                    QuicPunchLog.Info($"[HANDSHAKE] Initial response to {remoteEndPoint} could not be sent: {ex.SocketErrorCode}.");
                                }

                                if (transport == TransportType.Wan)
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        for (int i = 0; i < 3; i++)
                                        {
                                            if (token.IsCancellationRequested) break;
                                            try { await Task.Delay(25, token).ConfigureAwait(false); } catch { break; }
                                            try { await qc.SendResponseAsync(payload, remoteEndPoint, transport, torChannel).ConfigureAwait(false); }
                                            catch (Exception ex) when (!token.IsCancellationRequested)
                                            {
                                                QuicPunchLog.Info($"[HANDSHAKE] Follow-up response to {remoteEndPoint} could not be sent: {ex.Message}");
                                                break;
                                            }
                                        }
                                    });
                                }

                                token.ThrowIfCancellationRequested();

                                if (decidedResponse == HandShakeType.Accept && (handler != null || isMultiplexerTunnel))
                                {
                                    if (!qc.AvailablePeers.TryGetValue(peerId, out var targetPeer))
                                    {
                                        targetPeer = handshakePeer;
                                    }

                                    if (transport == TransportType.Wan && nudp != null)
                                    {
                                        var connection = await QuicPunchConnection.InitQuicConnectionCore(qc, qc.GetCurrentPeer(transport), nudp, targetPeer, remoteCandidates, remotePort, guid, qc.GetCertManager(transport).PeerCertificate!, token).ConfigureAwait(false);

                                        token.ThrowIfCancellationRequested();

                                        if (connection.Connection != null && connection.Stream != null)
                                        {
                                            bool isServer = QuicPunchConnection.AmIServer(qc.GetCurrentPeer(transport), targetPeer);
                                            var mux = new Multiplexer.QuicPeerMultiplexer(
                                                connection.Connection,
                                                targetPeer,
                                                isServer,
                                                protocolLookup: id => qc.ProtocolHandlers.TryGetValue(id, out var h) ? h : null,
                                                accessCheck: qc.IsTrustedPeer,
                                                sessionAccessCheck: (p, id, ct) => qc.AuthorizeIncomingSessionAsync(p, id, TransportType.Wan, ct),
                                                initialControlStream: connection.Stream,
                                                sessionRegisteredCallback: (protoId, conn, str) => qc.RegisterProtocolSessionAsync(targetPeer.Id, protoId, conn, str, workerGen),
                                                sessionUnregisteredCallback: (protoId, conn) => qc.UnregisterProtocolSessionAsync(targetPeer.Id, protoId, conn),
                                                recoveryAuthorization: qc.AuthorizeRecoverableSessionAsync,
                                                recoveryFinished: qc.FinishRecoverableSession,
                                                sessionStateChanged: qc.RaiseSessionState,
                                                transportOptions: qc.PeerTransport,
                    recoveryHandlerFinished: qc.RecoveryHandlerFinished);

                                            await mux.StartAsync(token).ConfigureAwait(false);
                                            qc.RegisterPeerMultiplexer(targetPeer.Id, mux);

                                            sessionHandedOff = true;
                                            handshakeCts.CancelAfter(Timeout.InfiniteTimeSpan);
                                        }
                                    }
                                    else if (transport == TransportType.Tor && (torChannel != null || handshakePeer.TorChannel != null))
                                    {
                                        var activeChannel = torChannel ?? handshakePeer.TorChannel!;
                                        var dummyConn = activeChannel.CreateQuicConnection();
                                        try
                                        {
                                            // The handshake initiator is not necessarily the logical QUIC client.
                                            // Let the multiplexer establish its control stream according to the same
                                            // deterministic identity ordering used by the native QUIC path instead of
                                            // unconditionally waiting for an inbound stream as the legacy Tor path did.
                                            bool isServer = QuicPunchConnection.AmIServer(qc.GetCurrentPeer(transport), targetPeer);
                                            var mux = new Multiplexer.QuicPeerMultiplexer(
                                                dummyConn,
                                                targetPeer,
                                                isServer,
                                                protocolLookup: id => qc.ProtocolHandlers.TryGetValue(id, out var h) ? h : null,
                                                accessCheck: qc.IsTrustedPeer,
                                                sessionAccessCheck: (p, id, ct) => qc.AuthorizeIncomingSessionAsync(p, id, TransportType.Tor, ct),
                                                sessionRegisteredCallback: (protoId, conn, str) => qc.RegisterProtocolSessionAsync(targetPeer.Id, protoId, conn, str, workerGen),
                                                sessionUnregisteredCallback: (protoId, conn) => qc.UnregisterProtocolSessionAsync(targetPeer.Id, protoId, conn),
                                                recoveryAuthorization: qc.AuthorizeRecoverableSessionAsync,
                                                recoveryFinished: qc.FinishRecoverableSession,
                                                sessionStateChanged: qc.RaiseSessionState,
                                                transportOptions: qc.PeerTransport,
                    recoveryHandlerFinished: qc.RecoveryHandlerFinished);

                                            await mux.StartAsync(token).ConfigureAwait(false);
                                            qc.RegisterPeerMultiplexer(targetPeer.Id, mux);

                                            sessionHandedOff = true;
                                            handshakeCts.CancelAfter(Timeout.InfiniteTimeSpan);
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            try { await dummyConn.DisposeAsync().ConfigureAwait(false); } catch { }
                                            throw;
                                        }
                                        catch (Exception ex)
                                        {
                                            QuicPunchLog.Error("[TOR QUIC HANDLER ERROR]", ex);
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
                                if (transportReserved) qc.ReleasePeerTransport(peerId, transportGeneration);
                                if (!sessionHandedOff)
                                {
                                    try { decisionLinkedCts?.Dispose(); } catch { }
                                    try { handshakeCts.Dispose(); } catch { }
                                }
                            }
                        });
                        _ = worker.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        return;
                    }

                case HandShakeType.Accept:
                    if ((transport == TransportType.Tor || remotePort > 0) && qc._manager.CompleteRemote(guid, peerId, handshakePeer.CertHash, connectionType,
                        transport, remoteEndPoint, new HandshakeDecision(true, remotePort, null, remoteCandidates)))
                    {
                        QuicPunchLog.Info($"Received handshake ACCEPT from {remoteEndPoint} (Candidates: {remoteCandidates.Count})");
                    }
                    return;

                case HandShakeType.Decline or HandShakeType.Unsupported:
                    QuicPunchLog.Info($"Handshake canceled from {remoteEndPoint}");
                    qc._manager.CompleteRemote(guid, peerId, handshakePeer.CertHash, connectionType,
                        transport, remoteEndPoint, new HandshakeDecision(false, null, null));
                    return;
            }
        }

        private static void ResendCachedResponse(QuicPunch qc, IncomingHandshakeSession session, EndPoint remoteEndPoint, TransportType transport, TorQuicConnectionManager? torChannel)
        {
            // Preparation may still be gathering candidates or awaiting a local
            // decision. The worker sends the first response; only replay a response
            // that actually exists, without queuing a task for every duplicate.
            if (!session.ResponsePayloadTcs.Task.IsCompletedSuccessfully || session.ResponsePayloadTcs.Task.Result.Length == 0)
                return;
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
