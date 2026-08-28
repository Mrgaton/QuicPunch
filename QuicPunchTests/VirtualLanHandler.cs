using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;
using Wintun;

namespace QuicPunchTests
{
    public class PeerLanSession : IDisposable
    {
        public uint RemoteIpUint { get; }
        public IPAddress RemoteIp { get; }
        public PeerInfo Peer { get; }
        public Stream Stream { get; }
        public DateTime ConnectedAt { get; } = DateTime.Now;
        public long RxPackets;
        public long TxPackets;
        public long RxBytes;
        public long TxBytes;

        private readonly System.Threading.Channels.Channel<byte[]> _outQueue = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(512)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _sendTask;

        public PeerLanSession(uint remoteIpUint, IPAddress remoteIp, PeerInfo peer, Stream stream)
        {
            RemoteIpUint = remoteIpUint;
            RemoteIp = remoteIp;
            Peer = peer;
            Stream = stream;
            _sendTask = Task.Run(SendLoopAsync);
        }

        public void EnqueuePacket(byte[] packetData)
        {
            _outQueue.Writer.TryWrite(packetData);
        }

        private async Task SendLoopAsync()
        {
            byte[] sizeBuffer = new byte[2];
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var packet = await _outQueue.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                    BinaryPrimitives.WriteUInt16LittleEndian(sizeBuffer, (ushort)packet.Length);
                    await Stream.WriteAsync(sizeBuffer, _cts.Token).ConfigureAwait(false);
                    await Stream.WriteAsync(packet, _cts.Token).ConfigureAwait(false);
                    await Stream.FlushAsync(_cts.Token).ConfigureAwait(false);

                    Interlocked.Increment(ref TxPackets);
                    Interlocked.Add(ref TxBytes, packet.Length);
                }
            }
            catch {}
        }

        public void Dispose()
        {
            _cts.Cancel();
            _outQueue.Writer.TryComplete();
        }
    }

    internal class VirtualLanHandler : QuicPunch.QuicPunch.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public ushort PreferredPort => 0; 
        public string ProtocolName => "FriendsLAN";

        public ZstandardCompressionOptions? CompressionOptions => null;

        private IPAddress _localIp = IPAddress.Parse("10.0.0.2");
        private uint _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse("10.0.0.2").GetAddressBytes());
        private string _subnetMask = "255.0.0.0";
        private int _mtu = 9000;

        public string AdapterName { get; } = "QuicPunchAdapter";
        public string AdapterStatus { get; private set; } = "Not Started";
        public string? LastError { get; private set; }

        public IPAddress LocalIp => _localIp;
        public string SubnetMask => _subnetMask;
        public int Mtu => _mtu;

        public long TotalRxPackets;
        public long TotalTxPackets;
        public long TotalRxBytes;
        public long TotalTxBytes;

        public ConcurrentDictionary<uint, PeerLanSession> ActivePeers { get; } = new();

        private WintunAdapter? _adapter;
        private WintunSession? _session;
        private CancellationTokenSource? _captureCts;

        public VirtualLanHandler()
        {
        }

        public void SetupTun(string initialIp = "10.0.0.2", string subnetMask = "255.0.0.0")
        {
            if (IPAddress.TryParse(initialIp, out var parsedIp))
            {
                _localIp = parsedIp;
                _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(parsedIp.GetAddressBytes());
            }
            _subnetMask = subnetMask;

            try
            {
                Console.WriteLine("\n[FriendsLAN] Initializing Wintun adapter...");
                _adapter = WintunAdapter.Create(AdapterName, "QuicPunchTunnel");
                Console.WriteLine($"[FriendsLAN] Adapter created with LUID: {_adapter.Luid}");

                SetAdapterIP(AdapterName, _localIp.ToString(), _subnetMask);
                SetAdapterMTU(AdapterName, _mtu);

                _session = _adapter.StartSession();
                AdapterStatus = "Active";
                LastError = null;

                _captureCts?.Cancel();
                _captureCts = new CancellationTokenSource();
                Task.Run(() => CaptureWintunAndSendToInternet(_captureCts.Token));
            }
            catch (Win32Exception ex)
            {
                LastError = ex.Message;
                if (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
                {
                    AdapterStatus = "Administrator Privileges Required";
                    Console.WriteLine("[FriendsLAN] Administrator privileges required to create Wintun adapter.");
                }
                else
                {
                    AdapterStatus = "Error";
                    Console.WriteLine($"[FriendsLAN] Failed to create adapter: {ex.Message} (Error code: {ex.NativeErrorCode})");
                }
            }
            catch (Exception ex)
            {
                AdapterStatus = "Error";
                LastError = ex.Message;
                Console.WriteLine($"[FriendsLAN] Unexpected error initializing Wintun: {ex}");
            }
        }

        public bool SetVirtualIp(string newIpStr, string subnetMask = "255.0.0.0")
        {
            if (!IPAddress.TryParse(newIpStr, out var newIp))
            {
                return false;
            }

            _localIp = newIp;
            _localIpUint = BinaryPrimitives.ReadUInt32BigEndian(newIp.GetAddressBytes());
            _subnetMask = subnetMask;

            if (_session != null)
            {
                try
                {
                    SetAdapterIP(AdapterName, _localIp.ToString(), _subnetMask);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }

            return true;
        }

        public async Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[FriendsLAN] Connection request denied or failed for peer: {peer.Name} ({peer.ActiveEndPoint})");
            await Task.CompletedTask;
        }

        public async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            if (connection == null) 
                throw new ArgumentNullException(nameof(connection));

            Console.WriteLine($"\n[FriendsLAN] Secure tunnel established with peer: {peer.Name} ({peer.ActiveEndPoint})");

            uint remoteIpUint = 0;
            IPAddress? remoteIp = null;

            try
            {
                byte[] localIpBytes = _localIp.GetAddressBytes();
                await stream.WriteAsync(localIpBytes, ct);
                await stream.FlushAsync(ct);

                byte[] remoteIpBytes = new byte[4];
                await ReadExactlyAsync(stream, remoteIpBytes, 4, ct);
                remoteIpUint = BinaryPrimitives.ReadUInt32BigEndian(remoteIpBytes);
                remoteIp = new IPAddress(remoteIpBytes);

                Console.WriteLine($"[FriendsLAN] Connected peer '{peer.Name}' with Virtual IP: {remoteIp}");

                var peerSession = new PeerLanSession(remoteIpUint, remoteIp, peer, stream);
                ActivePeers[remoteIpUint] = peerSession;

                byte[] lenBuffer = new byte[2];

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await ReadExactlyAsync(stream, lenBuffer, 2, ct);
                        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(lenBuffer);

                        if (len == 0 || len > 65535)
                            continue;

                        byte[] packet = new byte[len];
                        await ReadExactlyAsync(stream, packet, len, ct);

                        Interlocked.Increment(ref TotalRxPackets);
                        Interlocked.Add(ref TotalRxBytes, len);
                        Interlocked.Increment(ref peerSession.RxPackets);
                        Interlocked.Add(ref peerSession.RxBytes, len);

                        if (packet.Length >= 20 && (packet[0] >> 4) == 4)
                        {
                            uint destIp = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(packet, 16, 4));
                            bool isBroadcast = IsBroadcastOrMulticast(destIp);

                            if ((destIp == _localIpUint || isBroadcast) && _session != null)
                            {
                                try
                                {
                                    _session.SendPacket(packet);
                                }
                                catch { }
                            }

                            if (isBroadcast)
                            {
                                RelayBroadcastToOtherPeers(packet, remoteIpUint);
                            }
                            else if (destIp != _localIpUint && ActivePeers.TryGetValue(destIp, out var targetPeerSession))
                            {
                                SendPacketToPeerStream(targetPeerSession, packet);
                            }
                        }
                    }
                    catch (QuicException quicEx)
                    {
                        Console.WriteLine($"[FriendsLAN] QUIC stream ended with peer {peer.Name}: {quicEx.Message}");
                        break;
                    }
                    catch (EndOfStreamException)
                    {
                        Console.WriteLine($"[FriendsLAN] Connection closed by peer {peer.Name}.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FriendsLAN] Packet error from peer {peer.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsLAN] Tunnel setup error with peer {peer.Name}: {ex.Message}");
            }
            finally
            {
                if (remoteIpUint != 0)
                {
                    if (ActivePeers.TryRemove(remoteIpUint, out var s))
                    {
                        s.Dispose();
                    }
                }
                Console.WriteLine($"[FriendsLAN] Secure tunnel closed with peer: {peer.Name}");
            }
        }

        private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), ct);
                if (read == 0)
                {
                    throw new EndOfStreamException("Connection closed by remote peer while reading packet data.");
                }
                totalRead += read;
            }
        }

        private unsafe void CaptureWintunAndSendToInternet(CancellationToken ct)
        {
            const int ERROR_NO_MORE_ITEMS = 259;

            while (!ct.IsCancellationRequested && _session != null)
            {
                try
                {
                    _session.ReadWaitEvent.WaitOne(100);

                    while (!ct.IsCancellationRequested && _session != null)
                    {
                        byte* packetPointer = WintunApi.WintunReceivePacket(_session.Handle, out uint packetSize);

                        if (packetPointer == null)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == ERROR_NO_MORE_ITEMS)
                                break;
                            throw new Win32Exception(error);
                        }

                        try
                        {
                            if (packetSize < 20) continue;
                            if ((packetPointer[0] >> 4) != 4) continue; // Must be IPv4

                            uint destIp = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(packetPointer + 16, 4));
                            byte[] packetData = new byte[packetSize];
                            Marshal.Copy((IntPtr)packetPointer, packetData, 0, (int)packetSize);

                            bool isBroadcast = IsBroadcastOrMulticast(destIp);

                            if (isBroadcast)
                            {
                                foreach (var peerSession in ActivePeers.Values)
                                {
                                    SendPacketToPeerStream(peerSession, packetData);
                                }
                            }
                            else if (ActivePeers.TryGetValue(destIp, out var targetPeerSession))
                            {
                                SendPacketToPeerStream(targetPeerSession, packetData);
                            }
                        }
                        finally
                        {
                            WintunApi.WintunReleaseReceivePacket(_session.Handle, packetPointer);
                        }
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested) break;
                    Console.WriteLine($"[FriendsLAN] Wintun capture error: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }

        private void SendPacketToPeerStream(PeerLanSession session, byte[] packetData)
        {
            try
            {
                session.EnqueuePacket(packetData);
                Interlocked.Increment(ref TotalTxPackets);
                Interlocked.Add(ref TotalTxBytes, packetData.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsLAN] Error sending packet to peer {session.Peer.Name}: {ex.Message}");
            }
        }

        private void RelayBroadcastToOtherPeers(byte[] packetData, uint sourceIpUint)
        {
            foreach (var peerSession in ActivePeers.Values)
            {
                if (peerSession.RemoteIpUint != sourceIpUint)
                {
                    SendPacketToPeerStream(peerSession, packetData);
                }
            }
        }

        private static bool IsBroadcastOrMulticast(uint destIp)
        {
            // 255.255.255.255 (0xFFFFFFFF)
            if (destIp == 0xFFFFFFFF) return true;

            // Multicast: 224.0.0.0 - 239.255.255.255 (0xE0000000 - 0xEFFFFFFF)
            if (destIp >= 0xE0000000 && destIp <= 0xEFFFFFFF) return true;

            // Subnet broadcast ending in .255 (e.g. 10.255.255.255 or 10.0.0.255)
            if ((destIp & 0x000000FF) == 0x000000FF) return true;

            return false;
        }

        public static void SetAdapterIP(string adapterName, string ipAddress, string subnetMask)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"interface ip set address name=\"{adapterName}\" static {ipAddress} {subnetMask}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
        }

        public static void SetAdapterMTU(string adapterName, int mtu)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"interface ipv4 set subinterface \"{adapterName}\" mtu={mtu} store=persistent",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
        }
    }
}

