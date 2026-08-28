using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace QuicPunch
{
    internal static class EndpointCache
    {
        // Protocol: 'QPEP' (QuicPunch Endpoint Protocol)
        // 4 bytes: Magic [0x51, 0x50, 0x45, 0x50] ("QPEP")
        // 1 byte: Version (1)
        // 1 byte: Flags (0)
        // 8 bytes: TimestampUtcTicks (long, little-endian)
        // 4 bytes: Count (int, little-endian)
        // Repeated records:
        //   - Family tag (1 byte): 4 for IPv4, 6 for IPv6
        //   - IPv4: 4 bytes IP, 2 bytes port (ushort big-endian) -> 7 bytes
        //   - IPv6: 16 bytes IP, 2 bytes port (ushort big-endian), 4 bytes scope id (uint little-endian) -> 23 bytes

        private static readonly byte[] Magic = new byte[] { (byte)'Q', (byte)'P', (byte)'E', (byte)'P' };
        private const byte CurrentVersion = 1;
        private const byte FamilyIPv4 = 4;
        private const byte FamilyIPv6 = 6;
        private const int HeaderSize = 18;

        public static bool TryRead(string cachePath, TimeSpan ttl, out List<IPEndPoint> endpoints, bool checkTtl)
        {
            endpoints = new List<IPEndPoint>();
            try
            {
                if (!File.Exists(cachePath))
                    return false;

                var fileInfo = new FileInfo(cachePath);
                if (fileInfo.Length == 0)
                {
                    try { File.Delete(cachePath); } catch { }
                    return false;
                }

                byte[] data;
                using (var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    data = new byte[fs.Length];
                    fs.ReadExactly(data);
                }
                if (data.Length >= HeaderSize &&
                    data[0] == Magic[0] && data[1] == Magic[1] &&
                    data[2] == Magic[2] && data[3] == Magic[3])
                {
                    byte version = data[4];
                    if (version != CurrentVersion)
                        return false;

                    long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(6, 8));
                    if (checkTtl && DateTime.UtcNow.Ticks - timestampTicks > ttl.Ticks)
                    {
                        return false;
                    }

                    int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14, 4));
                    if (count <= 0)
                        return false;

                    endpoints = new List<IPEndPoint>(count);
                    int offset = HeaderSize;

                    while (offset < data.Length && endpoints.Count < count)
                    {
                        byte family = data[offset++];
                        if (family == FamilyIPv4)
                        {
                            if (offset + 4 + 2 > data.Length) break;
                            var ip = new IPAddress(data.AsSpan(offset, 4));
                            offset += 4;
                            ushort port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                            offset += 2;
                            endpoints.Add(new IPEndPoint(ip, port));
                        }
                        else if (family == FamilyIPv6)
                        {
                            if (offset + 16 + 2 + 4 > data.Length) break;
                            var ip = new IPAddress(data.AsSpan(offset, 16));
                            offset += 16;
                            ushort port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                            offset += 2;
                            uint scope = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                            offset += 4;
                            if (scope != 0) ip.ScopeId = scope;
                            endpoints.Add(new IPEndPoint(ip, port));
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (endpoints.Count > 0)
                        return true;
                }
                else
                {
                    // Backward compatibility fallback for legacy plain text .epl file:
                    if (checkTtl && DateTime.UtcNow - fileInfo.LastWriteTimeUtc > ttl)
                    {
                        return false;
                    }

                    var text = System.Text.Encoding.UTF8.GetString(data);
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (IPEndPoint.TryParse(line, out var ep))
                        {
                            endpoints.Add(ep);
                        }
                    }

                    if (endpoints.Count > 0)
                    {
                        // Auto-upgrade legacy cache file to binary format
                        Save(cachePath, endpoints);
                        return true;
                    }
                }

                try { File.Delete(cachePath); } catch { }
            }
            catch { }

            return false;
        }

        public static void Save(string cachePath, IEnumerable<IPEndPoint> endpoints)
        {
            try
            {
                var list = endpoints.Distinct().ToList();
                if (list.Count == 0)
                    return;

                int estimatedSize = HeaderSize;
                foreach (var ep in list)
                {
                    estimatedSize += (ep.AddressFamily == AddressFamily.InterNetworkV6 ? 23 : 7);
                }

                byte[] buffer = new byte[estimatedSize];
                Magic.CopyTo(buffer.AsSpan(0, 4));
                buffer[4] = CurrentVersion;
                buffer[5] = 0; // flags
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(6, 8), DateTime.UtcNow.Ticks);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(14, 4), list.Count);

                int offset = HeaderSize;
                foreach (var ep in list)
                {
                    if (ep.AddressFamily == AddressFamily.InterNetwork)
                    {
                        buffer[offset++] = FamilyIPv4;
                        ep.Address.TryWriteBytes(buffer.AsSpan(offset, 4), out _);
                        offset += 4;
                        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)ep.Port);
                        offset += 2;
                    }
                    else if (ep.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        buffer[offset++] = FamilyIPv6;
                        ep.Address.TryWriteBytes(buffer.AsSpan(offset, 16), out _);
                        offset += 16;
                        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)ep.Port);
                        offset += 2;
                        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)ep.Address.ScopeId);
                        offset += 4;
                    }
                }

                string? dir = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, buffer.AsSpan(0, offset).ToArray());
                File.Move(tempPath, cachePath, overwrite: true);
            }
            catch { }
        }
    }
}
