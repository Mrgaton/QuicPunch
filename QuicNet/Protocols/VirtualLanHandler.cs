using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;
using QuicPunch.Vpn;
using QuicNet.Vpn;
using QuicPunch.Vpn.Routing;
using QuicPunch.Vpn.Broker;

namespace QuicNet.Protocols;

internal sealed class VirtualLanHandler : IVirtualLanHandler, QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public string ProtocolName => "LAN Bridge";
    public ushort StreamPriority => (ushort)QuicPunch.Helpers.QuicStreamPriority.High;

    private IPAddress _localIp = IPAddress.Parse("10.0.0.2");
    private uint _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse("10.0.0.2").GetAddressBytes());
    private uint _directedBroadcast = 0x0A0000FF;
    private bool _hasDirectedBroadcast = true;
    private long _configurationRevision = -1;
    public const int DefaultDatagramMtu = 1420; // Inner IP MTU; negotiated datagram fragmentation handles smaller QUIC paths.
    private string _subnetMask = "255.255.255.0";
    private int _mtu = DefaultDatagramMtu;

    public string AdapterName { get; } = OperatingSystem.IsWindows() ? "QuicPunchAdapter" : "qp-tun0";
    public string AdapterStatus { get; private set; } = "Stopped";
    public string? LastError { get; private set; }
    public IPAddress LocalIp => _localIp;
    public string SubnetMask => _subnetMask;
    public int Mtu => _mtu;
    public bool AutoAssignEnabled { get; private set; } = true;
    public bool IsActive => _session?.IsHealthy == true && _captureTask?.IsCompleted != true;
    public bool CanStartWithoutElevation => IsActive || BrokerVpnAdapter.IsAvailable || QuicPunch.Vpn.Linux.LinuxVpnAdapter.HasNetworkAdministration;
    public bool CanStartSession => _adapterFactory != null || CanStartWithoutElevation || QuicNet.WebUi.Modules.LanApiModule.IsRunningAsAdmin();
    public string AddressMode => AutoAssignEnabled ? "Automatic" : "Manual";
    public IVpnControl? Control { get; private set; }
    public bool UsesOwnSessionPermissions => Control != null;
    private VpnPolicy? _policy;
    private readonly VpnReplyTracker _replies = new();
    private readonly object _lifecycle = new();
    private VpnRouteManager? _routes;
    private VpnForwardingManager? _forwarding;
    private bool _forwardingEnabled;
    private string[] _inboundPrefixes = [];
    private bool _broadcastEnabled;
    private string[] _routePrefixes = [];
    private string[] _transportEndpoints = [];
    private bool _transportEndpointsDirty;
    private HashSet<uint> _hostAddresses = [];
    internal void SetTransportEndpoints(string[] addresses)
    {
        _transportEndpointsDirty = !_transportEndpoints.SequenceEqual(addresses);
        _transportEndpoints = addresses;
    }
    private readonly Func<IVpnAdapter>? _adapterFactory;
    internal VirtualLanHandler(Func<IVpnAdapter>? adapterFactory = null) => _adapterFactory = adapterFactory;
    internal void AttachControl(IVpnControl control) => Control = control;
    internal void PublishPolicy(VpnPolicy policy)
    {
        Volatile.Write(ref _hostAddresses, System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => Ipv4Prefix.Address(a.Address.ToString())).ToHashSet());
        Volatile.Write(ref _policy, policy);
        foreach (var session in ActivePeers.Values)
            if (policy.Peer(session.Identity) is not { } p || p.Paused || p.Address != session.RemoteIpUint) DisconnectPeer(session.Peer.Id);
    }
    internal void ApplyConfiguration(VpnConfiguration config, VpnPolicy policy)
    {
        lock (_lifecycle)
        {
            var connected = Ipv4Prefix.Parse(Ipv4Prefix.Format(policy.LocalAddress) + "/" + Ipv4Prefix.MaskLength(config.SubnetMask));
            string[] prefixes = policy.Peers.SelectMany(p => p.Routes).Where(p => !connected.Contains(p))
                .Select(p => p.ToString()).Distinct().Order().ToArray();
            bool forwarding = policy.Peers.Any(p => p.Forwarding);
            string[] inbound = policy.Peers.SelectMany(p => p.Destinations)
                .Append(new Ipv4Prefix(policy.LocalAddress, 32)).Select(p => p.ToString()).Distinct().Order().ToArray();
            bool broadcast = policy.Peers.Any(p => p.Broadcast);
            bool restart = (!config.Enabled && (_adapter != null || !ActivePeers.IsEmpty)) || policy.LocalAddress != _localIpUint || config.SubnetMask != _subnetMask ||
                config.Mtu != _mtu || !_routePrefixes.SequenceEqual(prefixes) || forwarding != _forwardingEnabled ||
                !_inboundPrefixes.SequenceEqual(inbound) || _broadcastEnabled != broadcast || _transportEndpointsDirty;
            if (restart) StopTun();
            _transportEndpointsDirty = false;
            bool changed = restart || _configurationRevision != config.Revision;
            var previous = Volatile.Read(ref _policy);
            foreach (var session in ActivePeers.Values)
            {
                var old = previous?.Peer(session.Identity); var next = policy.Peer(session.Identity);
                if (next == null || old == null || old.Address != next.Address || old.Broadcast != next.Broadcast || old.Forwarding != next.Forwarding ||
                    !old.Sources.SequenceEqual(next.Sources) || !old.Destinations.SequenceEqual(next.Destinations) || !old.Routes.SequenceEqual(next.Routes))
                { DisconnectPeer(session.Peer.Id); changed = true; }
            }
            AutoAssignEnabled = config.AutoAssign;
            SetAddressFields(IPAddress.Parse(Ipv4Prefix.Format(policy.LocalAddress)), config.SubnetMask);
            _mtu = config.Mtu;
            _routePrefixes = prefixes;
            _forwardingEnabled = forwarding;
            _inboundPrefixes = inbound;
            _broadcastEnabled = broadcast;
            _configurationRevision = config.Revision;
            if (changed) _replies.Clear();
            PublishPolicy(policy);
            if (changed) StateChanged?.Invoke();
        }
    }
    public bool? IncomingSessionPolicy(PeerInfo peer)
    {
        if (Control == null) return null;
        if (!Control.Configuration.Enabled) return false;
        var policy = Volatile.Read(ref _policy)?.Peer(Convert.ToHexString(peer.CertHash));
        return policy == null || policy.Paused ? false : policy.AutoAccept ? true : null;
    }

    public bool SupportsSessionRecovery => true;
    internal const byte VpnDatagramCategory = 9;

    public void OnFrameReceived(QuicConnection connection, PeerInfo peer, byte category, ReadOnlyMemory<byte> payload, bool isKeyFrame)
    {
        if (category != VpnDatagramCategory || payload.Length > 9000) return;
        var session = ActivePeers.Values.FirstOrDefault(s => ReferenceEquals(s.Connection, connection) && s.Peer.Id == peer.Id);
        if (session?.SupportsFragmentedDatagrams == true) ProcessIncomingPacket(session, payload.Span);
    }

    public event Action? StateChanged;

    public long TotalRxPackets;
    public long TotalTxPackets;
    public long TotalRxBytes;
    public long TotalTxBytes;

    long IVirtualLanHandler.TotalRxPackets => TotalRxPackets;
    long IVirtualLanHandler.TotalTxPackets => TotalTxPackets;
    long IVirtualLanHandler.TotalRxBytes => TotalRxBytes;
    long IVirtualLanHandler.TotalTxBytes => TotalTxBytes;

    public bool HasActivePeer(Guid peerId) => ActivePeers.Values.Any(p => p.Peer.Id == peerId);
    public int ActivePeerCount => ActivePeers.Count;
    public IEnumerable<object> GetActivePeerSummaries() => ActivePeers.Values.Select(item => (object)
        System.Text.Json.JsonSerializer.SerializeToElement(new QuicNet.Serialization.LanPeerSummaryResponse(
            item.Peer.Id.ToString(), item.Peer.Name, item.Peer.Name, item.RemoteIp.ToString(), item.RemoteIp.ToString(),
            item.Connection.IsTor ? "Tor/stream" : "Datagram", item.Stream != null ? "Connected" : "Negotiating", item.Peer.Ping, item.TxPackets, item.RxPackets,
            item.TxBytes, item.RxBytes, item.DroppedPackets, item.ConnectedAt.ToString("HH:mm:ss"), item.ConnectedAt.ToString("o")),
            QuicNet.Serialization.NativeUiJsonContext.Default.LanPeerSummaryResponse));

    public void Stop() => StopTun();

    public ConcurrentDictionary<uint, PeerLanSession> ActivePeers { get; } = new();

    private IVpnAdapter? _adapter;
    private IVpnSession? _session;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    public static IPAddress ComputeAutomaticIp(byte[] identityHash, byte[]? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(identityHash);
        if (identityHash.Length == 0)
            throw new ArgumentException("Identity hash cannot be empty.", nameof(identityHash));

        byte[] input = new byte[identityHash.Length + (groupId?.Length ?? 0)];
        identityHash.CopyTo(input, 0);
        groupId?.CopyTo(input, identityHash.Length);
        byte[] hostDigest = SHA256.HashData(input);
        // One memorable /24 LAN, with 250 host slots; collisions still require identity-bound validation.
        return new IPAddress(new byte[] { 10, 0, 0, (byte)(2 + BinaryPrimitives.ReadUInt32BigEndian(hostDigest) % 250) });
    }

    public void ConfigureAutoAssignment(byte[] identityHash, byte[]? groupId = null)
    {
        AutoAssignEnabled = true;
        SetAddressFields(ComputeAutomaticIp(identityHash, groupId), "255.255.255.0");
    }

    public bool ConfigureManualAddress(string ip, string subnetMask)
    {
        if (!TryParseIpv4(ip, out var parsed) || !TryParseIpv4(subnetMask, out _))
            return false;
        try { Ipv4Prefix.MaskLength(subnetMask); } catch (InvalidDataException) { return false; }

        AutoAssignEnabled = false;
        SetAddressFields(parsed, subnetMask);
        return true;
    }

    public bool SetMtu(int mtu)
    {
        if (mtu < 1200 || mtu > 9000)
            return false;
        _mtu = mtu;
        if (mtu > 1500)
        {
            QuicPunch.Helpers.MsQuicTuner.EnsurePathMtuDiscovery(1280, (ushort)Math.Min(mtu, 9000));
        }
        if (_adapter != null)
        {
            try { _adapter.SetMtu(_mtu); }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }
        return true;
    }

    public void SetupTun(string? initialIp = null, string? subnetMask = null, int? mtu = null)
    {
        lock (_lifecycle) SetupTunCore(initialIp, subnetMask, mtu);
    }
    private void SetupTunCore(string? initialIp, string? subnetMask, int? mtu)
    {
        if (!string.IsNullOrWhiteSpace(initialIp) && TryParseIpv4(initialIp, out var parsed))
            SetAddressFields(parsed, string.IsNullOrWhiteSpace(subnetMask) ? _subnetMask : subnetMask!);
        else if (!string.IsNullOrWhiteSpace(subnetMask))
            _subnetMask = subnetMask!;
        if (mtu.HasValue && !SetMtu(mtu.Value))
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU must be between 1200 and 9000.");

        StopTun();
        try
        {
            Console.WriteLine($"[LAN] Starting VPN adapter ({AdapterName}) at {_localIp}/{_subnetMask}, MTU {_mtu}...");
            _adapter = _adapterFactory?.Invoke() ?? (BrokerVpnAdapter.IsAvailable ? new BrokerVpnAdapter() : VpnAdapterFactory.Create(AdapterName, "QuicPunchTunnel"));
            if (_adapter is IVpnRouteAdapter broker) broker.SetTransportEndpoints(_transportEndpoints);
            else
            {
                _routes = new VpnRouteManager(AdapterName);
                _routes.ProtectTransportEndpoints(_transportEndpoints);
            }
            _adapter.SetIpAddress(_localIp, _subnetMask);
            _adapter.SetMtu(_mtu);
            // Connected subnet routes are already created with the interface address.
            var connected = Ipv4Prefix.Parse(_localIp + "/" + Ipv4Prefix.MaskLength(_subnetMask));
            string[] routes = _routePrefixes.Where(r => !connected.Contains(Ipv4Prefix.Parse(r))).ToArray();
            if (_adapter is IVpnRouteAdapter remote) { remote.SetRoutes(routes); remote.SetForwarding(_forwardingEnabled); remote.SetAccess(_inboundPrefixes, _broadcastEnabled); }
            else _routes!.Install(routes);
            if (_adapter is not IVpnRouteAdapter) _forwarding = new VpnForwardingManager(AdapterName, _forwardingEnabled);
            _session = _adapter.StartSession();
            AdapterStatus = "Active";
            LastError = null;

            _captureCts = new CancellationTokenSource();
            IVpnSession captureSession = _session;
            var captureToken = _captureCts.Token;
            _captureTask = Task.Run(() => CaptureVpnAndSendToPeers(captureSession, captureToken));
        }
        catch (Win32Exception ex)
        {
            LastError = ex.Message;
            bool isPrivilege = (OperatingSystem.IsWindows() && ex.NativeErrorCode == 5) ||
                               (OperatingSystem.IsLinux() && (ex.NativeErrorCode == 1 || ex.NativeErrorCode == 13));
            AdapterStatus = isPrivilege ? "Administrator / root privileges required" : $"Error: {ex.Message}";
            CleanupAdapter();
            Console.WriteLine($"[LAN] VPN initialization failed: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AdapterStatus = $"Error: {ex.Message}";
            CleanupAdapter();
            Console.WriteLine($"[LAN] VPN initialization failed: {ex.Message}");
            throw;
        }
        finally
        {
            StateChanged?.Invoke();
        }
    }

    public void NotifyPrivilegeRequired()
    {
        LastError = "Se requiere el servicio VPN, permisos de administrador en Windows o CAP_NET_ADMIN en Linux para crear el adaptador virtual.";
        AdapterStatus = "Administrator / root privileges required";
        StateChanged?.Invoke();
    }

    public void StopTun()
    {
        lock (_lifecycle) StopTunCore();
    }
    private void StopTunCore()
    {
        try { _captureCts?.Cancel(); } catch { }
        try { _captureTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _captureTask = null;
        try { _captureCts?.Dispose(); } catch { }
        _captureCts = null;

        foreach (var session in ActivePeers.Values)
            session.Dispose();
        ActivePeers.Clear();

        CleanupAdapter();
        if (AdapterStatus != "Error" && !AdapterStatus.Contains("privileges", StringComparison.OrdinalIgnoreCase))
            AdapterStatus = "Stopped";

        StateChanged?.Invoke();
    }

    private void CleanupAdapter()
    {
        _routes?.Dispose();
        _routes = null;
        _forwarding?.Dispose();
        _forwarding = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        try { _adapter?.Dispose(); } catch { }
        _adapter = null;
    }

    public bool SetVirtualIp(string newIpStr, string subnetMask = "255.255.255.0")
    {
        if (!ConfigureManualAddress(newIpStr, subnetMask))
            return false;
        if (_adapter == null)
            return true;

        try
        {
            _adapter.SetIpAddress(_localIp, _subnetMask);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
    {
        Console.WriteLine($"[LAN] Connection with {peer.Name} was denied or failed.");
        return Task.CompletedTask;
    }

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.ApplyOptimalTuning();
        QuicPunch.Helpers.MsQuicTuner.TrySetStreamPriority(stream, QuicPunch.Helpers.QuicStreamPriority.High);

        if (Control != null && (!Control.Configuration.Enabled || Volatile.Read(ref _policy)?.Peer(Convert.ToHexString(peer.CertHash)) is not { Paused: false }))
            throw new InvalidDataException("VPN disabled or peer has no authorized VPN address.");
        if (Control != null && !IsActive)
            throw new InvalidOperationException(Control.ConfigurationError ?? "VPN adapter is waiting for orchestration.");
        if (_session == null && (Control?.Configuration.Enabled ?? AutoAssignEnabled))
        {
            try
            {
                Console.WriteLine($"[LAN] Auto-activating VPN adapter on incoming session from {peer.Name}...");
                SetupTun();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LAN] On-demand VPN activation failed: {ex.Message}");
            }
        }

        uint remoteIpUint = 0;
        PeerLanSession? peerSession = null;
        Action<byte[]>? datagramHandler = null;
        try
        {
            byte[] localIpBytes = _localIp.GetAddressBytes();
            await stream.WriteAsync(localIpBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            byte[] remoteIpBytes = new byte[4];
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await stream.ReadExactlyAsync(remoteIpBytes, handshake.Token).ConfigureAwait(false);
            }
            remoteIpUint = BinaryPrimitives.ReadUInt32BigEndian(remoteIpBytes);
            var remoteIp = new IPAddress(remoteIpBytes);

            var authorized = Volatile.Read(ref _policy)?.Peer(Convert.ToHexString(peer.CertHash));
            if (Control != null && (authorized == null || authorized.Paused || authorized.Address != remoteIpUint))
                throw new InvalidDataException($"Unauthorized VPN address {remoteIp} announced by {peer.Name}.");
            if (_session == null) throw new InvalidOperationException("VPN adapter is not available.");
            // Never change an address silently after announcing it on the wire.
            if (remoteIpUint == _localIpUint)
            {
                throw new InvalidDataException($"LAN address collision: both nodes use {_localIp}. Assign unique addresses in VPN settings.");
            }

            lock (_lifecycle)
            {
                if (_session?.IsHealthy != true || (Control != null && (!Control.Configuration.Enabled ||
                    Volatile.Read(ref _policy)?.Peer(Convert.ToHexString(peer.CertHash)) is not { Paused: false } latest || latest.Address != remoteIpUint)))
                    throw new InvalidDataException("VPN authorization changed during negotiation.");
                if (ActivePeers.ContainsKey(remoteIpUint) || HasActivePeer(peer.Id))
                    throw new InvalidDataException($"VPN session or address {remoteIp} is already in use.");
                peerSession = new PeerLanSession(remoteIpUint, remoteIp, peer, stream, connection);
                ActivePeers[remoteIpUint] = peerSession;
            }
            Console.WriteLine($"[LAN] {peer.Name} joined as {remoteIp}. Active LAN peers: {ActivePeers.Count}.");
            StateChanged?.Invoke();

            datagramHandler = dgram =>
            {
                if (dgram.Length > 0 && dgram[0] == QuicPunch.Datagrams.QuicDatagramEnvelope.MagicMarker) return;
                ProcessIncomingPacket(peerSession, dgram);
            };

            if (connection.DatagramChannel != null)
            {
                connection.DatagramChannel.OnDatagramReceived += datagramHandler;
            }

            byte[] lenBuffer = new byte[2];
            // Zero-length frames were ignored by legacy receivers. Both updated
            // ends advertise this capability before using fragmented datagrams.
            await stream.WriteAsync(lenBuffer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(lenBuffer, ct).ConfigureAwait(false);
                    ushort len = BinaryPrimitives.ReadUInt16LittleEndian(lenBuffer);
                    if (len == 0) { peerSession.SupportsFragmentedDatagrams = true; continue; }

                    await stream.ReadExactlyAsync(rented.AsMemory(0, len), ct).ConfigureAwait(false);
                    ProcessIncomingPacket(peerSession, rented.AsSpan(0, len));
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception) when (peerSession?.IsDisposed == true) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[LAN] Tunnel error with {peer.Name}: {ex.Message}");
            if (Control != null) throw;
        }
        finally
        {
            if (connection.DatagramChannel != null && datagramHandler != null)
            {
                try { connection.DatagramChannel.OnDatagramReceived -= datagramHandler; } catch { }
            }
            if (peerSession != null)
            {
                ActivePeers.TryRemove(new KeyValuePair<uint, PeerLanSession>(remoteIpUint, peerSession));
                peerSession.Dispose();
                StateChanged?.Invoke();
            }
            Console.WriteLine($"[LAN] Tunnel with {peer.Name} closed.");
        }
    }

    private void CaptureVpnAndSendToPeers(IVpnSession session, CancellationToken ct)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        int failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!session.WaitForPacket(100, ct))
                    { failures = 0; continue; }

                    while (!ct.IsCancellationRequested)
                    {
                        int packetSize = session.ReceivePacket(rented);
                        if (packetSize <= 0)
                            break;

                        var packetSpan = rented.AsSpan(0, packetSize);
                        if (!Ipv4Packet.TryRead(packetSpan, out var ip)) continue;
                        var policy = Volatile.Read(ref _policy);
                        if (IsBroadcastOrMulticast(ip.Destination))
                        {
                            using var packet = PooledPacket.Create(packetSpan);
                            foreach (var target in ActivePeers.Values)
                                if (policy == null || policy.Peer(target.Identity)?.Broadcast == true)
                                    SendPacketToPeerStream(target, packet);
                        }
                        else
                        {
                            var target = FindTarget(policy, ip.Destination);
                            if (target == null) continue;
                            _replies.Record(target.Identity, packetSpan, ip);
                            using var packet = PooledPacket.Create(packetSpan);
                            SendPacketToPeerStream(target, packet);
                        }
                    }
                    failures = 0;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Console.WriteLine($"[LAN] VPN capture error: {ex.Message}");
                    if (++failures >= 3) { LastError = ex.Message; AdapterStatus = "Error: capture stopped"; StateChanged?.Invoke(); break; }
                    Thread.Sleep(100);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void SendPacketToPeerStream(PeerLanSession session, PooledPacket packet)
    {
        if (session.TrySendDatagram(packet))
        {
            Interlocked.Increment(ref TotalTxPackets);
            Interlocked.Add(ref TotalTxBytes, packet.Length);
            return;
        }

        // A native congestion drop must not become a reliable retransmission queue.
        if (session.Connection.IsNativeQuic)
        {
            Interlocked.Increment(ref session.DroppedPackets);
            // Older peers have no fragmentation capability. Tell local senders
            // their actual path MTU instead of silently black-holing DF packets.
            int mtu = session.Connection.DatagramChannel?.MaxSendDatagramLength ?? 0;
            if (!session.SupportsFragmentedDatagrams && mtu > 0 && packet.Length > mtu &&
                Ipv4Packet.TryRead(packet.Span, out var ip) && !IsBroadcastOrMulticast(ip.Destination) &&
                (ip.Source == _localIpUint || Volatile.Read(ref _hostAddresses).Contains(ip.Source)))
            {
                var feedback = CreateMtuFeedback(packet.Span, _localIpUint, mtu);
                if (feedback != null) try { _session?.SendPacket(feedback); } catch { }
            }
            return;
        }
        packet.AddRef();
        session.EnqueuePacket(packet);
        Interlocked.Increment(ref TotalTxPackets);
        Interlocked.Add(ref TotalTxBytes, packet.Length);
    }

    internal static byte[]? CreateMtuFeedback(ReadOnlySpan<byte> packet, uint gateway, int mtu)
    {
        if (mtu < 68 || mtu > ushort.MaxValue || packet.Length <= mtu || !Ipv4Packet.TryRead(packet, out var ip)) return null;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[6..8]);
        if ((flags & 0x4000) == 0 || (flags & 0x1fff) != 0 || ip.Source == 0 || ip.Source >= 0xe0000000 ||
            ip.Destination >= 0xe0000000) return null;
        if (packet[9] == 1 && packet.Length > ip.HeaderLength && packet[ip.HeaderLength] is 3 or 4 or 5 or 11 or 12) return null;
        int quote = Math.Min(packet.Length, ip.HeaderLength + 8);
        byte[] reply = new byte[28 + quote];
        reply[0] = 0x45; reply[8] = 64; reply[9] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)reply.Length);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), gateway);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(16), ip.Source);
        reply[20] = 3; reply[21] = 4;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(26), (ushort)mtu);
        packet[..quote].CopyTo(reply.AsSpan(28));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), InternetChecksum(reply.AsSpan(0, 20)));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22), InternetChecksum(reply.AsSpan(20)));
        return reply;
    }

    private static ushort InternetChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i, 2));
        if (i < data.Length) sum += (uint)data[i] << 8;
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        return (ushort)~sum;
    }

    private PeerLanSession? FindTarget(VpnPolicy? policy, uint destination)
    {
        if (policy == null) return ActivePeers.GetValueOrDefault(destination);
        string? identity = policy.Route(destination);
        var owner = identity == null ? null : policy.Peer(identity);
        return owner == null || owner.Paused ? null : ActivePeers.GetValueOrDefault(owner.Address);
    }

    internal void ProcessIncomingPacket(PeerLanSession peer, ReadOnlySpan<byte> bytes)
    {
        if (!Ipv4Packet.TryRead(bytes, out var ip)) { Interlocked.Increment(ref peer.DroppedPackets); return; }
        var policy = Volatile.Read(ref _policy);
        bool broadcast = IsBroadcastOrMulticast(ip.Destination);
        bool sourceAllowed = policy == null ? ip.Source == peer.RemoteIpUint : policy.AcceptSource(peer.Identity, ip.Source);
        sourceAllowed &= !Volatile.Read(ref _hostAddresses).Contains(ip.Source);
        bool destinationAllowed = policy == null ? ip.Destination == _localIpUint || broadcast :
            policy.AcceptDestination(peer.Identity, ip.Destination, broadcast) ||
            (!broadcast && _replies.IsReply(peer.Identity, bytes, ip));
        if (!sourceAllowed || !destinationAllowed || !ActivePeers.TryGetValue(peer.RemoteIpUint, out var current) || !ReferenceEquals(current, peer))
        {
            Interlocked.Increment(ref peer.DroppedPackets);
            return;
        }
        Interlocked.Increment(ref TotalRxPackets);
        Interlocked.Add(ref TotalRxBytes, bytes.Length);
        Interlocked.Increment(ref peer.RxPackets);
        Interlocked.Add(ref peer.RxBytes, bytes.Length);
        var target = broadcast ? null : FindTarget(policy, ip.Destination);
        if (target != null && !ReferenceEquals(target, peer))
        {
            if (policy?.Peer(peer.Identity)?.Forwarding != true || bytes[8] <= 1)
            { Interlocked.Increment(ref peer.DroppedPackets); return; }
            using var packet = PooledPacket.Create(bytes);
            var mutable = packet.Buffer.AsSpan(0, packet.Length);
            mutable[8]--;
            mutable[10] = mutable[11] = 0;
            uint sum = 0;
            for (int i = 0; i < ip.HeaderLength; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(mutable.Slice(i, 2));
            while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
            BinaryPrimitives.WriteUInt16BigEndian(mutable[10..12], (ushort)~sum);
            SendPacketToPeerStream(target, packet);
            return;
        }
        if (!broadcast && ip.Destination != _localIpUint && policy?.Peer(peer.Identity)?.Forwarding != true)
        {
            bool localAddress = Volatile.Read(ref _hostAddresses).Contains(ip.Destination);
            if (!localAddress) { Interlocked.Increment(ref peer.DroppedPackets); return; }
        }
        // Broadcast is delivered locally only: relaying it creates loops and breaks identity-bound source validation.
        try { _session?.SendPacket(bytes); }
        catch { Interlocked.Increment(ref peer.DroppedPackets); }
    }

    public bool DisconnectPeer(Guid peerId)
    {
        var target = ActivePeers.FirstOrDefault(kvp => kvp.Value.Peer.Id == peerId);
        if (target.Value != null && ActivePeers.TryRemove(target.Key, out var session))
        {
            session.Dispose();
            StateChanged?.Invoke();
            return true;
        }
        return false;
    }

    public async Task<(bool Success, double RttMs, string? Error)> PingIpAsync(IPAddress targetIp, int timeoutMs = 1500)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(targetIp, timeoutMs).ConfigureAwait(false);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                return (true, reply.RoundtripTime, null);
            }
            return (false, 0, reply.Status.ToString());
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private bool IsBroadcastOrMulticast(uint destIp)
    {
        if (destIp == 0xFFFFFFFF) return true;
        if (destIp >= 0xE0000000 && destIp <= 0xEFFFFFFF) return true;

        return _hasDirectedBroadcast && destIp == _directedBroadcast;
    }

    private void SetAddressFields(IPAddress ip, string subnetMask)
    {
        _localIp = ip;
        _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        _subnetMask = subnetMask;
        uint mask = Ipv4Prefix.Address(subnetMask);
        _directedBroadcast = (_localIpUint & mask) | ~mask;
        _hasDirectedBroadcast = Ipv4Prefix.MaskLength(subnetMask) < 31;
    }

    private static bool TryParseIpv4(string value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address = parsed;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    public void Dispose() => StopTun();
}

/// <summary>
/// High-performance zero-allocation packet container backed by ArrayPool&lt;byte&gt;.Shared
/// with atomic reference counting. Instances are recycled via an internal pool to eliminate
/// all heap allocations during steady-state packet forwarding.
/// </summary>
public sealed class PooledPacket : IDisposable
{
    private static readonly ConcurrentQueue<PooledPacket> _instancePool = new();
    private const int MaxPooledInstances = 2048;

    private byte[]? _buffer;
    private int _length;
    private int _refCount;

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledPacket));
    public int Length => _length;
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);
    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _length);

    private PooledPacket() { }

    public static PooledPacket Create(ReadOnlySpan<byte> source)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(rented);

        if (!_instancePool.TryDequeue(out var packet))
        {
            packet = new PooledPacket();
        }

        packet._buffer = rented;
        packet._length = source.Length;
        packet._refCount = 1;
        return packet;
    }

    public static unsafe PooledPacket Create(byte* source, int length)
    {
        byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        new ReadOnlySpan<byte>(source, length).CopyTo(rented);

        if (!_instancePool.TryDequeue(out var packet))
        {
            packet = new PooledPacket();
        }

        packet._buffer = rented;
        packet._length = length;
        packet._refCount = 1;
        return packet;
    }

    public void AddRef()
    {
        Interlocked.Increment(ref _refCount);
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            byte[]? buf = Interlocked.Exchange(ref _buffer, null);
            if (buf != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
            _length = 0;
            if (_instancePool.Count < MaxPooledInstances)
            {
                _instancePool.Enqueue(this);
            }
        }
    }
}

/// <summary>
/// Bounded queue for LAN sessions that discards oldest packets under congestion
/// and immediately recycles discarded packets to ArrayPool without memory leaks.
/// </summary>
public sealed class PooledPacketQueue : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<PooledPacket> _queue;
    private readonly int _capacity;
    private readonly SemaphoreSlim _signal = new(0);
    private bool _disposed;

    public PooledPacketQueue(int capacity = 512)
    {
        _capacity = capacity;
        _queue = new Queue<PooledPacket>(capacity);
    }

    public int Count
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    public bool Enqueue(PooledPacket packet, ref long droppedCounter)
    {
        PooledPacket? dropped = null;
        bool releaseSignal = false;

        lock (_lock)
        {
            if (_disposed)
            {
                packet.Dispose();
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                dropped = _queue.Dequeue();
                Interlocked.Increment(ref droppedCounter);
            }
            else
            {
                releaseSignal = true;
            }

            _queue.Enqueue(packet);
        }

        dropped?.Dispose();
        if (releaseSignal)
        {
            try { _signal.Release(); } catch (ObjectDisposedException) { }
        }
        return true;
    }

    public ValueTask<PooledPacket?> DequeueAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_disposed)
                return ValueTask.FromResult<PooledPacket?>(null);

            if (_queue.Count > 0)
            {
                _signal.Wait(0);
                return ValueTask.FromResult<PooledPacket?>(_queue.Dequeue());
            }
        }

        return DequeueSlowAsync(ct);
    }

    private async ValueTask<PooledPacket?> DequeueSlowAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_disposed || _queue.Count == 0)
                return null;

            return _queue.Dequeue();
        }
    }

    public void Dispose()
    {
        List<PooledPacket> remaining;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            remaining = new List<PooledPacket>(_queue);
            _queue.Clear();
        }

        foreach (var p in remaining)
        {
            p.Dispose();
        }

        try { _signal.Release(int.MaxValue / 2); } catch { }
        _signal.Dispose();
    }
}

public sealed class PeerLanSession : IDisposable
{
    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public uint RemoteIpUint { get; }
    public IPAddress RemoteIp { get; }
    public PeerInfo Peer { get; }
    public string Identity { get; }
    public Stream Stream { get; }
    public QuicConnection Connection { get; }
    public DateTime ConnectedAt { get; } = DateTime.Now;
    public long RxPackets;
    public long TxPackets;
    public long RxBytes;
    public long TxBytes;
    public long DroppedPackets;
    public volatile bool SupportsFragmentedDatagrams;

    private readonly PooledPacketQueue _outQueue = new(512);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendTask;

    public PeerLanSession(uint remoteIpUint, IPAddress remoteIp, PeerInfo peer, Stream stream, QuicConnection connection)
    {
        RemoteIpUint = remoteIpUint;
        RemoteIp = remoteIp;
        Peer = peer;
        Identity = Convert.ToHexString(peer.CertHash);
        Stream = stream;
        Connection = connection;
        _sendTask = Task.Run(SendLoopAsync);
    }

    public bool TrySendDatagram(PooledPacket packet)
    {
        if (Connection.DatagramChannel != null && Connection.DatagramChannel.IsSendEnabled)
        {
            int maxLen = Connection.DatagramChannel.MaxSendDatagramLength;
            if (packet.Length > maxLen && SupportsFragmentedDatagrams && packet.Length <= 9000)
            {
                if (!Connection.SendFrame(VirtualLanHandler.VpnDatagramCategory, packet.Span)) return false;
                Interlocked.Increment(ref TxPackets);
                Interlocked.Add(ref TxBytes, packet.Length);
                return true;
            }
            if (packet.Length <= maxLen)
            {
                if (Connection.SendDatagram(packet.Span))
                {
                    Interlocked.Increment(ref TxPackets);
                    Interlocked.Add(ref TxBytes, packet.Length);
                    return true;
                }
            }
        }
        return false;
    }

    public void EnqueuePacket(PooledPacket packet)
    {
        _outQueue.Enqueue(packet, ref DroppedPackets);
    }

    private async Task SendLoopAsync()
    {
        byte[] sendBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65535 + 2);
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                PooledPacket? packet = await _outQueue.DequeueAsync(_cts.Token).ConfigureAwait(false);
                if (packet == null)
                    break;

                try
                {
                    int packetLen = packet.Length;
                    if (packetLen == 0 || packetLen > ushort.MaxValue)
                        continue;

                    BinaryPrimitives.WriteUInt16LittleEndian(sendBuffer.AsSpan(0, 2), (ushort)packetLen);
                    packet.Span.CopyTo(sendBuffer.AsSpan(2));

                    int totalLen = packetLen + 2;
                    await Stream.WriteAsync(sendBuffer.AsMemory(0, totalLen), _cts.Token).ConfigureAwait(false);

                    // Smart flush: flush only when the queue is currently drained, allowing QUIC to coalesce back-to-back packets
                    if (_outQueue.Count == 0)
                    {
                        await Stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref TxPackets);
                    Interlocked.Add(ref TxBytes, packetLen);
                }
                finally
                {
                    packet.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(sendBuffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _outQueue.Dispose();
        try { Stream.Close(); } catch { }
        try { _sendTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        _cts.Dispose();
    }
}

