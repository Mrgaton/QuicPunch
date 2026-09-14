using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
        public static bool TryIpToUint(IPAddress? ip, out uint result)
        {
            result = 0;
            if (ip == null) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;

            Span<byte> bytes = stackalloc byte[4];
            if (!ip.TryWriteBytes(bytes, out int written) || written != 4)
                return false;

            result = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint IpToUint(IPAddress ip)
        {
            if (TryIpToUint(ip, out uint result))
                return result;

            throw new ArgumentException("IPv4 only", nameof(ip));
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
            if (peerInfo.ActiveEndPoint is { } active &&
                IsValidPeerAddress(active.Address) && active.Port is >= 1 and <= 65535)
            {
                try
                {
                    await udp.SendAsync(data, active).ConfigureAwait(false);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                return;
            }

            const int MaxEndpointsPerBurst = 32;
            var targets = new HashSet<IPEndPoint>();
            var addresses = (peerInfo.Addresses ?? Array.Empty<IPAddress>())
                .Where(IsValidPeerAddress)
                .Distinct()
                .Take(32)
                .ToArray();

            if (addresses.Length > 0 && targets.Count < MaxEndpointsPerBurst)
            {
                int portSpan = peerInfo.PortArray.Length;
                long exhaustiveCount = (long)addresses.Length * portSpan;

                if (exhaustiveCount + targets.Count <= MaxEndpointsPerBurst)
                {
                    foreach (var address in addresses)
                    {
                        foreach (int port in peerInfo.PortArray)
                            targets.Add(new IPEndPoint(address, port));
                    }
                }
                else
                {
                    foreach (var address in addresses)
                    {
                        if (targets.Count >= MaxEndpointsPerBurst) break;
                        int port = peerInfo.PortArray[Random.Shared.Next(peerInfo.PortArray.Length)];
                        targets.Add(new IPEndPoint(address, port));
                    }

                    int attempts = 0;
                    while (targets.Count < MaxEndpointsPerBurst && attempts++ < MaxEndpointsPerBurst * 8)
                    {
                        var address = addresses[Random.Shared.Next(addresses.Length)];
                        int port = peerInfo.PortArray[Random.Shared.Next(peerInfo.PortArray.Length)];
                        targets.Add(new IPEndPoint(address, port));
                    }
                }
            }

            foreach (var target in targets)
            {
                try
                {
                    await udp.SendAsync(data, target).ConfigureAwait(false);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
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
            port = 3478;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            line = line.Trim();

            if (line.StartsWith("stun:", StringComparison.OrdinalIgnoreCase))
                line = line[5..].Trim();
            else if (line.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
                line = line[5..].Trim();

            if (string.IsNullOrWhiteSpace(line))
                return false;

            if (line.StartsWith('['))
            {
                int closing = line.IndexOf(']');
                if (closing > 1)
                {
                    host = line[1..closing].Trim();
                    if (closing + 1 < line.Length && line[closing + 1] == ':')
                    {
                        if (int.TryParse(line[(closing + 2)..].Trim(), out int parsedPort) && parsedPort is >= 1 and <= 65535)
                        {
                            port = parsedPort;
                            return true;
                        }
                        return false;
                    }
                    port = 3478;
                    return true;
                }
                return false;
            }

            int colon = line.LastIndexOf(':');
            if (colon <= 0 || colon == line.Length - 1)
            {
                if (colon == -1)
                {
                    host = line;
                    port = 3478;
                    return true;
                }
                return false;
            }

            host = line[..colon].Trim();
            if (int.TryParse(line[(colon + 1)..].Trim(), out int p) && p is >= 1 and <= 65535)
            {
                port = p;
                return true;
            }
            return false;
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
            catch
            {
                return null;
            }
        }

        public static ConnectionFlags GetConnectionFlags(IEnumerable<IPEndPoint> stunResponses, int originalPort)
        {
            var cf = new ConnectionFlags();
            var list = stunResponses?.ToList();
            if (list == null || list.Count == 0)
                return cf;

            int total = list.Count;

            var ipGroups = list.GroupBy(x => x.Address)
                               .Select(g => g.Count())
                               .OrderByDescending(c => c)
                               .ToArray();

            cf.MultipleIps = ipGroups.Length > 1 && ((double)ipGroups[0] / total) < 0.95;

            var portGroups = list.GroupBy(x => x.Port)
                                 .Select(g => new { Port = g.Key, Hits = g.Count() })
                                 .OrderByDescending(x => x.Hits)
                                 .ToArray();

            int dominantPort = portGroups[0].Port;
            double dominantRatio = (double)portGroups[0].Hits / total;
            double avgHits = (double)total / portGroups.Length;

            if (portGroups.Length == 1 || dominantRatio >= 0.95)
                cf.PortMode = PortMode.Single;
            else if (portGroups.Length <= 8 && portGroups[^1].Hits >= avgHits * 0.40)
                cf.PortMode = PortMode.Multiple;
            else
                cf.PortMode = PortMode.Range;

            cf.IsNat = cf.MultipleIps || cf.PortMode != PortMode.Single || (originalPort > 0 && dominantPort != originalPort);

            return cf;
        }

        public const int TorTokenBinaryLength = 70; // 1 byte flags + 35 bytes onion raw + 2 bytes port + 32 bytes cert hash
        public const int EndpointTokenSignatureLength = 64;

        public static string Base32Encode(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return string.Empty;
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            int outputLength = (data.Length * 8 + 4) / 5;
            Span<char> chars = outputLength <= 128 ? stackalloc char[outputLength] : new char[outputLength];

            int buffer = 0;
            int bitsLeft = 0;
            int charIdx = 0;

            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;

                while (bitsLeft >= 5)
                {
                    bitsLeft -= 5;
                    chars[charIdx++] = alphabet[(buffer >> bitsLeft) & 31];
                }
            }

            if (bitsLeft > 0)
            {
                chars[charIdx++] = alphabet[(buffer << (5 - bitsLeft)) & 31];
            }

            return new string(chars[..charIdx]);
        }

        public static bool TryBase32Decode(ReadOnlySpan<char> input, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            input = input.Trim();
            if (input.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
                input = input[..^6];

            if (input.IsEmpty) return false;

            int buffer = 0;
            int bitsLeft = 0;
            int written = 0;

            foreach (char c in input)
            {
                int val = c switch
                {
                    >= 'a' and <= 'z' => c - 'a',
                    >= 'A' and <= 'Z' => c - 'A',
                    >= '2' and <= '7' => c - '2' + 26,
                    _ => -1
                };

                if (val < 0) return false;

                buffer = (buffer << 5) | val;
                bitsLeft += 5;

                if (bitsLeft >= 8)
                {
                    bitsLeft -= 8;
                    if (written >= destination.Length) return false;
                    destination[written++] = (byte)(buffer >> bitsLeft);
                }
            }

            bytesWritten = written;
            return true;
        }

        public static byte[] Base32Decode(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return Array.Empty<byte>();
            Span<byte> dest = stackalloc byte[(input.Length * 5) / 8 + 1];
            if (TryBase32Decode(input.AsSpan(), dest, out int written))
                return dest[..written].ToArray();
            return Array.Empty<byte>();
        }

        private const int MaxEndpointTokenAddresses = 32;
        private const int EndpointTokenCertificateHashLength = 32;
        private const int MaxEndpointTokenBytes = 4096;

        public static string EncodeEndpointToken(PeerInfo p)
        {
            if (p == null)
                throw new ArgumentNullException(nameof(p), "PeerInfo cannot be null when encoding endpoint token.");

            if (p.ConnectionFlags.IsTor || p.NetworkType == QuicPunch.NetworkType.Tor)
            {
                if (string.IsNullOrWhiteSpace(p.OnionAddress))
                    throw new ArgumentException("OnionAddress cannot be null or empty for Tor network type.", nameof(p));

                if (!p.TryGetCertificateHash(out var torCertHash) || torCertHash.Length != EndpointTokenCertificateHashLength)
                    throw new ArgumentException("Peer certificate hash must be exactly 32 bytes.", nameof(p));

                int torPort = p.PortArray?.Length > 0 ? p.PortArray[0] : 443;
                if (torPort is < 1 or > 65535)
                    throw new ArgumentOutOfRangeException(nameof(p), "Tor port must be in the range 1..65535.");

                Span<byte> tokenBytes = stackalloc byte[TorTokenBinaryLength];
                tokenBytes[0] = new ConnectionFlags { IsTor = true }.RawValue;

                if (!TryBase32Decode(p.OnionAddress.AsSpan(), tokenBytes.Slice(1, 35), out int decodedLen) || decodedLen != 35)
                    throw new ArgumentException($"Invalid Tor v3 onion address '{p.OnionAddress}': expected 35 decoded bytes (56 base32 characters).", nameof(p));

                BinaryPrimitives.WriteUInt16LittleEndian(tokenBytes.Slice(36, 2), (ushort)torPort);
                torCertHash.CopyTo(tokenBytes.Slice(38, EndpointTokenCertificateHashLength));

                return Base64Url.EncodeToString(tokenBytes);
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            var rawAddresses = p.Addresses ?? Array.Empty<IPAddress>();
            var addresses = rawAddresses.Length > 0
                ? rawAddresses.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || a.IsIPv4MappedToIPv6).Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Take(MaxEndpointTokenAddresses).ToList()
                : GetValidLocalIPAddresses().Take(MaxEndpointTokenAddresses).ToList();

            if (addresses.Count == 0)
                throw new InvalidOperationException("Peer has no valid IPv4 address to publish in an endpoint token.");

            ushort[] portArray = p.PortArray?.Length > 0 ? p.PortArray : [];

            if (portArray.Length == 0)
                throw new InvalidOperationException("Peer endpoint token requires at least one valid port.");

            p.PortArray = portArray;
            bool multipleAddresses = addresses.Count > 1;
            p.ConnectionFlags.MultipleIps = multipleAddresses;
            if (portArray.Length <= 1)
            {
                p.ConnectionFlags.PortMode = PortMode.Single;
            }
            else if (p.ConnectionFlags.PortMode == PortMode.Single || (p.ConnectionFlags.PortMode == PortMode.Multiple && portArray.Length > 255))
            {
                bool isContiguous = true;
                for (int i = 1; i < portArray.Length; i++)
                {
                    if (portArray[i] != portArray[i - 1] + 1)
                    {
                        isContiguous = false;
                        break;
                    }
                }
                p.ConnectionFlags.PortMode = (isContiguous || portArray.Length > 8) ? PortMode.Range : PortMode.Multiple;
            }

            w.Write(p.ConnectionFlags.RawValue);

            if (multipleAddresses)
            {
                w.Write((byte)addresses.Count);
                foreach (IPAddress address in addresses)
                    w.Write(address.GetAddressBytes());
            }
            else
            {
                w.Write(addresses[0].GetAddressBytes());
            }

            switch (p.ConnectionFlags.PortMode)
            {
                case PortMode.Single:
                    w.Write((ushort)portArray[0]);
                    break;
                case PortMode.Multiple:
                    if (portArray.Length > 255)
                        throw new InvalidOperationException($"Too many ports ({portArray.Length}) for Multiple port mode. Maximum is 255.");
                    w.Write((byte)portArray.Length);
                    foreach (ushort port in portArray)
                        w.Write((ushort)port);
                    break;
                case PortMode.Range:
                    w.Write((ushort)portArray.Min());
                    w.Write((ushort)portArray.Max());
                    break;
            }

            if (!p.TryGetCertificateHash(out var certHash) || certHash.Length != EndpointTokenCertificateHashLength)
                throw new ArgumentException("Peer certificate hash must be exactly 32 bytes.", nameof(p));

            w.Write(certHash);

            return Base64Url.EncodeToString(ms.ToArray());
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
                var onionPeer = new PeerInfo
                {
                    NetworkType = QuicPunch.NetworkType.Tor,
                    ConnectionFlags = new ConnectionFlags { IsTor = true }
                };
                var parts = t.Split(':');
                onionPeer.OnionAddress = parts[0];
                ushort port = 443;
                if (parts.Length > 1 && ushort.TryParse(parts[1], out ushort parsedPort)) port = parsedPort;
                if (port is < 1 or > 65535)
                    throw new InvalidDataException("Tor endpoint port must be in the range 1..65535.");
                onionPeer.PortArray = [port];
                return onionPeer;
            }

            byte[] rawToken = Base64Url.DecodeFromChars(t);
            if (rawToken.Length == 0 || rawToken.Length > MaxEndpointTokenBytes)
                throw new InvalidDataException($"Endpoint token size is invalid ({rawToken.Length} bytes).");

            var flags = new ConnectionFlags(rawToken[0]);
            bool isTor = flags.IsTor || (rawToken.Length == TorTokenBinaryLength && (rawToken[0] & 0b111) == (byte)QuicPunch.NetworkType.Tor);
            if (isTor)
            {
                if (rawToken.Length != TorTokenBinaryLength)
                    throw new InvalidDataException($"Invalid Tor endpoint token size: expected {TorTokenBinaryLength} bytes, got {rawToken.Length}.");

                ReadOnlySpan<byte> span = rawToken.AsSpan();
                var torPeer = new PeerInfo
                {
                    NetworkType = QuicPunch.NetworkType.Tor,
                    ConnectionFlags = new ConnectionFlags { IsTor = true }
                };

                ReadOnlySpan<byte> rawOnion = span.Slice(1, 35);
                torPeer.OnionAddress = Base32Encode(rawOnion).ToLowerInvariant() + ".onion";

                ushort port = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(36, 2));
                if (port == 0)
                    throw new InvalidDataException("Tor endpoint token contains port 0.");
                torPeer.PortArray = [port];

                byte[] torHash = span.Slice(38, EndpointTokenCertificateHashLength).ToArray();
                torPeer.SetCertificateHash(torHash);

                return torPeer;
            }

            try
            {
                return DecodeModernWanEndpointToken(rawToken, flags);
            }
            catch (Exception) when (TryDecodeLegacyWanToken(rawToken, out var legacyPeer) && legacyPeer != null)
            {
                return legacyPeer;
            }
        }

        private static PeerInfo DecodeModernWanEndpointToken(ReadOnlySpan<byte> span, ConnectionFlags flags)
        {
            const int MinWanTokenLength = 1 + 4 + 2 + EndpointTokenCertificateHashLength;
            if (span.Length < MinWanTokenLength)
                throw new InvalidDataException($"WAN endpoint token size is too short ({span.Length} < {MinWanTokenLength} bytes).");

            var peer = new PeerInfo
            {
                ConnectionFlags = flags,
                NetworkType = QuicPunch.NetworkType.Static
            };

            try
            {
                int offset = 1;

                var allAddresses = new List<IPAddress>();
                int addressCount = flags.MultipleIps ? span[offset++] : 1;

                if (addressCount is < 1 or > MaxEndpointTokenAddresses)
                    throw new InvalidDataException($"Token address count exceeds maximum allowed ({addressCount}).");

                for (int i = 0; i < addressCount; i++)
                {
                    if (offset + 4 > span.Length)
                        throw new InvalidDataException("Truncated peer address in endpoint token.");

                    var ip = new IPAddress(span.Slice(offset, 4));
                    offset += 4;

                    if (!IsValidPeerAddress(ip))
                        throw new InvalidDataException($"Invalid, reserved or dangerous peer address '{ip}'.");

                    if (!allAddresses.Contains(ip))
                        allAddresses.Add(ip);
                }

                switch (flags.PortMode)
                {
                    case PortMode.Single:
                        if (offset + 2 > span.Length)
                            throw new InvalidDataException("Truncated port in endpoint token.");
                        peer.PortArray = [BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2))];
                        offset += 2;
                        break;

                    case PortMode.Range:
                        if (offset + 4 > span.Length)
                            throw new InvalidDataException("Truncated port range in endpoint token.");
                        var minPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                        offset += 2;
                        var maxPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                        offset += 2;
                        if (minPort == 0 || minPort > maxPort)
                            throw new InvalidDataException("Invalid port range in endpoint token.");
                        peer.PortArray = Enumerable.Range(minPort, maxPort - minPort + 1).Select(p => (ushort)p).ToArray();
                        break;

                    case PortMode.Multiple:
                        if (offset + 1 > span.Length)
                            throw new InvalidDataException("Truncated port count in endpoint token.");
                        int portCount = span[offset++];
                        if (offset + (portCount * 2) > span.Length)
                            throw new InvalidDataException("Truncated port array in endpoint token.");

                        ushort[] ports = new ushort[portCount];
                        for (int i = 0; i < portCount; i++)
                        {
                            ports[i] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                            offset += 2;
                        }
                        peer.PortArray = ports;
                        break;

                    default:
                        throw new InvalidDataException("Invalid port mode in endpoint token.");
                }

                bool hasValidPorts = (peer.PortArray != null && peer.PortArray.Length > 0);

                if (!hasValidPorts)
                    throw new InvalidDataException("Endpoint token contains an invalid port configuration.");

                if (offset + EndpointTokenCertificateHashLength != span.Length)
                    throw new InvalidDataException("Endpoint token format mismatch or contains unexpected trailing data.");

                byte[] certHash = span.Slice(offset, EndpointTokenCertificateHashLength).ToArray();
                offset += EndpointTokenCertificateHashLength;

                peer.SetCertificateHash(certHash);
                peer.Addresses = allAddresses.ToArray();
                return peer;
            }
            catch
            {
                peer.Dispose();
                throw;
            }
        }

        private static bool TryDecodeLegacyWanToken(ReadOnlySpan<byte> span, out PeerInfo? peer)
        {
            peer = null;
            const int MinWanTokenLength = 1 + 4 + 2 + EndpointTokenCertificateHashLength;
            if (span.Length < MinWanTokenLength)
                return false;

            try
            {
                byte flagByte = span[0];
                int netType = flagByte & 0b111;
                if (netType > 3)
                    return false;

                int offset = 1;
                var allAddresses = new List<IPAddress>();

                if (netType == 2 || netType == 3)
                {
                    if (offset >= span.Length) return false;
                    int addrCount = span[offset++];
                    if (addrCount < 1 || addrCount > 32) return false;
                    for (int i = 0; i < addrCount; i++)
                    {
                        if (offset + 4 > span.Length) return false;
                        var ip = new IPAddress(span.Slice(offset, 4));
                        offset += 4;
                        if (!IsValidPeerAddress(ip)) return false;
                        if (!allAddresses.Contains(ip)) allAddresses.Add(ip);
                    }
                }
                else
                {
                    if (offset + 4 > span.Length) return false;
                    var ip = new IPAddress(span.Slice(offset, 4));
                    offset += 4;
                    if (!IsValidPeerAddress(ip)) return false;
                    allAddresses.Add(ip);
                }

                ushort minPort;
                ushort maxPort;
                if (netType == 1 || netType == 3)
                {
                    if (offset + 4 > span.Length) return false;
                    minPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                    maxPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                }
                else
                {
                    if (offset + 2 > span.Length) return false;
                    minPort = maxPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                }

                if (minPort == 0 || minPort > maxPort)
                    return false;

                if (offset < span.Length - EndpointTokenCertificateHashLength)
                {
                    int localIpCount = span[offset++];
                    if (localIpCount > 32) return false;
                    for (int i = 0; i < localIpCount; i++)
                    {
                        if (offset + 4 > span.Length) return false;
                        var localIp = new IPAddress(span.Slice(offset, 4));
                        offset += 4;
                        if (IsValidPeerAddress(localIp) && !allAddresses.Contains(localIp))
                        {
                            allAddresses.Add(localIp);
                        }
                    }
                }

                if (offset + EndpointTokenCertificateHashLength != span.Length)
                    return false;

                byte[] certHash = span.Slice(offset, EndpointTokenCertificateHashLength).ToArray();

                var legacyPeer = new PeerInfo
                {
                    Addresses = allAddresses.ToArray(),
                    PortArray = minPort == maxPort
                        ? [(ushort)minPort]
                        : Enumerable.Range(minPort, maxPort - minPort + 1).Select(p => (ushort)p).ToArray(),
                    NetworkType = QuicPunch.NetworkType.Static
                };
                legacyPeer.ConnectionFlags.PortMode = minPort == maxPort ? PortMode.Single : PortMode.Range;
                legacyPeer.ConnectionFlags.MultipleIps = allAddresses.Count > 1;
                legacyPeer.SetCertificateHash(certHash);
                peer = legacyPeer;
                return true;
            }
            catch
            {
                peer?.Dispose();
                peer = null;
                return false;
            }
        }

        public static byte[] SignToken(string token, X509Certificate2? cert)
        {
            if (string.IsNullOrWhiteSpace(token) || cert == null)
                return Array.Empty<byte>();

            using var ecdsa = cert.GetECDsaPrivateKey();
            if (ecdsa == null)
                return Array.Empty<byte>();

            byte[] dataToSign = Encoding.UTF8.GetBytes(token.Trim());
            return ecdsa.SignData(dataToSign, HashAlgorithmName.SHA256);
        }

        public static bool TryCreateECDsaFromPublicKey(ReadOnlySpan<byte> publicKeyBytes, out ECDsa? ecdsa)
        {
            ecdsa = null;
            try
            {
                if (publicKeyBytes.Length == 65 && publicKeyBytes[0] == 0x04)
                {
                    var parameters = new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint
                        {
                            X = publicKeyBytes.Slice(1, 32).ToArray(),
                            Y = publicKeyBytes.Slice(33, 32).ToArray()
                        }
                    };
                    ecdsa = ECDsa.Create(parameters);
                    return true;
                }

                var e = ECDsa.Create();
                e.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                ecdsa = e;
                return true;
            }
            catch
            {
                ecdsa?.Dispose();
                ecdsa = null;
                return false;
            }
        }

        public static bool TryVerifyTokenCertificate(string token, ReadOnlySpan<byte> certPublicKey, ReadOnlySpan<byte> signature, out byte[]? certHash)
        {
            certHash = null;
            if (string.IsNullOrWhiteSpace(token) || certPublicKey.IsEmpty || signature.IsEmpty)
                return false;

            try
            {
                using var decodedPeer = DecodeEndpointToken(token);
                if (!decodedPeer.TryGetCertificateHash(out var expectedHash))
                    return false;

                byte[] computedHash = SHA3_256.HashData(certPublicKey);
                if (!CryptographicOperations.FixedTimeEquals(computedHash, expectedHash))
                    return false;

                certHash = computedHash;

                if (!TryCreateECDsaFromPublicKey(certPublicKey, out var ecdsa) || ecdsa == null)
                    return false;

                using (ecdsa)
                {
                    byte[] dataToVerify = Encoding.UTF8.GetBytes(token.Trim());
                    return ecdsa.VerifyData(dataToVerify, signature, HashAlgorithmName.SHA256);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ToCanonicalPeerId(byte[]? certHash)
        {
            if (certHash == null || certHash.Length == 0) return "";
            return Convert.ToHexString(certHash)[..Math.Min(16, Convert.ToHexString(certHash).Length)].ToLowerInvariant();
        }

        public static string ToCanonicalPeerId(string? base64OrHex)
        {
            if (string.IsNullOrWhiteSpace(base64OrHex)) return "";
            try
            {
                if (base64OrHex.Length == 64 && base64OrHex.All(Uri.IsHexDigit))
                    return base64OrHex[..16].ToLowerInvariant();

                if (base64OrHex.Length == 16 && base64OrHex.All(Uri.IsHexDigit))
                    return base64OrHex.ToLowerInvariant();

                if (Guid.TryParse(base64OrHex, out var guid))
                    return guid.ToString("N")[..16].ToLowerInvariant();

                var bytes = Convert.FromBase64String(base64OrHex);
                return ToCanonicalPeerId(bytes);
            }
            catch
            {
                return base64OrHex.Length >= 16 ? base64OrHex[..16].ToLowerInvariant() : base64OrHex.ToLowerInvariant();
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

        private static readonly string[] IdentityAdjectives =
        [
            "swift", "bold", "quiet", "silent", "shadow", "solar", "lunar", "cyber", "amber", "frost",
            "nova", "iron", "storm", "bright", "mystic", "zenith", "vector", "quantum", "hyper", "alpha",
            "cosmic", "delta", "echo", "pixel", "phantom", "rapid", "silver", "vivid", "wild", "zephyr",
            "astral", "blaze", "crimson", "drift", "emerald", "flare", "glacier", "horizon", "indigo", "jade",
            "kinetic", "laser", "matrix", "nexus", "orbit", "prism", "quasar", "radiant", "sonic", "vortex"
        ];

        private static readonly string[] IdentityNouns =
        [
            "fox", "wolf", "hawk", "eagle", "bear", "lion", "runner", "pilot", "ranger", "voyager",
            "seeker", "driver", "scout", "falcon", "tiger", "dragon", "phoenix", "rider", "walker", "coder",
            "spark", "pulse", "tracer", "weaver", "hunter", "drifter", "keeper", "cadet", "sentinel", "operator",
            "beacon", "cipher", "daemon", "engine", "flux", "guardian", "harbor", "matrix", "navigator", "orbit",
            "pioneer", "rover", "signal", "titan", "vector", "warden", "zenith", "specter", "strider", "vanguard"
        ];

        private static readonly string[] HostnamePrefixes =
        [
            "DESKTOP", "LAPTOP", "PC", "NODE", "HOST", "WORKSTATION"
        ];

        public static string GenerateRandomHostname()
        {
            string prefix = HostnamePrefixes[RandomNumberGenerator.GetInt32(HostnamePrefixes.Length)];
            const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            Span<char> suffix = stackalloc char[7];
            for (int i = 0; i < suffix.Length; i++)
            {
                suffix[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
            return $"{prefix}-{new string(suffix)}";
        }

        public static string GenerateRandomUsername()
        {
            string adj = IdentityAdjectives[RandomNumberGenerator.GetInt32(IdentityAdjectives.Length)];
            string noun = IdentityNouns[RandomNumberGenerator.GetInt32(IdentityNouns.Length)];
            int num = RandomNumberGenerator.GetInt32(10, 100);
            return $"{adj}_{noun}{num}";
        }

        public static (string Username, string Hostname, string FullName) GenerateRandomIdentityNames()
        {
            string username = GenerateRandomUsername();
            string hostname = GenerateRandomHostname();
            return (username, hostname, $"{username}@{hostname}");
        }

        public static bool PeerTargetsMatch(PeerInfo a, PeerInfo b)
        {
            bool aHasIdentity = a.TryGetCertificateHash(out var aHash);
            bool bHasIdentity = b.TryGetCertificateHash(out var bHash);
            if (aHasIdentity && bHasIdentity)
            {
                // Once both sides have identities, endpoint reuse must never make two
                // different peers compare equal. Endpoint matching is only a bridge
                // while at least one side is still an unauthenticated candidate.
                return CryptographicOperations.FixedTimeEquals(aHash, bHash);
            }

            if (!string.IsNullOrWhiteSpace(a.OnionAddress) && !string.IsNullOrWhiteSpace(b.OnionAddress) &&
                string.Equals(a.OnionAddress, b.OnionAddress, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (a.ActiveEndPoint != null && b.ActiveEndPoint != null && a.ActiveEndPoint.Equals(b.ActiveEndPoint))
                return true;

            if (a.ActiveEndPoint != null && EndpointMatchesPeer(a.ActiveEndPoint, b))
                return true;
            if (b.ActiveEndPoint != null && EndpointMatchesPeer(b.ActiveEndPoint, a))
                return true;

            if (a.Addresses != null && b.Addresses != null && a.Addresses.Intersect(b.Addresses).Any())
            {
                if (a.PortArray != null && b.PortArray != null && a.PortArray.Intersect(b.PortArray).Any())
                    return true;
            }

            return false;
        }

        public static bool EndpointMatchesPeer(IPEndPoint endpoint, PeerInfo peer)
        {
            if (peer.ActiveEndPoint != null && peer.ActiveEndPoint.Equals(endpoint))
                return true;

            if (peer.Addresses == null || !peer.Addresses.Contains(endpoint.Address))
                return false;

            return peer.PortArray != null && peer.PortArray.Contains((ushort)endpoint.Port);
        }
    }
}
