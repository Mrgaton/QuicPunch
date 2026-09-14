using QuicPunch.Helpers;
using QuicPunch.Security;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuicPunch
{
    internal class QuicPunchConnection
    {
        //TODO: also implement stun to retrieve external port?
        public static async Task<(bool Success, UdpClient? client, IPEndPoint? remoteEndpoint)> OpenPortCore(
            PeerInfo ownPeer, UdpClient nudp, PeerInfo remotePeer, IReadOnlyList<CandidateEndpoint>? remoteCandidates,
            ushort peerPort, Guid connectionGuid, CancellationToken mainCt, ECDsa? ownPrivateKey = null, byte[]? passwordHash = null)
        {
            try
            {
                QuicPunchLog.Info($"[HOLE PUNCH] Starting UDP connectivity checks with peer {remotePeer.Name ?? "Peer"} ({remotePeer.Id}) (Guid: {connectionGuid})");
                using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt);
                _ = SendLoopAsync(nudp, remotePeer, remoteCandidates, peerPort, connectionGuid, ownPeer, ownPrivateKey, passwordHash, punchCts.Token);

                var remoteEndpoint = await ReceiveHoleLoopAsync(nudp, connectionGuid, ownPeer, remotePeer, ownPrivateKey, passwordHash, punchCts.Token);
                punchCts.Cancel();

                if (remoteEndpoint == null)
                {
                    QuicPunchLog.Info($"[HOLE PUNCH] Failed to hole-punch with {remotePeer.Name ?? "Peer"}: no candidate response received within timeout.");
                    return (false, null, null);
                }

                QuicPunchLog.Info($"[HOLE PUNCH] Succeeded! Nominated peer-reflexive endpoint: {remoteEndpoint}");
                return (true, nudp, remoteEndpoint);
            }
            catch (OperationCanceledException)
            {
                QuicPunchLog.Info($"[HOLE PUNCH] Canceled for peer {remotePeer.Name ?? "Peer"}.");
                return (false, null, null);
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error($"Error in OpenPortCore: {ex.Message}", ex);
                return (false, null, null);
            }
        }


        public static async Task<(QuicConnection Connection, Stream Stream)> InitQuicConnectionCore(
            QuicPunch? qc, PeerInfo ownPeer, UdpClient nudp, PeerInfo remotePeer, IReadOnlyList<CandidateEndpoint>? remoteCandidates,
            ushort peerPort, Guid connectionGuid, X509Certificate2 ownCertificate, ZstandardCompressionOptions? compressionOptions, CancellationToken mainCt)
        {
            MsQuicTuner.EnsureOptimalConfiguration();

            if (remotePeer.ResumptionTicket == null && qc?.PeerStore != null && remotePeer.CertHash.Length > 0)
            {
                if (qc.PeerStore.TryGetResumptionTicket(remotePeer.CertHash, out var cachedTicket) && cachedTicket != null)
                {
                    remotePeer.ResumptionTicket = cachedTicket;
                }
            }

            using var openPortCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var openPortLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, openPortCts.Token);

            var udpResult = await OpenPortCore(ownPeer, nudp, remotePeer, remoteCandidates, peerPort, connectionGuid, openPortLinkedCts.Token, qc?.CertManager?.Curve, qc?.PasswordHash)
                .WaitAsync(openPortLinkedCts.Token);

            if (!udpResult.Success || udpResult.remoteEndpoint == null)
            {
                try { nudp.Dispose(); } catch { }
                return (null!, null!);
            }

            var localPort = ((IPEndPoint)nudp.Client.LocalEndPoint!).Port;
            bool isServer = AmIServer(ownPeer, remotePeer);

            QuicConnection? connection = null;
            QuicStream? stream = null;

            QuicPunchLog.Info($"\n--- QUIC Establishment: Acting as {(isServer ? "SERVER" : "CLIENT")} --- LocalPort: {localPort} -> Nominated Remote: {udpResult.remoteEndpoint}");

            using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, attemptCts.Token);

            try
            {
                if (isServer)
                {
                    (connection, stream) = await TryRunServer(localPort, ownCertificate, remotePeer.CertHash, nudp, linkedCts.Token, async () =>
                    {
                        if (qc != null)
                        {
                            QuicPunchLog.Info($"[QUIC SERVER] Bound listener on port {localPort}. Sending QUIC_READY signal to client...");
                            await qc.SendQuicReadyAsync(remotePeer, connectionGuid, (ushort)localPort).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                }
                else
                {
                    if (qc != null)
                    {
                        QuicPunchLog.Info($"[QUIC CLIENT] Awaiting QUIC_READY signal from server {remotePeer.Name}...");
                        try
                        {
                            await qc.WaitForQuicReadyAsync(connectionGuid, remotePeer.Id, TimeSpan.FromSeconds(10), linkedCts.Token).ConfigureAwait(false);
                            QuicPunchLog.Info($"[QUIC CLIENT] Received QUIC_READY signal from server {remotePeer.Name}. Proceeding to connect...");
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            QuicPunchLog.Info($"[QUIC CLIENT] WaitForQuicReady notice: {ex.Message}");
                        }
                        finally
                        {
                            qc.UnregisterPendingQuicReady(connectionGuid);
                        }
                    }

                    (connection, stream) = await TryRunClient(udpResult.remoteEndpoint, ownCertificate, remotePeer.CertHash, localPort, nudp, linkedCts.Token).ConfigureAwait(false);
                }

                if (connection != null && stream != null)
                {
                    connection.ApplyOptimalTuning();
                    QuicPunchLog.Info($"[SUCCESS] QUIC connection established with {remotePeer.Name ?? "Peer"} ({udpResult.remoteEndpoint})! Path MTU: {connection.PathMtu} bytes (BBR & PMTUD active)");

                    // Cache TLS 1.3 0-RTT session resumption ticket if available
                    if (connection.TryGetResumptionTicket(out var ticket) && ticket is { Length: > 0 })
                    {
                        remotePeer.ResumptionTicket = ticket;
                        if (qc != null)
                        {
                            try { qc.PeerStore?.SetResumptionTicket(remotePeer.CertHash, ticket, save: true); } catch { }
                        }
                    }

                    // Hook connection migration so IP roaming seamlessly updates peer routing
                    if (connection.DatagramChannel != null)
                    {
                        connection.DatagramChannel.OnPeerAddressChanged += newEp =>
                        {
                            QuicPunchLog.Info($"[CONNECTION MIGRATION] Peer {remotePeer.Name} shifted to new endpoint: {newEp}");
                            remotePeer.ActiveEndPoint = newEp;
                            if (qc != null)
                            {
                                try { qc.UpdateSavedPeerIfPresent(remotePeer); } catch { }
                            }
                        };
                    }

                    if (connection.TryGetTelemetry(out var telem))
                    {
                        remotePeer.LastTelemetry = telem;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                QuicPunchLog.Info($"[QUIC] {(isServer ? "Listening" : "Connecting")} timed out.");
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error("[QUIC] Failure", ex);
            }
            finally
            {
                try { nudp?.Dispose(); } catch { }
            }

            if (connection == null || stream == null)
            {
                if (connection != null)
                    try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }

                if (stream != null)
                    try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }

                QuicPunchLog.Info($"[FAILED] QUIC connection with {remotePeer.Name ?? "Peer"} failed to complete.");
                return (null!, null!);
            }

            if (compressionOptions != null)
            {
                return (connection, new CompressedTransparentStream(stream, compressionOptions));
            }

            return (connection, stream);
        }

        public static async Task<IPEndPoint?> ReceiveHoleLoopAsync(
            UdpClient udp, Guid connectionGuid, PeerInfo ownPeer, PeerInfo remotePeer,
            ECDsa? ownPrivateKey, byte[]? passwordHash, CancellationToken token)
        {
            byte[] ackBody = new byte[QuicPunch.MagicHeader.Length + 1 + 16 + 16];
            Buffer.BlockCopy(QuicPunch.MagicHeader, 0, ackBody, 0, QuicPunch.MagicHeader.Length);
            ackBody[QuicPunch.MagicHeader.Length] = (byte)QuicPunchStructures.MessageType.Ack;
            connectionGuid.ToByteArray().CopyTo(ackBody.AsSpan(QuicPunch.MagicHeader.Length + 1, 16));
            ownPeer.IdRaw.CopyTo(ackBody.AsSpan(QuicPunch.MagicHeader.Length + 1 + 16, 16));

            byte[] receiveBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        EndPoint remoteEndPoint = udp.Client.AddressFamily == AddressFamily.InterNetworkV6
                            ? new IPEndPoint(IPAddress.IPv6Any, 0)
                            : new IPEndPoint(IPAddress.Any, 0);

                        SocketReceiveFromResult result = await udp.Client.ReceiveFromAsync(
                            receiveBuffer.AsMemory(0, 65536),
                            SocketFlags.None,
                            remoteEndPoint,
                            token).ConfigureAwait(false);

                        int bytesRead = result.ReceivedBytes;

                        // High-security single-packet punch check (192 bytes)
                        if (bytesRead >= QuicPunchHolePacket.PacketSize)
                        {
                            ReadOnlySpan<byte> holeSpan = receiveBuffer.AsSpan(0, bytesRead);
                            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(holeSpan[..4]);
                            if (magic == QuicPunchHolePacket.MagicHeader)
                            {
                                bool valid = false;
                                try
                                {
                                    if (remotePeer.Curve != null)
                                    {
                                        valid = QuicPunchHolePacket.TryVerifyFast(
                                            holeSpan,
                                            expectedRecipientId: ownPeer.Id,
                                            ourCertHash: ownPeer.CertHash,
                                            senderPublicKey: remotePeer.Curve,
                                            localPasswordHash: passwordHash ?? ReadOnlySpan<byte>.Empty);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    QuicPunchLog.Info($"[HOLE PUNCH] Notice verifying single-packet punch: {ex.Message}");
                                }

                                if (valid)
                                {
                                    var senderEp = (IPEndPoint)result.RemoteEndPoint;
                                    QuicPunchLog.Info($"[HOLE PUNCH] Verified single-packet punch (PoP Validated) from {remotePeer.Name ?? "Peer"}! Nominated: {senderEp}");
                                    for (int a = 0; a < 4; a++)
                                    {
                                        try { await udp.SendAsync(ackBody, senderEp, token).ConfigureAwait(false); } catch { }
                                    }
                                    return senderEp;
                                }
                                else
                                {
                                    QuicPunchLog.Info($"[HOLE PUNCH] Rejected single-packet punch from {result.RemoteEndPoint}: signature or password mismatch.");
                                    continue;
                                }
                            }
                        }

                        if (bytesRead < QuicPunch.MagicHeader.Length + 1 + 16 + 16)
                            continue;

                        ReadOnlySpan<byte> span = receiveBuffer.AsSpan(0, bytesRead);
                        if (!span.Slice(0, QuicPunch.MagicHeader.Length).SequenceEqual(QuicPunch.MagicHeader))
                            continue;

                        int offset = QuicPunch.MagicHeader.Length;
                        var messageType = (QuicPunchStructures.MessageType)span[offset++];

                        if (messageType == QuicPunchStructures.MessageType.FinalHandshake || messageType == QuicPunchStructures.MessageType.Ack)
                        {
                            var recvGuid = new Guid(span.Slice(offset, 16));
                            offset += 16;
                            var senderId = new Guid(span.Slice(offset, 16));

                            if (recvGuid != connectionGuid)
                                continue;

                            if (senderId != remotePeer.Id)
                                continue;

                            var senderEp = (IPEndPoint)result.RemoteEndPoint;
                            for (int a = 0; a < 4; a++)
                            {
                                try { await udp.SendAsync(ackBody, senderEp, token).ConfigureAwait(false); } catch { }
                            }

                            return senderEp;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode is SocketError.OperationAborted
                                                         or SocketError.Interrupted)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Error($"Error in ReceiveLoopAsync: {ex.Message}");
                        if (token.IsCancellationRequested)
                            break;
                        try
                        {
                            await Task.Delay(20, token);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(receiveBuffer);
            }

            return null;
        }

        public static async Task SendLoopAsync(
            UdpClient udp, PeerInfo peer, IReadOnlyList<CandidateEndpoint>? remoteCandidates,
            ushort askedPort, Guid connectionGuid, PeerInfo ownPeer,
            ECDsa? ownPrivateKey, byte[]? passwordHash, CancellationToken token)
        {
            try
            {
                byte[]? punchPacketBytes = null;
                if (ownPrivateKey != null && ownPeer.CertHash.Length == 32 && peer.CertHash.Length == 32)
                {
                    try
                    {
                        var punch = QuicPunchHolePacket.Create(
                            ownPeer.Id,
                            peer.Id,
                            ownPeer.CertHash,
                            peer.CertHash,
                            ownPrivateKey,
                            passwordHash);
                        punchPacketBytes = punch.Encode();
                    }
                    catch (Exception ex)
                    {
                        QuicPunchLog.Info($"[HOLE PUNCH] Notice creating single punch packet: {ex.Message}");
                    }
                }

                byte[] payload = new byte[QuicPunch.MagicHeader.Length + 1 + 16 + 16];
                Buffer.BlockCopy(QuicPunch.MagicHeader, 0, payload, 0, QuicPunch.MagicHeader.Length);
                payload[QuicPunch.MagicHeader.Length] = (byte)QuicPunchStructures.MessageType.FinalHandshake;
                connectionGuid.ToByteArray().CopyTo(payload.AsSpan(QuicPunch.MagicHeader.Length + 1, 16));
                ownPeer.IdRaw.CopyTo(payload.AsSpan(QuicPunch.MagicHeader.Length + 1 + 16, 16));

                var targetEndPoints = new HashSet<IPEndPoint>();
                if (remoteCandidates != null && remoteCandidates.Count > 0)
                {
                    foreach (var c in remoteCandidates)
                    {
                        if (c.EndPoint != null && Utilities.IsValidPeerAddress(c.EndPoint.Address))
                        {
                            targetEndPoints.Add(c.EndPoint);
                        }
                    }
                }

                // Always include known active and public endpoints from PeerInfo
                if (peer.ActiveEndPoint != null)
                {
                    targetEndPoints.Add(peer.ActiveEndPoint);
                    if (askedPort > 0)
                    {
                        targetEndPoints.Add(new IPEndPoint(peer.ActiveEndPoint.Address, askedPort));
                    }
                }

                if (peer.Addresses != null)
                {
                    foreach (var addr in peer.Addresses)
                    {
                        if (Utilities.IsValidPeerAddress(addr))
                        {
                            if (askedPort > 0) targetEndPoints.Add(new IPEndPoint(addr, askedPort));
                            if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Port > 0)
                                targetEndPoints.Add(new IPEndPoint(addr, peer.ActiveEndPoint.Port));
                            if (peer.PortArray is { Length: > 0 } ports)
                            {
                                foreach (var p in ports)
                                {
                                    if (p > 0) targetEndPoints.Add(new IPEndPoint(addr, p));
                                }
                            }
                        }
                    }
                }

                QuicPunchLog.Info($"[HOLE PUNCH] Probing {targetEndPoints.Count} candidate endpoint(s): {string.Join(", ", targetEndPoints)}");

                int tries = 0;
                while (!token.IsCancellationRequested)
                {
                    tries++;
                    foreach (var ep in targetEndPoints)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            if (punchPacketBytes != null)
                            {
                                await udp.SendAsync(punchPacketBytes, ep).ConfigureAwait(false);
                            }
                            await udp.SendAsync(payload, ep).ConfigureAwait(false);
                        }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }
                    }
                    // Fast burst on the first 3 cycles (25ms), then 125ms interval
                    int delayMs = tries <= 3 ? 25 : 125;
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                QuicPunchLog.Info($"[QuicPunchConnection] SendLoopAsync notice: {ex.Message}");
            }
        }

        public const string DefaultDisguiseHost = "cloudflare-quic.com";
        public static readonly List<SslApplicationProtocol> SupportedProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("h3"),          // RFC 9114 HTTP/3 standard ALPN (looks like normal web traffic to DPI)
            new SslApplicationProtocol("h3-29"),       // HTTP/3 draft 29 fallback
            new SslApplicationProtocol("quic-punch")   // Legacy backwards-compatible fallback
        };
        public static async Task<(QuicConnection? Connection, QuicStream? Stream)> TryRunServer(
            int localPort, X509Certificate2 ownCertificate, byte[] peerCertificate, UdpClient? holePunchUdp, CancellationToken token, Func<Task>? onListening = null)
        {
            var options = new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                ApplicationProtocols = SupportedProtocols,
                ConnectionOptionsCallback = (_, _, _) =>
                {
                    var serverConnOpts = new QuicServerConnectionOptions
                    {
                        DefaultStreamErrorCode = 0,
                        DefaultCloseErrorCode = 0,
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ApplicationProtocols = SupportedProtocols,
                            ServerCertificate = ownCertificate,
                            ClientCertificateRequired = true,

                            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                                ValidateRemotePeerCertificate(certificate, peerCertificate, "SERVER")
                        },

                        MaxInboundBidirectionalStreams = 512,
                        MaxInboundUnidirectionalStreams = 512,
                        IdleTimeout = TimeSpan.FromMinutes(10),
                        KeepAliveInterval = TimeSpan.FromSeconds(19)
                    };

                    if (MsQuicDatagramChannel.IsSupported)
                    {
                        MsQuicDatagramChannel.EnsureServerConfigurationPatched(serverConnOpts);
                    }

                    return ValueTask.FromResult(serverConnOpts);
                },
            };

            QuicListener? listener = null;
            int bindTries = 0;
            while (!token.IsCancellationRequested && bindTries < 10)
            {
                try
                {
                    if (MsQuicDatagramChannel.IsSupported)
                    {
                        MsQuicDatagramChannel.EnableDatagramsOnConfigurationCache();
                    }
                    // Zero-dead-window overlapping bind: bind listener while holePunchUdp is still open
                    listener = await QuicListener.ListenAsync(options, token);
                    MsQuicTuner.EnsureOptimalConfiguration();
                    if (holePunchUdp != null)
                    {
                        try { holePunchUdp.Close(); holePunchUdp.Dispose(); holePunchUdp = null; } catch { }
                    }
                    break;
                }
                catch when (bindTries < 9 && !token.IsCancellationRequested)
                {
                    bindTries++;
                    // Fallback for platforms/configurations that forbid overlapping listener binds:
                    if (holePunchUdp != null)
                    {
                        try { holePunchUdp.Close(); holePunchUdp.Dispose(); holePunchUdp = null; } catch { }
                    }
                    await Task.Delay(20, token);
                }
            }

            if (holePunchUdp != null)
            {
                try { holePunchUdp.Close(); holePunchUdp.Dispose(); holePunchUdp = null; } catch { }
            }

            if (listener == null)
                return (null, null);

            await using (listener)
            {
                QuicPunchLog.Info("[SERVER] Bound to port. Waiting for peer...");
                if (onListening != null)
                {
                    try { await onListening().ConfigureAwait(false); } catch { }
                }

                var nativeConn = await listener.AcceptConnectionAsync(token);
                MsQuicTuner.TryApplyOptimalTuning(nativeConn);

                try
                {
                    QuicConnection connection = QuicConnection.Wrap(nativeConn);
                    var stream = await connection.AcceptInboundStreamAsync(token);
                    byte[] headerByte = new byte[1];
                    await stream.ReadExactlyAsync(headerByte, token);

                    return (connection, stream);
                }
                catch
                {
                    await nativeConn.DisposeAsync();
                    throw;
                }
            }
        }

        public static async Task<(QuicConnection? Connection, QuicStream? Stream)> TryRunClient(
            IPEndPoint targetPeer, X509Certificate2 ownCertificate, byte[] peerCertificate, int localPort, UdpClient? holePunchUdp, CancellationToken token, string targetHost = DefaultDisguiseHost)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = targetPeer,
                LocalEndPoint = localPort > 0 ? new IPEndPoint(IPAddress.Any, localPort) : null,
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = string.IsNullOrWhiteSpace(targetHost) ? DefaultDisguiseHost : targetHost,
                    ApplicationProtocols = SupportedProtocols,
                    ClientCertificates = new X509Certificate2Collection(ownCertificate),
                    LocalCertificateSelectionCallback = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                    {
                        return ownCertificate;
                    },
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                        ValidateRemotePeerCertificate(certificate, peerCertificate, "CLIENT")
                },

                MaxInboundBidirectionalStreams = 512,
                MaxInboundUnidirectionalStreams = 512,
                IdleTimeout = TimeSpan.FromMinutes(10),
                KeepAliveInterval = TimeSpan.FromSeconds(19),

                HandshakeTimeout = TimeSpan.FromSeconds(12),
            };

            QuicConnection? connection = null;
            int backoffMs = 50;

            if (MsQuicDatagramChannel.IsSupported)
            {
                MsQuicDatagramChannel.EnsureClientConfigurationPatched(options);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    connection = await QuicConnection.ConnectAsync(options, token).ConfigureAwait(false);
                    connection.ApplyOptimalTuning();
                    QuicPunchLog.Info("[CLIENT] Connected successfully (BBR + PMTUD optimal tuning active)!");
                    if (holePunchUdp != null)
                    {
                        try { holePunchUdp.Close(); holePunchUdp.Dispose(); holePunchUdp = null; } catch { }
                    }
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (holePunchUdp != null)
                    {
                        try { holePunchUdp.Close(); holePunchUdp.Dispose(); holePunchUdp = null; } catch { }
                    }
                    QuicPunchLog.Info($"[QUIC CLIENT ATTEMPT] Connect to {targetPeer} failed ({ex.GetType().Name}: {ex.Message}). Retrying in {backoffMs}ms...");
                    try
                    {
                        await Task.Delay(backoffMs, token).ConfigureAwait(false);
                        backoffMs = Math.Min(backoffMs * 2, 400);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (holePunchUdp != null)
            {
                try { holePunchUdp.Close(); holePunchUdp.Dispose(); holePunchUdp = null; } catch { }
            }

            if (connection == null)
                return (null, null);

            try
            {
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { 0x00 }, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);

                return (connection, stream);
            }
            catch
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
                throw;
            }
        }

        private static bool ValidateRemotePeerCertificate(X509Certificate? certificate, byte[]? peerCertificate, string role)
        {
            if (certificate == null || peerCertificate == null)
            {
                QuicPunchLog.Info($"[{role} TLS] Validation rejected: certificate is null ({certificate == null}), peerCertificate is null ({peerCertificate == null})");
                return false;
            }

            byte[] publicKey = certificate.GetPublicKey();
            byte[] hash = SHA3_256.HashData(publicKey);

            var valid = CryptographicOperations.FixedTimeEquals(hash, peerCertificate);
            QuicPunchLog.Info($"{role} cert hash: " + Convert.ToHexString(hash) + " valid: " + valid);
            return valid;
        }

        public static bool AmIServer(PeerInfo ownPeer, PeerInfo remotePeer)
        {
            if (ownPeer?.IdRaw != null && remotePeer?.IdRaw != null)
            {
                return ownPeer.Id.CompareTo(remotePeer.Id) > 0;
            }

            if (ownPeer?.CertHash != null && remotePeer?.CertHash != null)
            {
                return ownPeer.CertHash.AsSpan().SequenceCompareTo(remotePeer.CertHash) > 0;
            }

            return true;
        }
    }
}
