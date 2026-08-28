using QuicPunch.Helpers;
using System;
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
            PeerInfo ownPeer, UdpClient nudp, PeerInfo remotePeer, IReadOnlyList<CandidateEndpoint>? remoteCandidates, ushort peerPort, Guid connectionGuid, CancellationToken mainCt)
        {
            try
            {
                QuicPunchLog.Info($"[HOLE PUNCH] Starting UDP connectivity checks with peer {remotePeer.Name ?? "Peer"} ({remotePeer.Id}) (Guid: {connectionGuid})");
                using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt);
                _ = SendLoopAsync(nudp, remotePeer, remoteCandidates, peerPort, connectionGuid, ownPeer, punchCts.Token);

                var remoteEndpoint = await ReceiveHoleLoopAsync(nudp, connectionGuid, ownPeer, remotePeer, punchCts.Token);
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
            using var openPortCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var openPortLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, openPortCts.Token);

            var udpResult = await OpenPortCore(ownPeer, nudp, remotePeer, remoteCandidates, peerPort, connectionGuid, openPortLinkedCts.Token)
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
                            await qc.WaitForQuicReadyAsync(connectionGuid, TimeSpan.FromSeconds(10), linkedCts.Token).ConfigureAwait(false);
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

                    (connection, stream) = await TryRunClient(udpResult.remoteEndpoint, ownCertificate, remotePeer.CertHash, localPort, nudp, linkedCts.Token, "quic-punch").ConfigureAwait(false);
                }

                if (connection != null && stream != null)
                {
                    QuicPunchLog.Info($"[SUCCESS] QUIC connection established with {remotePeer.Name ?? "Peer"} ({udpResult.remoteEndpoint})!");
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

        public static async Task<IPEndPoint?> ReceiveHoleLoopAsync(UdpClient udp, Guid connectionGuid, PeerInfo ownPeer, PeerInfo remotePeer, CancellationToken token)
        {
            byte[] ackBody = new byte[QuicPunch.MagicHeader.Length + 1 + 16 + 16];
            Buffer.BlockCopy(QuicPunch.MagicHeader, 0, ackBody, 0, QuicPunch.MagicHeader.Length);
            ackBody[QuicPunch.MagicHeader.Length] = (byte)QuicPunchStructures.MessageType.Ack;
            connectionGuid.ToByteArray().CopyTo(ackBody.AsSpan(QuicPunch.MagicHeader.Length + 1, 16));
            ownPeer.IdRaw.CopyTo(ackBody.AsSpan(QuicPunch.MagicHeader.Length + 1 + 16, 16));

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await udp.ReceiveAsync(token);

                    if (result.Buffer.Length < QuicPunch.MagicHeader.Length + 1 + 16 + 16)
                        continue;

                    for (int i = 0; i < QuicPunch.MagicHeader.Length; i++)
                    {
                        if (result.Buffer[i] != QuicPunch.MagicHeader[i])
                            goto nextLoop;
                    }

                    using (MemoryStream ms = new MemoryStream(result.Buffer))
                    using (BinaryReader r = new BinaryReader(ms))
                    {
                        r.BaseStream.Position = QuicPunch.MagicHeader.Length;
                        var messageType = (QuicPunchStructures.MessageType)r.ReadByte();

                        if (messageType == QuicPunchStructures.MessageType.FinalHandshake || messageType == QuicPunchStructures.MessageType.Ack)
                        {
                            var recvGuid = new Guid(r.ReadBytes(16));
                            var senderId = new Guid(r.ReadBytes(16));

                            if (recvGuid != connectionGuid)
                                continue;

                            if (senderId != remotePeer.Id)
                                continue;

                            for (int a = 0; a < 4; a++)
                            {
                                try { await udp.SendAsync(ackBody, result.RemoteEndPoint, token).ConfigureAwait(false); } catch { }
                            }

                            return result.RemoteEndPoint;
                        }
                    }

                    nextLoop:;
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

            return null;
        }

        public static async Task SendLoopAsync(
            UdpClient udp, PeerInfo peer, IReadOnlyList<CandidateEndpoint>? remoteCandidates,
            ushort askedPort, Guid connectionGuid, PeerInfo ownPeer, CancellationToken token)
        {
            try
            {
                byte[] payload;

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(QuicPunch.MagicHeader);
                    w.Write((byte)QuicPunchStructures.MessageType.FinalHandshake);
                    w.Write(connectionGuid.ToByteArray());
                    w.Write(ownPeer.IdRaw);
                    payload = ms.ToArray();
                }

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
                            await udp.SendAsync(payload, ep).ConfigureAwait(false);
                        }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }
                    }
                    await Task.Delay(125, token).ConfigureAwait(false);
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

        public static readonly List<SslApplicationProtocol> SupportedProtocols = new List<SslApplicationProtocol> { new SslApplicationProtocol("quic-punch") };
        public static async Task<(QuicConnection? Connection, QuicStream? Stream)> TryRunServer(
            int localPort, X509Certificate2 ownCertificate, byte[] peerCertificate, UdpClient? holePunchUdp, CancellationToken token, Func<Task>? onListening = null)
        {
            if (holePunchUdp != null)
            {
                try { holePunchUdp.Close(); holePunchUdp.Dispose(); } catch { }
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            var options = new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                ApplicationProtocols = SupportedProtocols,
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = SupportedProtocols,
                        ServerCertificate = ownCertificate,
                        ClientCertificateRequired = true,

                        RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                        {
                            if (certificate == null || peerCertificate == null)
                            {
                                QuicPunchLog.Info($"[SERVER TLS] Validation rejected: certificate is null ({certificate == null}), peerCertificate is null ({peerCertificate == null})");
                                return false;
                            }

                            byte[] clientPublicKey = certificate.GetPublicKey();
                            byte[] clientHash = SHA3_256.HashData(clientPublicKey);

                            var valid = CryptographicOperations.FixedTimeEquals(clientHash, peerCertificate);

                            QuicPunchLog.Info("Client cert hash: " + Convert.ToHexString(clientHash) + " valid: " + valid);

                            return valid;
                        }
                    },

                    MaxInboundBidirectionalStreams = 512,
                    MaxInboundUnidirectionalStreams = 512,
                    IdleTimeout = TimeSpan.FromMinutes(10),
                    KeepAliveInterval = TimeSpan.FromSeconds(19)
                }),
            };

            QuicListener? listener = null;
            int bindTries = 0;
            while (!token.IsCancellationRequested && bindTries < 10)
            {
                try
                {
                    listener = await QuicListener.ListenAsync(options, token);
                    break;
                }
                catch when (bindTries < 9 && !token.IsCancellationRequested)
                {
                    bindTries++;
                    await Task.Delay(20, token);
                }
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
            IPEndPoint targetPeer, X509Certificate2 ownCertificate, byte[] peerCertificate, int localPort, UdpClient? holePunchUdp, CancellationToken token, string? targetHost = null)
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = targetPeer,
                LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = (!string.IsNullOrWhiteSpace(targetHost) && !targetHost.Contains('@')) ? targetHost : "quic-punch",
                    ApplicationProtocols = SupportedProtocols,
                    ClientCertificates = new X509Certificate2Collection(ownCertificate),
                    LocalCertificateSelectionCallback = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                    {
                        return ownCertificate;
                    },
                    RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate == null || peerCertificate == null)
                        {
                            QuicPunchLog.Info($"[CLIENT TLS] Validation rejected: certificate is null ({certificate == null}), peerCertificate is null ({peerCertificate == null})");
                            return false;
                        }

                        byte[] serverPublicKey = certificate.GetPublicKey();
                        byte[] serverHash = SHA3_256.HashData(serverPublicKey);

                        var valid = CryptographicOperations.FixedTimeEquals(serverHash, peerCertificate);

                        QuicPunchLog.Info("Server cert hash: " + Convert.ToHexString(serverHash) + " valid: " + valid);

                        return valid;
                    }
                },

                MaxInboundBidirectionalStreams = 512,
                MaxInboundUnidirectionalStreams = 512,
                IdleTimeout = TimeSpan.FromMinutes(10),
                KeepAliveInterval = TimeSpan.FromSeconds(19),

                HandshakeTimeout = TimeSpan.FromSeconds(12),
            };

            QuicConnection? connection = null;
            int backoffMs = 50;

            if (holePunchUdp != null)
            {
                try { holePunchUdp.Close(); holePunchUdp.Dispose(); } catch { }
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    connection = await QuicConnection.ConnectAsync(options, token).ConfigureAwait(false);
                    QuicPunchLog.Info("[CLIENT] Connected successfully!");
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
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

        public static bool AmIServer(PeerInfo ownPeer, PeerInfo remotePeer)
        {
            if (ownPeer?.IdRaw != null && remotePeer?.IdRaw != null)
            {
                return ownPeer.Id.CompareTo(remotePeer.Id) > 0;
            }

            if (ownPeer?.CertHash != null && remotePeer?.CertHash != null)
            {
                return Convert.ToBase64String(ownPeer.CertHash).CompareTo(Convert.ToBase64String(remotePeer.CertHash)) > 0;
            }

            return true;
        }
    }
}
