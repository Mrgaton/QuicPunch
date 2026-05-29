using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Quic;
using System.Runtime.InteropServices;
using Wintun;

namespace QuicPunch
{
    internal class FriendsLanHandler : QuicPunchCore.IProtocolHandler
    {
        public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public ushort PreferredPort => 0; 
        public string ProtocolName => "FriendsLAN";

        public ZstandardCompressionOptions? CompressionOptions => null; //new ZstandardCompressionOptions() { AppendChecksum = false, EnableLongDistanceMatching = false, Quality = 6};

        private IPAddress _localIp;

        public ConcurrentDictionary<uint, Stream> ActivePeers { get; } = new();

        private WintunSession _session;

        public void SetupTun()
        {
            try
            {
                uint version = WintunAdapter.GetRunningDriverVersion();
                Console.WriteLine($"Running driver version: {version}");
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine("wintun.dll not found in the output directory. Please copy it.");
                return;
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"Win32Exception while checking driver (expected if not installed/admin): {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error: {ex}");
            }

            Console.Write("Write your last ip digit 10.0.0.x:");
            string ipDigit = Console.ReadLine();

            string ip = $"10.0.0.{ipDigit}";

            _localIp = IPAddress.Parse(ip);

            Console.WriteLine("\nTrying to create QuicPunch adapter...");
            try
            {
                var adapter = WintunAdapter.Create("QuicPunchAdapter", "QuicPunchTunnel");
                Console.WriteLine($"Adapter created successfully! LUID: {adapter.Luid}");


                SetAdapterIP("QuicPunchAdapter", ip, "255.0.0.0");
                SetAdapterMTU("QuicPunchAdapter", 65535);

                _session = adapter.StartSession();
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"Failed to create adapter (requires Administrator privileges): {ex.Message} (Error code: {ex.NativeErrorCode})");
                if (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
                {
                    Console.WriteLine("Verification result: The library successfully called wintun.dll! Access Denied is the expected outcome without Administrator privileges.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unexpected error during creation: {ex}");
            }

            Task.Run(() => CaptureWintunAndSendToInternet(10));
        }

        public FriendsLanHandler()
        {

        }
        public async Task DeniedAsync(PeerInfo peer, CancellationToken ct)
        {
            Console.WriteLine($"\n[FriendsLAN] Peer: {peer.Name} ({peer.EndPoint}) failed or denied the conection.");
        }

        public  async Task HandleAsync(
            QuicConnection connection,
            Stream stream,
            PeerInfo peer,
            CancellationToken ct)
        {
            if (connection == null) 
                throw new ArgumentNullException(nameof(connection));

            Console.WriteLine($"\n[FriendsLAN] Secure tunnel established with peer: {peer.Name} ({peer.EndPoint})");

            uint remoteIp;

            byte[] localIpBytes = _localIp.GetAddressBytes();
            await stream.WriteAsync(localIpBytes, ct);
            await stream.FlushAsync(ct);

            byte[] remoteIpBytes = new byte[4];
            await ReadExactlyAsync(stream, remoteIpBytes, 4, ct);
            remoteIp = BinaryPrimitives.ReadUInt32BigEndian(remoteIpBytes);
            Console.WriteLine($"[FriendsLAN] Peer virtual IP: {remoteIp}");

            try
            {
                ActivePeers[remoteIp] = stream;

            
                byte[] lenBuffer = new byte[2];

                while (true) //(!ct.IsCancellationRequested)
                {
                    try
                    {
                        await ReadExactlyAsync(stream, lenBuffer, 2, ct);
                        ushort len = BitConverter.ToUInt16(lenBuffer, 0);

                        if (len == 0 || len > ushort.MaxValue)
                            continue;

                        byte[] packet = new byte[len];

                        await ReadExactlyAsync(stream, packet, (int)len, ct);

                        unsafe
                        {
                            fixed (byte* p = packet)
                            {
                                LogPacket(p, (uint)len);
                            }
                        }

                        if (_session != null)
                        {
                            _session.SendPacket(packet);
                        }
                    }
                    catch (QuicException quicex)
                    {
                        Console.WriteLine($"[FriendsLAN] QUIC error with peer {(remoteIp.ToString() ?? peer.Name)}: {quicex.Message}");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FriendsLAN] Tunnel error with peer {(peer.EndPoint.ToString() ?? peer.Name)}: {ex.Message}");
            }
            finally
            {
                if (remoteIp != null)
                {
                    ActivePeers.TryRemove(remoteIp, out _);
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

        private unsafe void CaptureWintunAndSendToInternet(byte ipFirstDigit)
        {
            const int ERROR_NO_MORE_ITEMS = 259;

            Span<byte> sizeBuffer = stackalloc byte[2];

            while (true)
            {
                _session.ReadWaitEvent.WaitOne();

                while (true)
                {
                    byte* packetPointer =
                        WintunApi.WintunReceivePacket(_session.Handle, out uint packetSize);

                    if (packetPointer == null)
                    {
                        int error = Marshal.GetLastWin32Error();

                        if (error == ERROR_NO_MORE_ITEMS)
                            break;

                        throw new Win32Exception(error);
                    }

                    if (packetSize < 20) // minimum ipv4 header size
                        continue;

                    if (packetPointer[0] >> 4 != 4) // not IPv4
                        continue;

                    try
                    {
                        uint destIp =
                            BinaryPrimitives.ReverseEndianness(
                                *(uint*)(packetPointer + 16));

                        if (packetPointer[16] < 200)
                        {
                            LogPacket(packetPointer, packetSize);
                        }

                        if (destIp == 0)
                        {
                            WintunApi.WintunSendPacket(_session.Handle, packetPointer);
                        }

                        if (packetPointer[16] != ipFirstDigit)
                            continue;

                        if (packetPointer[16 + 3] == 255 || packetPointer[16] > 224)
                        {
                            foreach (var stream in ActivePeers.Values)
                            {
                                BinaryPrimitives.WriteUInt16LittleEndian(sizeBuffer, (ushort)packetSize);
                                stream.Write(sizeBuffer);
                                stream.Write(new ReadOnlySpan<byte>(packetPointer, (int)packetSize));
                            }
                        }
                        else if (ActivePeers.TryGetValue(destIp, out var stream))
                        //if (_friendsLanHandler.ActivePeers.Count > 0)
                        {
                            //var stream = _friendsLanHandler.ActivePeers.ElementAt(0).Value;

                            BinaryPrimitives.WriteUInt16LittleEndian(sizeBuffer, (ushort)packetSize);
                            stream.Write(sizeBuffer);
                            stream.Write(new ReadOnlySpan<byte>(packetPointer, (int)packetSize));
                        }
                    }
                    finally
                    {
                        WintunApi.WintunReleaseReceivePacket(
                            _session.Handle,
                            packetPointer);
                    }
                }
            }
        }
        public static unsafe void LogPacket(byte* packetPointer, uint packetSize)
        {
            byte versionAndIhl = packetPointer[0];
            int version = versionAndIhl >> 4;
            int ihl = (versionAndIhl & 0x0F) * 4;

            byte protocol = packetPointer[9];
            byte ttl = packetPointer[8];

            ushort identification =
                BinaryPrimitives.ReadUInt16BigEndian(
                    new ReadOnlySpan<byte>(packetPointer + 4, 2));

            ushort totalLength =
                BinaryPrimitives.ReadUInt16BigEndian(
                    new ReadOnlySpan<byte>(packetPointer + 2, 2));

            var srcIp = new IPAddress(
                new ReadOnlySpan<byte>(packetPointer + 12, 4));

            var dstIp = new IPAddress(
                new ReadOnlySpan<byte>(packetPointer + 16, 4));

            string protocolName = protocol switch
            {
                1 => "ICMP",
                6 => "TCP",
                17 => "UDP",
                _ => $"UNKNOWN({protocol})"
            };

            Console.WriteLine(
                $"IPv{version} {protocolName} " +
                $"{srcIp} -> {dstIp} " +
                $"TTL={ttl} " +
                $"LEN={totalLength} " +
                $"ID={identification} " +
                $"HDR={ihl} " +
                $"PKT={packetSize}");
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
