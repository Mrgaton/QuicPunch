using System;
using System.Threading;

namespace QuicPunch.Helpers
{
    /// <summary>
    /// Implements a high-performance, constant-time, zero-allocation sliding window anti-replay filter
    /// (RFC 6479 / RFC 4303 / WireGuard model) for UDP packet streams.
    /// </summary>
    public sealed class AntiReplayWindow
    {
        public const int WindowSize = 128;

        private readonly object _lock = new();
        private ulong _lastSequence;
        private UInt128 _bitmap;

        public ulong LastSequence
        {
            get
            {
                lock (_lock) return _lastSequence;
            }
        }

        /// <summary>
        /// Checks if a sequence number is acceptable without updating the window state.
        /// Useful for fast pre-validation before cryptographic decryption.
        /// </summary>
        public bool Check(ulong sequenceNumber)
        {
            if (sequenceNumber == 0) return false;

            lock (_lock)
            {
                if (sequenceNumber > _lastSequence)
                {
                    return true;
                }

                ulong backwardDiff = _lastSequence - sequenceNumber;
                if (backwardDiff >= WindowSize)
                {
                    return false;
                }

                UInt128 mask = UInt128.One << (int)backwardDiff;
                return (_bitmap & mask) == 0;
            }
        }

        /// <summary>
        /// Checks if a sequence number is acceptable, and if valid, atomically marks it as seen and updates the window.
        /// Returns true if accepted (new or valid out-of-order), false if rejected (replay, duplicate, or outside window).
        /// </summary>
        public bool CheckAndAdd(ulong sequenceNumber)
        {
            if (sequenceNumber == 0) return false;

            lock (_lock)
            {
                if (sequenceNumber > _lastSequence)
                {
                    ulong diff = sequenceNumber - _lastSequence;
                    if (diff >= WindowSize)
                    {
                        _bitmap = 1;
                    }
                    else
                    {
                        _bitmap = (_bitmap << (int)diff) | 1;
                    }

                    _lastSequence = sequenceNumber;
                    return true;
                }

                ulong backwardDiff = _lastSequence - sequenceNumber;
                if (backwardDiff >= WindowSize)
                {
                    return false;
                }

                UInt128 mask = UInt128.One << (int)backwardDiff;
                if ((_bitmap & mask) != 0)
                {
                    return false;
                }

                _bitmap |= mask;
                return true;
            }
        }

        /// <summary>
        /// Resets the sliding window state (e.g. when establishing a new cryptographic session).
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _lastSequence = 0;
                _bitmap = 0;
            }
        }
    }
}
