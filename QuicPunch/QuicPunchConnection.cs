using QuicPunch.Helpers;
using QuicPunch.Security;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace QuicPunch
{
    internal partial class QuicPunchConnection
    {
        private static string? _androidQuicUnavailableReason;
        internal static string? AndroidQuicUnavailableReason => Volatile.Read(ref _androidQuicUnavailableReason);

        static QuicPunchConnection()
        {
            MsQuicNativeLoader.EnsureLoaded();
        }

        //TODO: also implement stun to retrieve external port?
        public static async Task<(bool Success, UdpClient? client, IPEndPoint? remoteEndpoint)> OpenPortCore(
            PeerInfo ownPeer, UdpClient nudp, PeerInfo remotePeer, IReadOnlyList<CandidateEndpoint>? remoteCandidates,
            ushort peerPort, Guid connectionGuid, CancellationToken mainCt, ECDsa? ownPrivateKey = null, byte[]? passwordHash = null)
        {
            try
            {
                QuicPunchLog.Info($"[HOLE PUNCH] Starting UDP connectivity checks with peer {remotePeer.Name ?? "Peer"} ({remotePeer.Id}) (Guid: {connectionGuid})");
                using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt);
                Task sender = SendLoopAsync(nudp, remotePeer, remoteCandidates, peerPort,
                    connectionGuid, ownPeer, ownPrivateKey, passwordHash, punchCts.Token);
                Task<(UdpClient? Socket, IPEndPoint? Endpoint)> primary = ReceivePrimary();
                IPEndPoint? birthdayTarget = BirthdayTarget(ownPeer, remotePeer, remoteCandidates, peerPort);
                Task<(UdpClient? Socket, IPEndPoint? Endpoint)>? birthday = birthdayTarget != null && ownPrivateKey != null
                    ? OpenBirthdaySocketsAsync(birthdayTarget, connectionGuid, ownPeer, remotePeer,
                        ownPrivateKey, passwordHash, punchCts.Token) : null;
                (UdpClient? Socket, IPEndPoint? Endpoint) nominated = (null, null);
                try
                {
                    var pending = new List<Task<(UdpClient? Socket, IPEndPoint? Endpoint)>> { primary };
                    if (birthday != null) pending.Add(birthday);
                    while (pending.Count > 0)
                    {
                        var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                        pending.Remove(completed);
                        var result = await completed.ConfigureAwait(false);
                        if (result.Endpoint == null) continue;
                        nominated = result;
                        break;
                    }
                }
                finally
                {
                    punchCts.Cancel();
                    await sender.ConfigureAwait(false);
                    await primary.ConfigureAwait(false);
                    if (birthday != null)
                    {
                        var extra = await birthday.ConfigureAwait(false);
                        if (extra.Socket != null && !ReferenceEquals(extra.Socket, nominated.Socket)) extra.Socket.Dispose();
                    }
                }
                var remoteEndpoint = nominated.Endpoint;
                async Task<(UdpClient? Socket, IPEndPoint? Endpoint)> ReceivePrimary() =>
                    (nudp, await ReceiveHoleLoopAsync(nudp, connectionGuid, ownPeer, remotePeer, ownPrivateKey, passwordHash, punchCts.Token).ConfigureAwait(false));

                if (remoteEndpoint == null)
                {
                    QuicPunchLog.Info($"[HOLE PUNCH] Failed to hole-punch with {remotePeer.Name ?? "Peer"}: no candidate response received within timeout.");
                    return (false, null, null);
                }

                QuicPunchLog.Info($"[HOLE PUNCH] Succeeded! Nominated peer-reflexive endpoint: {remoteEndpoint}");
                if (!ReferenceEquals(nominated.Socket, nudp)) nudp.Dispose();
                return (true, nominated.Socket, remoteEndpoint);
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
            ushort peerPort, Guid connectionGuid, X509Certificate2 ownCertificate, CancellationToken mainCt)
        {
            if (!OperatingSystem.IsAndroid()) MsQuicTuner.EnsureOptimalConfiguration();

            using var openPortCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var openPortLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(mainCt, openPortCts.Token);

            var udpResult = await OpenPortCore(ownPeer, nudp, remotePeer, remoteCandidates, peerPort, connectionGuid, openPortLinkedCts.Token, qc?.CertManager?.Curve, qc?.PasswordHash)
                .WaitAsync(openPortLinkedCts.Token);

            if (!udpResult.Success || udpResult.remoteEndpoint == null)
            {
                try { nudp.Dispose(); } catch { }
                return (null!, null!);
            }

            nudp = udpResult.client!;
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
                            // QUIC_READY can be lost during UDP rebinding. The listener is
                            // normally up within milliseconds; retrying a real connection is
                            // more useful than waiting most of the handshake budget here.
                            await qc.WaitForQuicReadyAsync(connectionGuid, remotePeer.Id, TimeSpan.FromSeconds(2), linkedCts.Token).ConfigureAwait(false);
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
                    QuicPunchLog.Info($"[SUCCESS] QUIC connection established with {remotePeer.Name ?? "Peer"} ({udpResult.remoteEndpoint})! Path MTU: {connection.PathMtu} bytes");

                    UpdateQuicEndPoint(remotePeer, udpResult.remoteEndpoint);
                    // The QUIC socket has a different port from the control socket.
                    if (connection.DatagramChannel != null)
                    {
                        connection.DatagramChannel.OnPeerAddressChanged += newEp =>
                        {
                            QuicPunchLog.Info($"[CONNECTION MIGRATION] Peer {remotePeer.Name} shifted to new endpoint: {newEp}");
                            UpdateQuicEndPoint(remotePeer, newEp);
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
                if (OperatingSystem.IsAndroid() && ex.GetBaseException() is DllNotFoundException missingLibrary)
                    Volatile.Write(ref _androidQuicUnavailableReason,
                        $"Android native MsQuic transport could not load: {missingLibrary.Message}");
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

                QuicPunchLog.Info(mainCt.IsCancellationRequested
                    ? $"[QUIC] Connection attempt with {remotePeer.Name ?? "Peer"} canceled."
                    : $"[QUIC] Could not establish connection with {remotePeer.Name ?? "Peer"}.");
                return (null!, null!);
            }

            return (connection, stream);
        }

        internal static void UpdateQuicEndPoint(PeerInfo peer, IPEndPoint endpoint) =>
            peer.QuicEndPoint = new IPEndPoint(endpoint.Address, endpoint.Port);

        public static async Task<IPEndPoint?> ReceiveHoleLoopAsync(
            UdpClient udp, Guid connectionGuid, PeerInfo ownPeer, PeerInfo remotePeer,
            ECDsa? ownPrivateKey, byte[]? passwordHash, CancellationToken token)
        {
            if (ownPrivateKey == null) return null;
            var challenges = new Dictionary<IPEndPoint, Guid>();

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

                        if (!ConnectivityProbe.TryVerify(receiveBuffer.AsSpan(0, bytesRead), connectionGuid,
                            ownPeer, remotePeer, passwordHash, out var challenge, out var echo))
                            continue;

                        var senderEp = EndpointAddressCodec.Normalize((IPEndPoint)result.RemoteEndPoint);
                        if (!challenges.TryGetValue(senderEp, out var expectedEcho))
                        {
                            // Bound memory even if an authenticated peer advertises many paths.
                            if (challenges.Count >= 64) continue;
                            expectedEcho = Guid.NewGuid();
                            challenges.Add(senderEp, expectedEcho);
                        }
                        bool verifiedReturnPath = echo == expectedEcho;
                        byte[] reply = ConnectivityProbe.Create(connectionGuid, expectedEcho, challenge,
                            ownPeer, remotePeer, ownPrivateKey, passwordHash);
                        for (int attempt = 0; attempt < (verifiedReturnPath ? 4 : 1); attempt++)
                            await udp.SendAsync(reply, senderEp, token).ConfigureAwait(false);
                        if (verifiedReturnPath)
                            return senderEp;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
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
                if (ownPrivateKey == null) return;

                var schedule = new PortProbeSchedule();

                int tries = 0;
                while (!token.IsCancellationRequested)
                {
                    tries++;
                    schedule.NextRound();
                    IReadOnlyList<IPEndPoint> targetEndPoints = BuildProbeTargets(peer, remoteCandidates, askedPort,
                        maxEndpoints: 16, schedule: schedule);
                    if (tries == 1) QuicPunchLog.Info($"[HOLE PUNCH] Probing {targetEndPoints.Count} endpoint(s); dynamic public ports rotate per round.");
                    byte[] probe = ConnectivityProbe.Create(connectionGuid, Guid.NewGuid(), Guid.Empty,
                        ownPeer, peer, ownPrivateKey, passwordHash);
                    foreach (var ep in targetEndPoints)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            await udp.SendAsync(probe, ep, token).ConfigureAwait(false);
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

        internal static IReadOnlyList<IPEndPoint> BuildProbeTargets(
            PeerInfo peer,
            IReadOnlyList<CandidateEndpoint>? remoteCandidates,
            ushort askedPort,
            int maxEndpoints = PortAdvertisementCodec.MaxProbeEndpoints,
            PortProbeSchedule? schedule = null)
        {
            ArgumentNullException.ThrowIfNull(peer);
            if (maxEndpoints <= 0)
                return Array.Empty<IPEndPoint>();

            var targets = new List<IPEndPoint>(maxEndpoints);
            var seen = new HashSet<IPEndPoint>();

            void Add(IPAddress? address, int port)
            {
                if (targets.Count >= maxEndpoints || address == null || port is < 1 or > 65535 || !EndpointAddressCodec.CanAdvertise(address))
                    return;

                var endpoint = new IPEndPoint(address, port);
                if (seen.Add(endpoint))
                    targets.Add(endpoint);
            }

            bool HasHostCandidate(IPAddress address) => remoteCandidates?.Any(candidate =>
                candidate.Type == CandidateType.Host && candidate.EndPoint.Address.Equals(address)) == true;

            if (peer.ActiveEndPoint is { } active)
            {
                if (askedPort == 0) Add(active.Address, active.Port);
                else if (!Utilities.IsPrivateLanAddress(active.Address) || !HasHostCandidate(active.Address))
                    Add(active.Address, askedPort);
            }

            if (remoteCandidates != null)
            {
                foreach (CandidateEndpoint candidate in remoteCandidates)
                {
                    if (targets.Count >= maxEndpoints) break;
                    Add(candidate.EndPoint?.Address, candidate.EndPoint?.Port ?? 0);
                }
            }

            // A mobile network or destination-dependent NAT may expose a different
            // public IP to the authenticated control peer than it does to STUN.
            // Try tunnel ports on that current address as well as the advertised one.
            if (peer.ActiveEndPoint is { } observed && !Utilities.IsPrivateLanAddress(observed.Address) && !IPAddress.IsLoopback(observed.Address) &&
                remoteCandidates != null)
                foreach (var candidate in remoteCandidates.Where(c => c.Type == CandidateType.ServerReflexive &&
                    c.EndPoint.Address.AddressFamily == observed.Address.AddressFamily &&
                    !Utilities.IsPrivateLanAddress(c.EndPoint.Address) && !IPAddress.IsLoopback(c.EndPoint.Address)))
                    Add(observed.Address, candidate.EndPoint.Port);

            IPAddress[] addresses = (peer.Addresses ?? Array.Empty<IPAddress>())
                .Prepend(peer.ActiveEndPoint?.Address ?? IPAddress.None)
                .Where(Utilities.IsValidPeerAddress)
                .Distinct()
                .OrderByDescending(Utilities.IsPrivateLanAddress)
                .Take(32)
                .ToArray();

            // Host candidates belong to their IP. A NAT's observed public ports
            // must not be sprayed across a LAN IP with a known local port.
            bool HasScopedHostPort(IPAddress address) => (Utilities.IsPrivateLanAddress(address) || address.AddressFamily == AddressFamily.InterNetworkV6) &&
                (askedPort == 0 && peer.PreferredPort > 0 || HasHostCandidate(address));

            foreach (IPAddress address in addresses)
            {
                if (HasScopedHostPort(address))
                {
                    if (askedPort == 0 && peer.PreferredPort > 0) Add(address, peer.PreferredPort);
                    continue;
                }
                if (askedPort > 0) Add(address, askedPort);
                if (askedPort == 0 && peer.ActiveEndPoint is { Port: > 0 } activeEndpoint && activeEndpoint.Address.Equals(address))
                    Add(address, activeEndpoint.Port);
            }

            IPAddress[] wanProbeAddresses = addresses.Where(address => !HasScopedHostPort(address)).ToArray();
            ushort[] advertisedPorts = PortAdvertisementCodec.Normalize(peer.PortArray);
            int remaining = Math.Max(0, maxEndpoints - targets.Count);
            int desiredPortCount = wanProbeAddresses.Length == 0 ? 0 : Math.Max(1, remaining / wanProbeAddresses.Length);
            ushort[] selectedPorts = SelectProbePorts(
                advertisedPorts,
                peer.ConnectionFlags.PortMode,
                schedule != null && peer.ConnectionFlags.PortMode == PortMode.Range && schedule.Round >= 4
                    ? Math.Max(1, desiredPortCount / 2) : desiredPortCount, schedule);

            // Port-major order distributes a bounded budget across all addresses
            // instead of exhausting it on the first address.
            foreach (ushort port in selectedPorts)
            {
                foreach (IPAddress address in wanProbeAddresses)
                {
                    if (targets.Count >= maxEndpoints)
                        return PrioritizeLocalSubnet(targets);
                    Add(address, port);
                }
            }

            // A single STUN port can still be translated by NAT. Search around
            // the tunnel's public candidate, not around its unrelated control
            // port or a private host candidate. Existing lists/ranges keep their
            // original traversal below, and exact Single peers never expand.
            if (schedule != null && HasTranslatedSinglePort(peer))
            {
                bool Public(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork &&
                    !Utilities.IsPrivateLanAddress(address) && !IPAddress.IsLoopback(address) && Utilities.IsValidPeerAddress(address);
                var reflexiveCandidates = remoteCandidates?.Where(c => c.Type == CandidateType.ServerReflexive && Public(c.EndPoint.Address))
                    .OrderByDescending(c => c.Priority).ToArray() ?? [];
                var publicAddresses = wanProbeAddresses.Concat(reflexiveCandidates.Select(c => c.EndPoint.Address))
                    .Where(Public).Distinct().Take(32).ToArray();
                int count = publicAddresses.Length == 0 ? 0 : (maxEndpoints - targets.Count) / publicAddresses.Length;
                foreach (var address in publicAddresses)
                {
                    int port = (reflexiveCandidates.FirstOrDefault(c => c.EndPoint.Address.Equals(address)) ??
                        reflexiveCandidates.FirstOrDefault())?.EndPoint.Port ?? (askedPort > 0 ? askedPort : advertisedPorts.FirstOrDefault());
                    if (port == 0) continue;
                    var window = TranslatedSinglePortWindow(port, schedule.Round);
                    foreach (ushort guess in schedule.Take(window.Minimum, window.Maximum, count)) Add(address, guess);
                }
            }

            // STUN lists/range bounds are observations, not guarantees for a new
            // destination-dependent mapping. After exact candidates, also rotate
            // through the remaining public port space. Never expand LAN targets.
            if (schedule is { Round: >= 4 } && peer.ConnectionFlags.PortMode != PortMode.Single)
            {
                IPAddress[] publicAddresses = wanProbeAddresses.Where(address =>
                    !Utilities.IsPrivateLanAddress(address) && !IPAddress.IsLoopback(address) &&
                    address.AddressFamily == AddressFamily.InterNetwork).ToArray();
                int count = publicAddresses.Length == 0 ? 0 : (maxEndpoints - targets.Count) / publicAddresses.Length;
                foreach (ushort port in schedule.Take(1024, 65535, count))
                    foreach (IPAddress address in publicAddresses) Add(address, port);
            }
            return PrioritizeLocalSubnet(targets);
        }

        private static IReadOnlyList<IPEndPoint> PrioritizeLocalSubnet(List<IPEndPoint> targets)
        {
            try
            {
                var localSubnets = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(network => network.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                                      address.IPv4Mask != null)
                    .Select(address => (Address: address.Address.GetAddressBytes(), Mask: address.IPv4Mask.GetAddressBytes()))
                    .ToArray();

                bool SameSubnet(IPAddress remote)
                {
                    if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
                    byte[] bytes = remote.GetAddressBytes();
                    return localSubnets.Any(local => Enumerable.Range(0, 4)
                        .All(index => (local.Address[index] & local.Mask[index]) == (bytes[index] & local.Mask[index])));
                }

                return targets.OrderByDescending(target => IPAddress.IsLoopback(target.Address))
                    .ThenByDescending(target => SameSubnet(target.Address)).ToArray();
            }
            catch (Exception) { return targets; }
        }

        private static ushort[] SelectProbePorts(ushort[] ports, PortMode mode, int maxPorts, PortProbeSchedule? schedule = null)
        {
            if (maxPorts <= 0 || ports.Length == 0)
                return Array.Empty<ushort>();

            if (mode == PortMode.Range && ports.Length >= 2)
            {
                int minPort = ports[0];
                int maxPort = ports[^1];
                if (schedule != null)
                {
                    if (minPort == maxPort || maxPorts == 1) return [(ushort)minPort];
                    var portsToProbe = new List<ushort>(maxPorts) { (ushort)minPort, (ushort)maxPort };
                    // Bounds came from real observations: retry them alongside new
                    // guesses instead of replacing all known ports with random ones.
                    foreach (ushort port in schedule.Take(minPort, maxPort, maxPorts))
                    {
                        if (portsToProbe.Count >= maxPorts) break;
                        if (!portsToProbe.Contains(port)) portsToProbe.Add(port);
                    }
                    return portsToProbe.ToArray();
                }
                if (minPort == maxPort || maxPorts == 1)
                    return [(ushort)minPort];
                if (maxPorts == 2)
                    return [(ushort)minPort, (ushort)maxPort];

                var candidates = new List<ushort>(maxPorts)
                {
                    (ushort)minPort,
                    (ushort)maxPort,
                    (ushort)(minPort + ((maxPort - minPort) / 2))
                };
                for (int slot = 1; candidates.Count < maxPorts && slot < maxPorts - 1; slot++)
                {
                    int port = minPort + (int)Math.Round((double)slot * (maxPort - minPort) / (maxPorts - 1));
                    ushort candidate = (ushort)port;
                    if (!candidates.Contains(candidate))
                        candidates.Add(candidate);
                }
                return candidates.Take(maxPorts).ToArray();
            }

            if (ports.Length <= maxPorts)
                return ports;
            if (maxPorts == 1)
                return [ports[ports.Length / 2]];
            if (maxPorts == 2)
                return [ports[0], ports[^1]];

            var indexes = new SortedSet<int> { 0, ports.Length - 1, ports.Length / 2 };

            for (int delta = 1; delta <= 8 && indexes.Count < maxPorts; delta++)
            {
                if (delta < ports.Length) indexes.Add(delta);
                int upper = ports.Length - 1 - delta;
                if (upper >= 0) indexes.Add(upper);
            }

            for (int slot = 1; indexes.Count < maxPorts && slot < maxPorts; slot++)
            {
                int index = (int)Math.Round((double)slot * (ports.Length - 1) / Math.Max(1, maxPorts - 1));
                indexes.Add(index);
            }

            return indexes.Take(maxPorts).Select(index => ports[index]).ToArray();
        }

        public const string DefaultDisguiseHost = "cloudflare-quic.com";
        public static readonly List<SslApplicationProtocol> SupportedProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("h3")           // RFC 9114 HTTP/3 standard ALPN (looks like normal web traffic to DPI)
        };

        private static async Task<(QuicConnection?, QuicStream?)> TryRunBridgeServer(
            int localPort, X509Certificate2 ownCertificate, byte[] peerCertHash,
            UdpClient? holePunchUdp, CancellationToken token, Func<Task>? onListening)
        {
            MsQuicBridgeTransport? native = null;
            try
            {
                native = MsQuicBridgeTransport.Create(ownCertificate, peerCertHash);
                // A probe socket must release the port before MsQuic owns it.
                holePunchUdp?.Dispose();
                holePunchUdp = null;
                native.Listen((ushort)localPort);
                if (onListening != null) await onListening().ConfigureAwait(false);
                await native.WaitConnectedAsync(token).ConfigureAwait(false);
                var connection = QuicConnection.FromTransport(native);
                var stream = await connection.AcceptInboundStreamAsync(token).ConfigureAwait(false);
                await stream.ReadExactlyAsync(new byte[1], token).ConfigureAwait(false);
                return (connection, stream);
            }
            catch
            {
                if (native != null) await native.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            finally { holePunchUdp?.Dispose(); }
        }

        private static async Task<(QuicConnection?, QuicStream?)> TryRunBridgeClient(
            IPEndPoint targetPeer, X509Certificate2 ownCertificate, byte[] peerCertHash,
            int localPort, UdpClient? holePunchUdp, CancellationToken token, string targetHost = DefaultDisguiseHost)
        {
            int delayMs = 50;
            holePunchUdp?.Dispose();
            while (!token.IsCancellationRequested)
            {
                MsQuicBridgeTransport? native = null;
                try
                {
                    native = MsQuicBridgeTransport.Create(ownCertificate, peerCertHash, targetPeer, targetHost);
                    native.Connect(targetPeer, (ushort)localPort);
                    await native.WaitConnectedAsync(token).ConfigureAwait(false);
                    var connection = QuicConnection.FromTransport(native);
                    var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
                    await stream.WriteAsync(new byte[] { 0 }, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    return (connection, stream);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (native != null) await native.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    if (native != null) await native.DisposeAsync().ConfigureAwait(false);
                    QuicPunchLog.Info($"[QUIC] Connect to {targetPeer}: {ex.Message}; retrying in {delayMs}ms.");
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                    delayMs = Math.Min(delayMs * 2, 400);
                }
            }
            return (null, null);
        }
        public static Task<(QuicConnection? Connection, QuicStream? Stream)> TryRunServer(
            int localPort, X509Certificate2 ownCertificate, byte[] peerCertificate, UdpClient? holePunchUdp, CancellationToken token, Func<Task>? onListening = null) =>
            TryRunBridgeServer(localPort, ownCertificate, peerCertificate, holePunchUdp, token, onListening);

        public static Task<(QuicConnection? Connection, QuicStream? Stream)> TryRunClient(
            IPEndPoint targetPeer, X509Certificate2 ownCertificate, byte[] peerCertificate, int localPort, UdpClient? holePunchUdp, CancellationToken token, string targetHost = DefaultDisguiseHost) =>
            TryRunBridgeClient(targetPeer, ownCertificate, peerCertificate, localPort, holePunchUdp, token, targetHost);

        private static bool ValidateRemotePeerCertificate(X509Certificate? certificate, byte[]? peerCertificate, string role)
        {
            if (certificate == null || peerCertificate == null)
            {
                QuicPunchLog.Info($"[{role} TLS] Validation rejected: certificate is null ({certificate == null}), peerCertificate is null ({peerCertificate == null})");
                return false;
            }

            byte[] publicKey = certificate.GetPublicKey();
            byte[] hash = Sha3Crypto.Hash256(publicKey);

            var valid = CryptographicOperations.FixedTimeEquals(hash, peerCertificate);
            QuicPunchLog.Info($"{role} cert hash: " + Convert.ToHexString(hash) + " valid: " + valid);
            return valid;
        }

        public static async Task<string> RunAndroidNativeSelfTestAsync(X509Certificate2 identity, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsAndroid()) throw new PlatformNotSupportedException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            int port = Random.Shared.Next(40000, 50000);
            byte[] identityHash = Sha3Crypto.Hash256(identity.GetPublicKey());
            await using var server = MsQuicBridgeTransport.Create(identity, identityHash);
            server.Listen((ushort)port);
            await using var client = MsQuicBridgeTransport.Create(identity, identityHash,
                new IPEndPoint(IPAddress.Loopback, port));
            client.Connect(new IPEndPoint(IPAddress.Loopback, port), 0);
            await Task.WhenAll(server.WaitConnectedAsync(timeout.Token), client.WaitConnectedAsync(timeout.Token)).ConfigureAwait(false);
            await using var outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, timeout.Token).ConfigureAwait(false);
            await outbound.Stream.WriteAsync(new byte[] { 0x51, 0x50 }, timeout.Token).ConfigureAwait(false);
            await using var inbound = await server.AcceptInboundStreamAsync(timeout.Token).ConfigureAwait(false);
            byte[] received = new byte[2];
            await inbound.Stream.ReadExactlyAsync(received, timeout.Token).ConfigureAwait(false);
            if (received[0] != 0x51 || received[1] != 0x50) throw new IOException("Android MsQuic stream integrity check failed.");
            if (client.NegotiatedTlsGroup != MsQuicPqcHandshake.X25519MlKem768 ||
                server.NegotiatedTlsGroup != MsQuicPqcHandshake.X25519MlKem768)
                throw new IOException($"Android MsQuic PQC check failed: client group {client.NegotiatedTlsGroup}, server group {server.NegotiatedTlsGroup}.");
            return "Native MsQuic TLS and stream loopback passed; TLS group: X25519MLKEM768 (4588)";
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
