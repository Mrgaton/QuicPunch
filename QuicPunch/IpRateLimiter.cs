using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QuicPunch
{
    internal sealed class IpRateLimiter
    {
        private readonly int _maxPerSecond;
        private readonly Dictionary<uint, (int Count, long Sec)> _buckets = new();

        public IpRateLimiter(int maxPerSecond) => _maxPerSecond = maxPerSecond;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(uint ip)
        {
            long sec = Environment.TickCount64 / 1000;

            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, ip, out bool exists);

            if (!exists || entry.Sec != sec)
            {
                entry = (1, sec);
                return true;
            }

            if (++entry.Count > _maxPerSecond)
                return false;

            // IP spoofing bad ass attack
            if (_buckets.Count > 10_000)
                _buckets.Clear();

            return true;
        }
    }
}
