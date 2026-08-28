using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QuicPunch.Helpers
{
    internal sealed class IpRateLimiter
    {
        private readonly int _maxPerSecond;
        private readonly int _maxCapacity;
        private readonly object _lock = new();
        private readonly Dictionary<uint, int> _buckets;
        private long _currentSec;

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _buckets.Count;
                }
            }
        }

        public IpRateLimiter(int maxPerSecond = 500, int maxCapacity = 10_000)
        {
            _maxPerSecond = maxPerSecond;
            _maxCapacity = maxCapacity;
            _buckets = new Dictionary<uint, int>(Math.Min(maxCapacity, 1024));
            _currentSec = Environment.TickCount64 / 1000;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsAllowed(uint ip)
        {
            long sec = Environment.TickCount64 / 1000;

            lock (_lock)
            {
                if (sec != _currentSec)
                {
                    _buckets.Clear();
                    _currentSec = sec;
                }
                else if (_buckets.Count >= _maxCapacity)
                {
                    _buckets.Clear();
                }

                ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, ip, out bool exists);
                if (!exists)
                {
                    count = 1;
                    return true;
                }

                if (++count > _maxPerSecond)
                {
                    return false;
                }

                return true;
            }
        }
    }
}
