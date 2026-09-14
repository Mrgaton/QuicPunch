using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace QuicPunch.Helpers
{
    internal sealed class IpRateLimiter
    {
        private readonly int _maxPerSecond;
        private readonly int _maxCapacity;
        private readonly int _shardCount;
        private readonly int _shardMask;
        private readonly Shard[] _shards;

        private sealed class Shard
        {
            public readonly object Lock = new();
            public readonly Dictionary<IpKey, int> Buckets;
            public readonly int Capacity;
            public long CurrentSec;

            public Shard(int capacity)
            {
                Capacity = capacity;
                Buckets = new Dictionary<IpKey, int>(Math.Min(capacity, 256));
                CurrentSec = Environment.TickCount64 / 1000;
            }

            public bool IsAllowed(in IpKey key, long sec, int maxPerSecond)
            {
                lock (Lock)
                {
                    if (sec != CurrentSec)
                    {
                        Buckets.Clear();
                        CurrentSec = sec;
                    }

                    if (Buckets.TryGetValue(key, out int count))
                    {
                        count++;
                        Buckets[key] = count;
                        return count <= maxPerSecond;
                    }

                    if (Buckets.Count >= Capacity)
                        return false;

                    Buckets[key] = 1;
                    return true;
                }
            }
        }

        public int Count
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _shards.Length; i++)
                {
                    lock (_shards[i].Lock)
                    {
                        total += _shards[i].Buckets.Count;
                    }
                }
                return total;
            }
        }

        public IpRateLimiter(int maxPerSecond = 500, int maxCapacity = 10_000)
        {
            if (maxPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerSecond));
            if (maxCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(maxCapacity));

            _maxPerSecond = maxPerSecond;
            _maxCapacity = maxCapacity;

            int shards = 1;
            while (shards * 2 <= 16 && (maxCapacity / (shards * 2)) >= 4)
            {
                shards *= 2;
            }

            _shardCount = shards;
            _shardMask = shards - 1;
            _shards = new Shard[_shardCount];

            int baseCap = maxCapacity / _shardCount;
            int remainder = maxCapacity % _shardCount;
            for (int i = 0; i < _shardCount; i++)
            {
                int shardCap = baseCap + (i < remainder ? 1 : 0);
                _shards[i] = new Shard(shardCap);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetShardIndex(in IpKey key)
        {
            if (_shardCount == 1) return 0;

            ulong h = key.Low ^ (key.High * 11400714819323198485UL);
            h ^= h >> 33;
            h *= 0xff51afd7ed558ccdUL;
            h ^= h >> 33;
            return (int)(h & (ulong)_shardMask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(in IpKey key)
        {
            long sec = Environment.TickCount64 / 1000;
            int shardIndex = GetShardIndex(in key);
            return _shards[shardIndex].IsAllowed(in key, sec, _maxPerSecond);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(uint ip) => IsAllowed(IpKey.FromUint(ip));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(IPAddress? address)
        {
            if (address == null) return false;
            return IsAllowed(IpKey.FromIPAddress(address));
        }
    }

    public readonly record struct IpKey(ulong High, ulong Low)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IpKey FromUint(uint ip) => new(0, ip);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IpKey FromIPAddress(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                Span<byte> bytes = stackalloc byte[4];
                address.TryWriteBytes(bytes, out _);
                return new IpKey(0, BinaryPrimitives.ReadUInt32BigEndian(bytes));
            }

            Span<byte> v6Bytes = stackalloc byte[16];
            address.TryWriteBytes(v6Bytes, out _);
            return new IpKey(
                BinaryPrimitives.ReadUInt64BigEndian(v6Bytes.Slice(0, 8)),
                BinaryPrimitives.ReadUInt64BigEndian(v6Bytes.Slice(8, 8)));
        }
    }
}
