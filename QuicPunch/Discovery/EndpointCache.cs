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
        private static readonly byte[] Magic = new byte[] { (byte)'Q', (byte)'P', (byte)'E', (byte)'P' };
        private const byte CurrentVersion = 1;
        private const byte FamilyIPv4 = 4;
        private const byte FamilyIPv6 = 6;
        private const int HeaderSize = 18;
        private const int MinRecordSize = 7;
        private const int MaxEntries = 4096;
        private const int MaxCacheFileBytes = 1024 * 1024;
        private static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);

        public static bool TryRead(string cachePath, TimeSpan ttl, out List<IPEndPoint> endpoints, bool checkTtl)
        {
            endpoints = new List<IPEndPoint>();
            try
            {
                if (!File.Exists(cachePath))
                    return false;

                var fileInfo = new FileInfo(cachePath);
                if (fileInfo.Length <= 0 || fileInfo.Length > MaxCacheFileBytes)
                    return Reject(cachePath);

                byte[] data = File.ReadAllBytes(cachePath);
                if (data.Length >= HeaderSize && HasMagic(data))
                {
                    if (data[4] != CurrentVersion)
                        return Reject(cachePath);

                    long timestampTicks = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(6, 8));
                    long nowTicks = DateTime.UtcNow.Ticks;
                    if (timestampTicks <= 0 || timestampTicks > nowTicks + MaxFutureClockSkew.Ticks)
                        return Reject(cachePath);

                    if (checkTtl && nowTicks - timestampTicks > ttl.Ticks)
                        return false;

                    int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14, 4));
                    int maxPossibleRecords = (data.Length - HeaderSize) / MinRecordSize;
                    if (count <= 0 || count > MaxEntries || count > maxPossibleRecords)
                        return Reject(cachePath);

                    var parsed = new List<IPEndPoint>(count);
                    int offset = HeaderSize;
                    for (int i = 0; i < count; i++)
                    {
                        if (offset >= data.Length)
                            return Reject(cachePath);

                        byte family = data[offset++];
                        if (family == FamilyIPv4)
                        {
                            if (offset + 6 > data.Length)
                                return Reject(cachePath);
                            var ip = new IPAddress(data.AsSpan(offset, 4));
                            offset += 4;
                            ushort port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                            offset += 2;
                            parsed.Add(new IPEndPoint(ip, port));
                        }
                        else if (family == FamilyIPv6)
                        {
                            if (offset + 22 > data.Length)
                                return Reject(cachePath);
                            var ip = new IPAddress(data.AsSpan(offset, 16));
                            offset += 16;
                            ushort port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                            offset += 2;
                            uint scope = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                            offset += 4;
                            if (scope != 0) ip.ScopeId = scope;
                            parsed.Add(new IPEndPoint(ip, port));
                        }
                        else
                        {
                            return Reject(cachePath);
                        }
                    }

                    if (offset != data.Length || parsed.Count != count)
                        return Reject(cachePath);

                    endpoints = parsed;
                    return true;
                }

                // Backward-compatible plain-text cache. Keep it bounded too, then
                // immediately upgrade it to the binary representation.
                DateTime legacyNow = DateTime.UtcNow;
                if (fileInfo.LastWriteTimeUtc > legacyNow + MaxFutureClockSkew)
                    return Reject(cachePath);
                if (checkTtl && legacyNow - fileInfo.LastWriteTimeUtc > ttl)
                    return false;

                var text = System.Text.Encoding.UTF8.GetString(data);
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (endpoints.Count >= MaxEntries)
                        return Reject(cachePath);
                    if (IPEndPoint.TryParse(line, out var ep))
                        endpoints.Add(ep);
                }

                if (endpoints.Count > 0)
                {
                    Save(cachePath, endpoints);
                    return true;
                }

                return Reject(cachePath);
            }
            catch
            {
                endpoints = new List<IPEndPoint>();
                return false;
            }
        }

        public static void Save(string cachePath, IEnumerable<IPEndPoint> endpoints)
        {
            try
            {
                var list = endpoints
                    .Where(ep => ep.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Distinct()
                    .Take(MaxEntries)
                    .ToList();
                if (list.Count == 0)
                    return;

                int estimatedSize = HeaderSize + list.Sum(ep => ep.AddressFamily == AddressFamily.InterNetworkV6 ? 23 : 7);
                byte[] buffer = new byte[estimatedSize];
                Magic.CopyTo(buffer.AsSpan(0, 4));
                buffer[4] = CurrentVersion;
                buffer[5] = 0;
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
                    else
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
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tempPath, buffer);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            catch { }
        }

        private static bool HasMagic(byte[] data) =>
            data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];

        private static bool Reject(string cachePath)
        {
            try { File.Delete(cachePath); } catch { }
            return false;
        }
    }
}
