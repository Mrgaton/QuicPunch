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

        internal static bool IsPrivateLanAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254);
        }

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
                        g.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);

                    bool isPhysical = ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                     ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                     (OperatingSystem.IsAndroid() &&
                                      (name.StartsWith("wlan", StringComparison.Ordinal) ||
                                       name.StartsWith("rmnet", StringComparison.Ordinal) ||
                                       name.StartsWith("eth", StringComparison.Ordinal)));

                    foreach (var unicast in props.UnicastAddresses)
                    {
                        var ip = unicast.Address;
                        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

                        if (EndpointAddressCodec.CanAdvertise(ip) && !IPAddress.IsLoopback(ip))
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
            if (list.Count == 0 && OperatingSystem.IsAndroid())
            {
                // Android often omits gateways and reports unusual interface types to
                // NetworkInterface. Keep the actual Wi-Fi/cellular addresses available
                // for host candidates instead of advertising a loopback placeholder.
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                        .OrderBy(n => n.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                    {
                        string name = ni.Name.ToLowerInvariant();
                        if (name.StartsWith("lo") || name.StartsWith("tun") || name.StartsWith("vgate") ||
                            name.StartsWith("ppp") || name.StartsWith("dummy"))
                            continue;
                        foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                        {
                            IPAddress ip = unicast.Address;
                            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                            if (EndpointAddressCodec.CanAdvertise(ip) && !IPAddress.IsLoopback(ip) &&
                                !list.Contains(ip))
                                list.Add(ip);
                        }
                    }
                }
                catch { }
            }
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

        public static Task BigSendAsync(this UdpClient udp, ReadOnlyMemory<byte> data, PeerInfo peerInfo) =>
            BigSendAsync(udp, data, peerInfo, null, default);

        internal static async Task BigSendAsync(this UdpClient udp, ReadOnlyMemory<byte> data, PeerInfo peerInfo,
            PortProbeSchedule? schedule = null, CancellationToken cancellationToken = default)
        {
            if (peerInfo.ActiveEndPoint is { } active &&
                IsValidPeerAddress(active.Address) && active.Port is >= 1 and <= 65535)
            {
                try
                {
                    await udp.SendAsync(data, active, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                return;
            }

            IReadOnlyList<IPEndPoint> targets = QuicPunchConnection.BuildProbeTargets(
                peerInfo,
                remoteCandidates: null,
                askedPort: 0,
                maxEndpoints: schedule == null ? 32 : 16, schedule: schedule);

            foreach (IPEndPoint target in targets)
            {
                try
                {
                    await udp.SendAsync(data, target, cancellationToken).ConfigureAwait(false);
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

            if (IPAddress.TryParse(line, out var literal) && literal.AddressFamily == AddressFamily.InterNetworkV6)
            { host = line; return true; }

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
                if (EndpointAddressCodec.CanAdvertise(ip))
                    return [new IPEndPoint(ip, port)];
                return null;
            }

            try
            {
                var addresses = Dns.GetHostAddresses(host);

                return addresses.Where(EndpointAddressCodec.CanAdvertise)
                    .Select(a => new IPEndPoint(a, port)).ToArray();
            }
            catch
            {
                return null;
            }
        }

        public static ConnectionFlags GetConnectionFlags(IEnumerable<IPEndPoint> stunResponses, int originalPort)
        {
            ArgumentNullException.ThrowIfNull(stunResponses);
            return GetConnectionFlags(
                stunResponses.Select(static endpoint => new KeyValuePair<IPEndPoint, int>(endpoint, 1)),
                originalPort);
        }

        public static ConnectionFlags GetConnectionFlags(IEnumerable<KeyValuePair<IPEndPoint, int>> stunResponses, int originalPort)
        {
            var cf = new ConnectionFlags();
            var list = stunResponses?
                .Where(static response => response.Key != null && response.Key.Port is >= 1 and <= 65535 && response.Value > 0)
                .ToList();
            if (list == null || list.Count == 0)
                return cf;

            long total = list.Sum(static response => (long)response.Value);

            var ipGroups = list.GroupBy(static response => response.Key.Address)
                               .Select(static group => group.Sum(response => (long)response.Value))
                               .OrderByDescending(static count => count)
                               .ToArray();

            cf.MultipleIps = ipGroups.Length > 1 && ((double)ipGroups[0] / total) < 0.95;

            var portGroups = list.GroupBy(static response => response.Key.Port)
                                 .Select(static group => new { Port = group.Key, Hits = group.Sum(response => (long)response.Value) })
                                 .OrderByDescending(static port => port.Hits)
                                 .ToArray();

            int dominantPort = portGroups[0].Port;
            if (portGroups.Length == 1)
                cf.PortMode = PortMode.Single;
            else if (portGroups.Length <= PortAdvertisementCodec.MaxExplicitPorts)
                cf.PortMode = PortMode.Multiple;
            else
                cf.PortMode = PortMode.Range;

            cf.ExternalPortMayDiffer = cf.PortMode != PortMode.Single || (originalPort > 0 && dominantPort != originalPort);
            cf.IsNat = cf.MultipleIps || cf.ExternalPortMayDiffer;

            return cf;
        }

        public static QuicPunch.NetworkType GetNetworkType(ConnectionFlags flags)
        {
            ArgumentNullException.ThrowIfNull(flags);
            if (flags.IsTor)
                return QuicPunch.NetworkType.Tor;

            bool dynamicPorts = flags.PortMode != PortMode.Single;
            return flags.MultipleIps
                ? (dynamicPorts ? QuicPunch.NetworkType.DynamicPortAndAddress : QuicPunch.NetworkType.DynamicAddress)
                : (dynamicPorts ? QuicPunch.NetworkType.DynamicPort : QuicPunch.NetworkType.Static);
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
                ? rawAddresses.Select(EndpointAddressCodec.Normalize).Where(EndpointAddressCodec.CanAdvertise).Distinct().Take(MaxEndpointTokenAddresses).ToList()
                : GetValidLocalIPAddresses().Take(MaxEndpointTokenAddresses).ToList();

            if (addresses.Count == 0)
                throw new InvalidOperationException("Peer has no valid IP address to publish in an endpoint token.");

            ushort[] portArray = PortAdvertisementCodec.Normalize(p.PortArray);

            if (portArray.Length == 0)
                throw new InvalidOperationException("Peer endpoint token requires at least one valid port.");

            PortMode normalizedMode = PortAdvertisementCodec.ResolveMode(portArray, p.ConnectionFlags.PortMode);
            portArray = PortAdvertisementCodec.NormalizeForMode(portArray, normalizedMode);
            p.PortArray = portArray;
            bool extended = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            bool multipleAddresses = addresses.Count > 1 || extended;
            p.ConnectionFlags.MultipleIps = multipleAddresses;
            p.ConnectionFlags.PortMode = normalizedMode;

            bool hasHostPort = p.PreferredPort > 0;
            var tokenFlags = new ConnectionFlags(p.ConnectionFlags.RawValue)
            {
                ExternalPortMayDiffer = p.ConnectionFlags.ExternalPortMayDiffer || normalizedMode != PortMode.Single ||
                    (p.PreferredPort > 0 && portArray.Any(port => port != p.PreferredPort)),
                UsesStructuredPorts = false,
                HasHostPort = hasHostPort
            };

            w.Write(tokenFlags.RawValue);

            if (multipleAddresses)
            {
                w.Write((byte)(addresses.Count | (extended ? 128 : 0)));
                foreach (IPAddress address in addresses)
                    EndpointAddressCodec.Write(w, address, extended);
            }
            else
            {
                w.Write(addresses[0].GetAddressBytes());
            }

            switch (tokenFlags.PortMode)
            {
                case PortMode.Single:
                    w.Write((ushort)portArray[0]);
                    break;
                case PortMode.Multiple:
                    if (portArray.Length > PortAdvertisementCodec.MaxExplicitPorts)
                        throw new InvalidOperationException($"Too many ports ({portArray.Length}) for Multiple port mode. Maximum is {PortAdvertisementCodec.MaxExplicitPorts}.");
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

            if (hasHostPort) w.Write(p.PreferredPort);
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
            catch (Exception) when (!flags.HasHostPort && !(flags.MultipleIps && rawToken.Length > 1 && (rawToken[1] & 128) != 0) && TryDecodeLegacyWanToken(rawToken, out var legacyPeer) && legacyPeer != null)
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
                int header = flags.MultipleIps ? span[offset++] : 1;
                bool extended = (header & 128) != 0;
                int addressCount = header & 127;

                if (addressCount is < 1 or > MaxEndpointTokenAddresses)
                    throw new InvalidDataException($"Token address count exceeds maximum allowed ({addressCount}).");

                for (int i = 0; i < addressCount; i++)
                {
                    if (offset >= span.Length) throw new InvalidDataException("Truncated address length.");
                    int length = extended ? span[offset++] : 4;
                    if (length is not (4 or 16) || offset + length > span.Length)
                        throw new InvalidDataException("Truncated or invalid peer address in endpoint token.");
                    var ip = EndpointAddressCodec.Normalize(new IPAddress(span.Slice(offset, length)));
                    offset += length;

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
                        peer.PortArray = [minPort, maxPort];
                        if (flags.UsesStructuredPorts)
                        {
                            if (offset + 2 > span.Length)
                                throw new InvalidDataException("Truncated preferred port in endpoint token.");
                            peer.PreferredPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                            offset += 2;
                            if (peer.PreferredPort == 0)
                                throw new InvalidDataException("Endpoint token contains preferred port 0.");
                        }
                        break;

                    case PortMode.Multiple:
                        if (offset + 1 > span.Length)
                            throw new InvalidDataException("Truncated port count in endpoint token.");
                        int portCount = span[offset++];
                        if (portCount is < 2 or > PortAdvertisementCodec.MaxExplicitPorts)
                            throw new InvalidDataException($"Endpoint token contains invalid explicit port count {portCount}.");
                        if (offset + (portCount * 2) > span.Length)
                            throw new InvalidDataException("Truncated port array in endpoint token.");

                        ushort[] ports = new ushort[portCount];
                        for (int i = 0; i < portCount; i++)
                        {
                            ports[i] = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                            offset += 2;
                        }
                        if (ports.Any(static port => port == 0) || ports.Distinct().Count() != ports.Length)
                            throw new InvalidDataException("Endpoint token contains invalid or duplicate ports.");
                        peer.PortArray = PortAdvertisementCodec.Normalize(ports);
                        break;

                    default:
                        throw new InvalidDataException("Invalid port mode in endpoint token.");
                }

                if (flags.HasHostPort)
                {
                    if (offset + 2 > span.Length) throw new InvalidDataException("Truncated host port in endpoint token.");
                    peer.PreferredPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                    if (peer.PreferredPort == 0) throw new InvalidDataException("Endpoint token contains host port 0.");
                }

                bool hasValidPorts = peer.PortArray is { Length: > 0 } && peer.PortArray.All(static port => port > 0);

                if (!hasValidPorts)
                    throw new InvalidDataException("Endpoint token contains an invalid port configuration.");

                if (offset + EndpointTokenCertificateHashLength != span.Length)
                    throw new InvalidDataException("Endpoint token format mismatch or contains unexpected trailing data.");

                byte[] certHash = span.Slice(offset, EndpointTokenCertificateHashLength).ToArray();
                offset += EndpointTokenCertificateHashLength;

                peer.SetCertificateHash(certHash);
                peer.Addresses = allAddresses.ToArray();
                peer.NetworkType = GetNetworkType(flags);
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
                        : [(ushort)minPort, (ushort)maxPort],
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

                byte[] computedHash = Sha3Crypto.Hash256(certPublicKey);
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
                if (PortAdvertisementsOverlap(a.ConnectionFlags, a.PortArray, b.ConnectionFlags, b.PortArray))
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

            if (IsPrivateLanAddress(endpoint.Address) && peer.PreferredPort > 0)
                return endpoint.Port == peer.PreferredPort;
            return PortAdvertisementContains(peer.ConnectionFlags, peer.PortArray, endpoint.Port);
        }

        public static bool PortAdvertisementContains(ConnectionFlags? flags, IEnumerable<ushort>? ports, int port)
        {
            if (port is <= 0 or > ushort.MaxValue || ports == null)
                return false;

            ushort[] normalized = PortAdvertisementCodec.Normalize(ports);
            if (normalized.Length == 0)
                return false;

            return flags?.PortMode == PortMode.Range && normalized.Length >= 2
                ? port >= normalized[0] && port <= normalized[^1]
                : normalized.Contains((ushort)port);
        }

        public static bool PortAdvertisementsOverlap(
            ConnectionFlags? leftFlags,
            IEnumerable<ushort>? leftPorts,
            ConnectionFlags? rightFlags,
            IEnumerable<ushort>? rightPorts)
        {
            ushort[] left = PortAdvertisementCodec.Normalize(leftPorts);
            ushort[] right = PortAdvertisementCodec.Normalize(rightPorts);
            if (left.Length == 0 || right.Length == 0)
                return false;

            bool leftRange = leftFlags?.PortMode == PortMode.Range && left.Length >= 2;
            bool rightRange = rightFlags?.PortMode == PortMode.Range && right.Length >= 2;
            if (leftRange && rightRange)
                return left[0] <= right[^1] && right[0] <= left[^1];
            if (leftRange)
                return right.Any(port => port >= left[0] && port <= left[^1]);
            if (rightRange)
                return left.Any(port => port >= right[0] && port <= right[^1]);
            return left.Intersect(right).Any();
        }

        public static bool HasExactAdvertisedPort(ConnectionFlags? flags, IEnumerable<ushort>? ports, int port)
        {
            if (port is <= 0 or > ushort.MaxValue || flags?.PortMode == PortMode.Range || ports == null)
                return false;

            return ports.Contains((ushort)port);
        }
    }
}
