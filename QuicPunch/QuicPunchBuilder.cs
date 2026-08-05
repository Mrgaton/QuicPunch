using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch
{
    public class QuicPunchBuilder
    {
        private byte[]? _poolId;
        private bool _autoDiscovery;
        private string[]? _customTrackers;
        private ushort _discoveryPort;
        private byte[]? _connectionPassword;
        private bool _autoAcceptConnections = true;
        private CancellationTokenSource? _cts;

        public QuicPunchBuilder UsePool(string poolNameOrHash)
        {
            if (string.IsNullOrWhiteSpace(poolNameOrHash))
                throw new ArgumentException("Pool identifier cannot be null or empty.", nameof(poolNameOrHash));

            if (poolNameOrHash.Length == 40 && IsHexString(poolNameOrHash))
            {
                _poolId = Convert.FromHexString(poolNameOrHash);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(poolNameOrHash);
                _poolId = SHA1.HashData(bytes);
            }

            return this;
        }

        public QuicPunchBuilder UsePool(byte[] poolId)
        {
            if (poolId == null || poolId.Length == 0)
                throw new ArgumentException("Pool ID cannot be null or empty.", nameof(poolId));

            _poolId = poolId.Length == 20 ? poolId : SHA1.HashData(poolId);
            return this;
        }

        public QuicPunchBuilder WithAutoDiscovery(bool enableTrackers = true, string[]? customTrackers = null)
        {
            _autoDiscovery = enableTrackers;
            _customTrackers = customTrackers;
            return this;
        }

        public QuicPunchBuilder WithPort(ushort port)
        {
            _discoveryPort = port;
            return this;
        }

        public QuicPunchBuilder WithPassword(string password)
        {
            _connectionPassword = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
            return this;
        }

        public QuicPunchBuilder WithPassword(byte[]? password)
        {
            _connectionPassword = password;
            return this;
        }

        public QuicPunchBuilder WithAutoAccept(bool autoAccept)
        {
            _autoAcceptConnections = autoAccept;
            return this;
        }

        public QuicPunchBuilder WithCancellationTokenSource(CancellationTokenSource cts)
        {
            _cts = cts;
            return this;
        }

        public async Task<QuicPunchNode> BuildAndStartAsync(CancellationToken cancellationToken = default)
        {
            var cts = _cts ?? new CancellationTokenSource();

            var discoveryId = _autoDiscovery ? _poolId : null;

            var quicPunch = new QuicPunch(cts, discoveryId, _connectionPassword, _autoAcceptConnections, _discoveryPort);

            if (_poolId != null)
            {
                quicPunch.PoolId = _poolId;
            }

            if (_autoDiscovery && quicPunch.TrackerScanner != null && _customTrackers != null && _customTrackers.Length > 0)
            {
                await quicPunch.TrackerScanner.Start(_customTrackers);
            }

            var node = new QuicPunchNode(quicPunch);
            return node;
        }

        private static bool IsHexString(string input)
        {
            foreach (char c in input)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }
    }
}
