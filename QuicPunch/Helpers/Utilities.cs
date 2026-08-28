using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace QuicPunch.Helpers
{
    public static class Utilities
    {
        internal static HttpClient client = new HttpClient();

        public static ushort GetDeterministicPortFromCertHash(byte[] certHash, int minPort = 49152, int maxPort = 65535)
        {
            if (certHash == null || certHash.Length < 4)
                return (ushort)Random.Shared.Next(minPort, maxPort + 1);

            uint val = BinaryPrimitives.ReadUInt32LittleEndian(certHash);
            int range = maxPort - minPort + 1;
            return (ushort)(minPort + (val % range));
        }
        public static List<IPAddress> GetValidLocalIPAddresses()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    string name = ni.Name.ToLowerInvariant();
                    string desc = ni.Description.ToLowerInvariant();

                    if (name.StartsWith("docker") || name.StartsWith("veth") || name.StartsWith("br-") ||
                        name.StartsWith("virbr") || name.StartsWith("vboxnet") || name.StartsWith("vmnet") ||
                        name.Contains("wsl") || desc.Contains("virtual") || desc.Contains("hyper-v") ||
                        desc.Contains("docker") || desc.Contains("vmware") || desc.Contains("virtualbox"))
                    {
                        continue;
                    }

                    var props = ni.GetIPProperties();

                    bool hasGateway = props.GatewayAddresses.Any(g => g.Address != null &&
                        !IPAddress.Any.Equals(g.Address) &&
                        !IPAddress.IPv6Any.Equals(g.Address) &&
                        g.Address.AddressFamily == AddressFamily.InterNetwork);

                    bool isPhysical = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                     ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

                    foreach (var unicast in props.UnicastAddresses)
                    {
                        var ip = unicast.Address;
                        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

                        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        {
                            if (hasGateway || isPhysical)
                            {
                                if (!list.Contains(ip))
                                    list.Add(ip);
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }
        /*public static async Task BigSendAsync( this UdpClient udp, byte[] data, PeerInfo peerInfo)
           {
               if (peerInfo.MinPort > peerInfo.MaxPort || peerInfo.MinPort - peerInfo.MaxPort > ushort.MaxValue / 2)
                   throw new ArgumentException("Invalid port range.", nameof(peerInfo));

               var tasks = new List<Task>();

               foreach (var address in peerInfo.Addresses)
               {
                   for (int port = peerInfo.MinPort; port <= peerInfo.MaxPort; port++)
                   {
                       tasks.Add(udp.SendAsync(data, data.Length, new IPEndPoint(address, port)));
                   }
               }

               await Task.WhenAll(tasks);
           }*/
        public static int GetMostUsedPort(IEnumerable<KeyValuePair<IPEndPoint, int>> responses)
        {
            return responses
            .GroupBy(x => x.Key.Port)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(x => x.Value)
            ).OrderByDescending(e => e.Value).FirstOrDefault().Key;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint IpToUint(IPAddress ip)
        {
            Span<byte> bytes = stackalloc byte[4];

            if (!ip.TryWriteBytes(bytes, out int written) || written != 4)
                throw new ArgumentException("IPv4 only");

            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }
        public static bool IsValidPeerAddress(IPAddress? ip)
        {
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> bytes = stackalloc byte[4];
                if (!ip.TryWriteBytes(bytes, out _)) return false;

                // 0.0.0.0 or 0.x.x.x (Source network / 'This' host)
                if (bytes[0] == 0) return false;

                // 255.255.255.255 (Limited broadcast)
                if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return false;

                // Multicast (224.0.0.0 to 239.255.255.255)
                if (bytes[0] >= 224 && bytes[0] <= 239) return false;

                // Reserved for future use (240.0.0.0/4 except 255.255.255.255)
                if (bytes[0] >= 240) return false;

                return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6Multicast) return false;
                if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;

                return true;
            }

            return false;
        }

        public static async Task BigSendAsync(this UdpClient udp, ReadOnlyMemory<byte> data, PeerInfo peerInfo)
        {
            if (peerInfo.ActiveEndPoint != null)
            {
                try
                {
                    await udp.SendAsync(data, peerInfo.ActiveEndPoint).ConfigureAwait(false);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                return;
            }

            if (peerInfo.Addresses == null || peerInfo.Addresses.Length == 0)
                return;

            int minPort = Math.Clamp(peerInfo.MinPort > 0 ? peerInfo.MinPort : 4002, 1, 65535);
            int maxPort = Math.Clamp(peerInfo.MaxPort > 0 ? peerInfo.MaxPort : minPort, 1, 65535);

            if (minPort > maxPort)
                (minPort, maxPort) = (maxPort, minPort);

            const int MaxPortsPerBurst = 64;
            int portSpan = maxPort - minPort + 1;

            int[] targetPorts;
            if (portSpan <= MaxPortsPerBurst)
            {
                targetPorts = new int[portSpan];
                for (int i = 0; i < portSpan; i++)
                    targetPorts[i] = minPort + i;
            }
            else
            {
                var portSet = new HashSet<int>(MaxPortsPerBurst);
                portSet.Add(minPort);
                portSet.Add(maxPort);
                portSet.Add(minPort + (portSpan / 2));

                for (int d = 1; d <= 12 && portSet.Count < MaxPortsPerBurst; d++)
                {
                    if (minPort + d <= 65535) portSet.Add(minPort + d);
                    if (minPort - d >= 1) portSet.Add(minPort - d);
                    if (maxPort + d <= 65535) portSet.Add(maxPort + d);
                    if (maxPort - d >= 1) portSet.Add(maxPort - d);
                }

                while (portSet.Count < MaxPortsPerBurst)
                {
                    portSet.Add(Random.Shared.Next(minPort, maxPort + 1));
                }

                targetPorts = portSet.ToArray();
            }

            foreach (var address in peerInfo.Addresses)
            {
                if (!IsValidPeerAddress(address))
                    continue;

                for (int i = 0; i < targetPorts.Length; i++)
                {
                    try
                    {
                        await udp.SendAsync(data, new IPEndPoint(address, targetPorts[i])).ConfigureAwait(false);
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return; }
                }
            }
        }

        public static byte[] Combine(params byte[][] arrays)
        {
            int len = 0;
            foreach (var a in arrays) len += a.Length;

            var r = new byte[len];
            int o = 0;

            foreach (var a in arrays)
            {
                Buffer.BlockCopy(a, 0, r, o, a.Length);
                o += a.Length;
            }

            return r;
        }

        static bool TryParseEndpoint(string line, out string host, out int port)
        {
            host = "";
            port = 0;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            line = line.Trim();

            int colon = line.LastIndexOf(':');
            if (colon <= 0 || colon == line.Length - 1)
                return false;

            host = line[..colon].Trim();
            return int.TryParse(line[(colon + 1)..].Trim(), out port);
        }

        public static IPEndPoint[]? ResolveEndpoint(string line)
        {
            if (!TryParseEndpoint(line, out var host, out var port))
                return null;

            if (IPAddress.TryParse(host, out var ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return [new IPEndPoint(ip, port)];
            }

            try
            {
                var addresses = Dns.GetHostAddresses(host);

                return addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new IPEndPoint(a, port)).ToArray();
            }
            catch (SocketException ex)
            {
                QuicPunchLog.Error($"DNS lookup failed for '{host}'", ex);
                return null;
            }
            catch (Exception ex)
            {
                QuicPunchLog.Error($"Failed to resolve '{host}'", ex);
                return null;
            }
        }

        public static QuicPunch.NetworkType GetNetworkType(IEnumerable<KeyValuePair<IPEndPoint, int>> stunsHits)
        {
            var hitsList = stunsHits as IReadOnlyCollection<KeyValuePair<IPEndPoint, int>> ?? stunsHits.ToList();
            if (hitsList.Count == 0)
                return QuicPunch.NetworkType.Unknown;

            var hitsByIp = stunsHits
                .GroupBy(x => x.Key.Address)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.Value)
                ).OrderByDescending(e => e.Value).ToArray();

            var biggestAddressHits = hitsByIp[0].Value;
            var otherAddressHits = hitsByIp.Skip(1).Sum(e => e.Value);
            double addressRatio = (double)biggestAddressHits / (biggestAddressHits + otherAddressHits);

            var hitsByPort = stunsHits
                .GroupBy(x => x.Key.Port)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.Value)
                ).OrderByDescending(e => e.Value).ToArray();

            var biggestPortHits = hitsByPort[0].Value;
            var otherPortHits = hitsByPort.Skip(1).Sum(e => e.Value);
            double portRatio = (double)biggestPortHits / (biggestPortHits + otherPortHits);

            if (addressRatio > 0.99 && portRatio > 0.99)
            {
                return QuicPunch.NetworkType.Static;
            }
            else if (addressRatio > 0.99 && portRatio < 0.99)
            {
                return QuicPunch.NetworkType.DynamicPort;
            }
            else if (addressRatio < 0.99 && portRatio > 0.99)
            {
                return QuicPunch.NetworkType.DynamicAddress;
            }

            return QuicPunch.NetworkType.DynamicPortAndAddress;
        }

        public static string Base32Encode(ReadOnlySpan<byte> data)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var output = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);

            int buffer = 0;
            int bitsLeft = 0;

            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;

                while (bitsLeft >= 5)
                {
                    bitsLeft -= 5;
                    output.Append(alphabet[(buffer >> bitsLeft) & 31]);
                }
            }

            if (bitsLeft > 0)
                output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);

            return output.ToString();
        }

        public static byte[] Base32Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return Array.Empty<byte>();
            input = input.Trim().ToUpperInvariant();
            if (input.EndsWith(".ONION")) input = input[..^6];

            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var bytes = new List<byte>();
            int buffer = 0;
            int bitsLeft = 0;

            foreach (char c in input)
            {
                int val = alphabet.IndexOf(c);
                if (val < 0) continue;

                buffer = (buffer << 5) | val;
                bitsLeft += 5;

                if (bitsLeft >= 8)
                {
                    bitsLeft -= 8;
                    bytes.Add((byte)(buffer >> bitsLeft));
                }
            }

            return bytes.ToArray();
        }

        private const byte TokenVersionByte = 1;
        public static string EncodeEndpointToken(PeerInfo p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p), "PeerInfo cannot be null when encoding endpoint token.");

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                PackedFlags pf = new PackedFlags()
                {
                    NetworkType = p.NetworkType
                };

                w.Write((byte)pf.RawValue);

                if (p.NetworkType == QuicPunch.NetworkType.Tor)
                {
                    if (string.IsNullOrWhiteSpace(p.OnionAddress))
                        throw new ArgumentException("OnionAddress cannot be null or empty for Tor network type.", nameof(p));

                    byte[] rawOnion = Base32Decode(p.OnionAddress);
                    if (rawOnion.Length != 35)
                        throw new ArgumentException($"Invalid Tor v3 onion address '{p.OnionAddress}': decoded length must be exactly 35 bytes (got {rawOnion.Length}).", nameof(p));

                    if (p.CertHash == null || p.CertHash.Length != 32)
                        throw new ArgumentException($"Invalid CertHash: must be exactly 32 bytes (got {p.CertHash?.Length ?? 0}).", nameof(p));

                    w.Write(rawOnion);
                    w.Write((ushort)(p.MinPort > 0 ? p.MinPort : 443));
                    w.Write(p.CertHash);
                    return Base64Url.EncodeToString(ms.ToArray());
                }

                var addresses = p.Addresses ?? Array.Empty<IPAddress>();

                if (p.NetworkType == QuicPunch.NetworkType.DynamicAddress || p.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    w.Write((byte)addresses.Length);

                    for (int i = 0; i < addresses.Length; i++)
                    {
                        w.Write(addresses[i].GetAddressBytes());
                    }
                }
                else
                {
                    var primaryAddress = addresses.Length > 0 ? addresses[0] : IPAddress.Any;
                    w.Write(primaryAddress.GetAddressBytes());
                }

                if (pf.NetworkType == QuicPunch.NetworkType.DynamicPort || pf.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    w.Write((short)p.MinPort);
                    w.Write((short)p.MaxPort);
                }
                else
                {
                    w.Write((ushort)p.MinPort);
                }

                var localIps = GetValidLocalIPAddresses().Where(ip => !addresses.Contains(ip)).ToList();
                w.Write((byte)localIps.Count);
                for (int i = 0; i < localIps.Count; i++)
                {
                    w.Write(localIps[i].GetAddressBytes());
                }

                byte[] certHash = p.CertHash ?? Array.Empty<byte>();
                w.Write(certHash);

                return Base64Url.EncodeToString(ms.ToArray());
            }
        }

        public static PeerInfo DecodeEndpointToken(string t)
        {
            if (string.IsNullOrWhiteSpace(t))
                throw new ArgumentNullException(nameof(t), "Token cannot be null or empty.");

            t = t.Trim();

            if (t.Contains("protred?uri=", StringComparison.OrdinalIgnoreCase))
            {
                var uriParam = t.Substring(t.IndexOf("protred?uri=", StringComparison.OrdinalIgnoreCase) + 12);
                t = System.Web.HttpUtility.UrlDecode(System.Web.HttpUtility.UrlDecode(uriParam));
            }

            if (t.StartsWith("qp://", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(5);
            else if (t.StartsWith("QPHP://", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(7);
            else if (t.Contains("://"))
                t = t.Substring(t.IndexOf("://") + 3);

            t = t.Trim('/');

            if (t.EndsWith(".onion", StringComparison.OrdinalIgnoreCase) || t.Contains(".onion:"))
            {
                var onionPeer = new PeerInfo { NetworkType = QuicPunch.NetworkType.Tor };
                var parts = t.Split(':');
                onionPeer.OnionAddress = parts[0];
                int port = 443;
                if (parts.Length > 1 && int.TryParse(parts[1], out int p)) port = p;
                onionPeer.MinPort = onionPeer.MaxPort = port;
                return onionPeer;
            }

            var peer = new PeerInfo();

            using (var ms = new MemoryStream(Base64Url.DecodeFromChars(t)))
            using (var r = new BinaryReader(ms))
            {
                PackedFlags pf = new PackedFlags(r.ReadByte());
                peer.NetworkType = pf.NetworkType;

                if (pf.NetworkType == QuicPunch.NetworkType.Tor)
                {
                    byte[] rawOnion = r.ReadBytes(35);
                    if (rawOnion.Length != 35)
                        throw new InvalidDataException($"Truncated Tor token: expected 35 bytes for onion address, got {rawOnion.Length}.");

                    peer.OnionAddress = Base32Encode(rawOnion).ToLowerInvariant() + ".onion";
                    peer.MinPort = peer.MaxPort = r.ReadUInt16();

                    var cHash = r.ReadBytes(256 / 8);
                    if (cHash.Length != 32)
                        throw new InvalidDataException($"Truncated Tor token: expected 32 bytes for certificate hash, got {cHash.Length}.");

                    peer.SetCertificateHash(cHash);
                    return peer;
                }

                var allAddresses = new List<IPAddress>();

                if (pf.NetworkType == QuicPunch.NetworkType.DynamicAddress || pf.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    var addressesLength = r.ReadByte();
                    if (addressesLength > 32)
                        throw new InvalidDataException($"Token address count exceeds maximum allowed ({addressesLength} > 32).");

                    for (int i = 0; i < addressesLength; i++)
                    {
                        var ip = new IPAddress(r.ReadBytes(4));
                        if (!IsValidPeerAddress(ip))
                            throw new InvalidDataException($"Invalid, reserved or dangerous peer address '{ip}'.");
                        allAddresses.Add(ip);
                    }
                }
                else
                {
                    var ip = new IPAddress(r.ReadBytes(4));
                    if (!IsValidPeerAddress(ip))
                        throw new InvalidDataException($"Invalid, reserved or dangerous peer address '{ip}'.");
                    allAddresses.Add(ip);
                }

                if (pf.NetworkType == QuicPunch.NetworkType.DynamicPort || pf.NetworkType == QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    peer.MinPort = r.ReadUInt16();
                    peer.MaxPort = r.ReadUInt16();
                }
                else
                {
                    peer.MinPort = peer.MaxPort = r.ReadUInt16();
                }

                if (r.BaseStream.Position < r.BaseStream.Length - (256 / 8))
                {
                    var localIpCount = r.ReadByte();
                    if (localIpCount > 32)
                        throw new InvalidDataException($"Token local IP count exceeds maximum allowed ({localIpCount} > 32).");

                    for (int i = 0; i < localIpCount; i++)
                    {
                        var localIp = new IPAddress(r.ReadBytes(4));
                        if (!IsValidPeerAddress(localIp))
                            throw new InvalidDataException($"Invalid, reserved or dangerous peer local address '{localIp}'.");

                        if (!allAddresses.Contains(localIp))
                        {
                            allAddresses.Add(localIp);
                        }
                    }
                }

                var certHash = r.ReadBytes(256 / 8);
                peer.SetCertificateHash(certHash);

                peer.Addresses = allAddresses.ToArray();

                return peer;
            }
        }
        public sealed class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public static ByteArrayComparer Instance { get; } = new();

            private ByteArrayComparer() { }

            public bool Equals(byte[]? x, byte[]? y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x is null || y is null) return false;
                return CryptographicOperations.FixedTimeEquals(x, y);
            }

            public int GetHashCode(byte[]? obj)
            {
                if (obj is null) return 0;
                var hash = new HashCode();

                foreach (byte b in obj)
                    hash.Add(b);

                return hash.ToHashCode();
            }
        }
    }
}
