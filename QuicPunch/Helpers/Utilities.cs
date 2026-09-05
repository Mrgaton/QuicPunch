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
                int minPort = Math.Clamp(peerInfo.MinPort > 0 ? peerInfo.MinPort : 4002, 1, 65535);
                int maxPort = Math.Clamp(peerInfo.MaxPort > 0 ? peerInfo.MaxPort : minPort, 1, 65535);
                if (minPort > maxPort)
                    (minPort, maxPort) = (maxPort, minPort);

                int portSpan = maxPort - minPort + 1;
                long exhaustiveCount = (long)addresses.Length * portSpan;

                if (exhaustiveCount + targets.Count <= MaxEndpointsPerBurst)
                {
                    foreach (var address in addresses)
                    {
                        for (int port = minPort; port <= maxPort && targets.Count < MaxEndpointsPerBurst; port++)
                            targets.Add(new IPEndPoint(address, port));
                    }
                }
                else
                {
                    foreach (var address in addresses)
                    {
                        if (targets.Count >= MaxEndpointsPerBurst) break;
                        int port = portSpan == 1 ? minPort : Random.Shared.Next(minPort, maxPort + 1);
                        targets.Add(new IPEndPoint(address, port));
                    }

                    int attempts = 0;
                    while (targets.Count < MaxEndpointsPerBurst && attempts++ < MaxEndpointsPerBurst * 8)
                    {
                        var address = addresses[Random.Shared.Next(addresses.Length)];
                        int port = portSpan == 1 ? minPort : Random.Shared.Next(minPort, maxPort + 1);
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

            if (p.NetworkType == QuicPunch.NetworkType.Tor)
            {
                if (string.IsNullOrWhiteSpace(p.OnionAddress))
                    throw new ArgumentException("OnionAddress cannot be null or empty for Tor network type.", nameof(p));

                if (!p.TryGetCertificateHash(out var torCertHash) || torCertHash.Length != EndpointTokenCertificateHashLength)
                    throw new ArgumentException("Peer certificate hash must be exactly 32 bytes.", nameof(p));

                int torPort = p.MinPort > 0 ? p.MinPort : 443;
                if (torPort is < 1 or > 65535)
                    throw new ArgumentOutOfRangeException(nameof(p), "Tor port must be in the range 1..65535.");

                Span<byte> tokenBytes = stackalloc byte[TorTokenBinaryLength];

                // Offset 0: Flags (NetworkType = Tor)
                tokenBytes[0] = (byte)new PackedFlags { NetworkType = QuicPunch.NetworkType.Tor }.RawValue;

                // Offset 1..35: Raw 35 bytes of Onion address decoded directly from Base32
                if (!TryBase32Decode(p.OnionAddress.AsSpan(), tokenBytes.Slice(1, 35), out int decodedLen) || decodedLen != 35)
                    throw new ArgumentException($"Invalid Tor v3 onion address '{p.OnionAddress}': expected 35 decoded bytes (56 base32 characters).", nameof(p));

                BinaryPrimitives.WriteUInt16LittleEndian(tokenBytes.Slice(36, 2), (ushort)torPort);

                // Offset 38..69: Certificate Hash (32 bytes)
                torCertHash.CopyTo(tokenBytes.Slice(38, EndpointTokenCertificateHashLength));

                return Base64Url.EncodeToString(tokenBytes);
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            var rawAddresses = p.Addresses ?? Array.Empty<IPAddress>();
            var addresses = rawAddresses.Length > 0
                ? rawAddresses.Take(MaxEndpointTokenAddresses).ToList()
                : GetValidLocalIPAddresses().Take(MaxEndpointTokenAddresses).ToList();

            if (addresses.Count == 0)
                throw new InvalidOperationException("Peer has no valid IPv4 address to publish in an endpoint token.");

            var localIps = GetValidLocalIPAddresses()
                .Where(ip => !addresses.Contains(ip))
                .Take(Math.Max(0, MaxEndpointTokenAddresses - addresses.Count))
                .ToList();
            int minPort = p.MinPort > 0 ? p.MinPort : p.MaxPort;
            int maxPort = p.MaxPort > 0 ? p.MaxPort : p.MinPort;
            if (minPort is < 1 or > 65535 || maxPort is < 1 or > 65535 || minPort > maxPort)
                throw new InvalidOperationException("Peer endpoint token requires a valid port or port range in 1..65535.");


            bool multipleAddresses = addresses.Count > 1;
            bool portRange = minPort != maxPort;
            QuicPunch.NetworkType networkType = multipleAddresses
                ? (portRange ? QuicPunch.NetworkType.DynamicPortAndAddress : QuicPunch.NetworkType.DynamicAddress)
                : (portRange ? QuicPunch.NetworkType.DynamicPort : QuicPunch.NetworkType.Static);

            var flags = new PackedFlags { NetworkType = networkType };
            w.Write((byte)flags.RawValue);

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

            if (portRange)
            {
                w.Write((ushort)minPort);
                w.Write((ushort)maxPort);
            }
            else
            {
                w.Write((ushort)minPort);
            }

            w.Write((byte)localIps.Count);
            foreach (IPAddress localIp in localIps)
                w.Write(localIp.GetAddressBytes());

            if (!p.TryGetCertificateHash(out var certHash) || certHash.Length != EndpointTokenCertificateHashLength)
                throw new ArgumentException("Peer certificate hash must be exactly 32 bytes.", nameof(p));

            w.Write(certHash);

            byte[] payloadBytes = ms.ToArray();
            return Base64Url.EncodeToString(payloadBytes);
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
                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedPort)) port = parsedPort;
                if (port is < 1 or > 65535)
                    throw new InvalidDataException("Tor endpoint port must be in the range 1..65535.");
                onionPeer.MinPort = onionPeer.MaxPort = port;
                return onionPeer;
            }

            byte[] rawToken = Base64Url.DecodeFromChars(t);
            if (rawToken.Length == 0 || rawToken.Length > MaxEndpointTokenBytes)
                throw new InvalidDataException($"Endpoint token size is invalid ({rawToken.Length} bytes).");

            var flags = new PackedFlags(rawToken[0]);
            if (flags.NetworkType == QuicPunch.NetworkType.Tor)
            {
                if (rawToken.Length != TorTokenBinaryLength)
                    throw new InvalidDataException($"Invalid Tor endpoint token size: expected {TorTokenBinaryLength} bytes, got {rawToken.Length}.");

                ReadOnlySpan<byte> span = rawToken.AsSpan();
                var torPeer = new PeerInfo { NetworkType = QuicPunch.NetworkType.Tor };

                // Offset 1..35: Raw 35 bytes of Onion address
                ReadOnlySpan<byte> rawOnion = span.Slice(1, 35);
                torPeer.OnionAddress = Base32Encode(rawOnion).ToLowerInvariant() + ".onion";

                // Offset 36..37: Virtual Port
                ushort port = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(36, 2));
                if (port == 0)
                    throw new InvalidDataException("Tor endpoint token contains port 0.");
                torPeer.MinPort = torPeer.MaxPort = port;

                // Offset 38..69: Certificate Hash (32 bytes)
                byte[] torHash = span.Slice(38, EndpointTokenCertificateHashLength).ToArray();
                torPeer.SetCertificateHash(torHash);

                return torPeer;
            }

            const int MinWanTokenLength = 1 + 4 + 2 + 1 + EndpointTokenCertificateHashLength;
            if (rawToken.Length < MinWanTokenLength)
                throw new InvalidDataException($"WAN endpoint token size is too short ({rawToken.Length} < {MinWanTokenLength} bytes).");

            var peer = new PeerInfo();
            try
            {
                ReadOnlySpan<byte> span = rawToken.AsSpan();
                peer.NetworkType = flags.NetworkType;
                if (peer.NetworkType is not QuicPunch.NetworkType.Static
                    and not QuicPunch.NetworkType.DynamicPort
                    and not QuicPunch.NetworkType.DynamicAddress
                    and not QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    throw new InvalidDataException($"Unsupported endpoint token network type: {(byte)peer.NetworkType}.");
                }

                int offset = 1;
                var allAddresses = new List<IPAddress>();
                int addressCount = peer.NetworkType is QuicPunch.NetworkType.DynamicAddress or QuicPunch.NetworkType.DynamicPortAndAddress
                    ? span[offset++]
                    : 1;

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

                if (peer.NetworkType is QuicPunch.NetworkType.DynamicPort or QuicPunch.NetworkType.DynamicPortAndAddress)
                {
                    if (offset + 4 > span.Length)
                        throw new InvalidDataException("Truncated port range in endpoint token.");
                    peer.MinPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                    peer.MaxPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                }
                else
                {
                    if (offset + 2 > span.Length)
                        throw new InvalidDataException("Truncated port in endpoint token.");
                    peer.MinPort = peer.MaxPort = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));
                    offset += 2;
                }

                if (peer.MinPort is < 1 or > 65535 || peer.MaxPort is < 1 or > 65535 || peer.MinPort > peer.MaxPort)
                    throw new InvalidDataException("Endpoint token contains an invalid port range.");

                if (offset + 1 + EndpointTokenCertificateHashLength > span.Length)
                    throw new InvalidDataException("Endpoint token is missing its local-address count or certificate hash.");

                int localIpCount = span[offset++];
                if (localIpCount > MaxEndpointTokenAddresses)
                    throw new InvalidDataException($"Token local IP count exceeds maximum allowed ({localIpCount} > {MaxEndpointTokenAddresses}).");

                for (int i = 0; i < localIpCount; i++)
                {
                    if (offset + 4 > span.Length)
                        throw new InvalidDataException("Truncated local peer address in endpoint token.");
                    var localIp = new IPAddress(span.Slice(offset, 4));
                    offset += 4;
                    if (!IsValidPeerAddress(localIp))
                        throw new InvalidDataException($"Invalid, reserved or dangerous peer local address '{localIp}'.");
                    if (!allAddresses.Contains(localIp))
                        allAddresses.Add(localIp);
                }

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
    }
}
