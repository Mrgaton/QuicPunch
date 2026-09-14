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
        private bool? _wanAutoDiscovery;
        private bool? _torAutoDiscovery;
        private string[]? _nostrRelays;
        private string[]? _torNostrRelays;
        private string? _peerName;
        private string? _torPeerName;
        private ushort _discoveryPort;
        private byte[]? _connectionPassword;
        private bool _autoAcceptConnections = true;
        private bool _enableUpnp = true;
        private CancellationTokenSource? _cts;

        public QuicPunchBuilder WithUpnp(bool enable = true)
        {
            _enableUpnp = enable;
            return this;
        }

        public QuicPunchBuilder WithPeerName(string? peerName)
        {
            _peerName = peerName;
            return this;
        }

        public QuicPunchBuilder WithTorPeerName(string? torPeerName)
        {
            _torPeerName = torPeerName;
            return this;
        }

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

        public QuicPunchBuilder WithAutoDiscovery(bool enabled = true, string[]? nostrRelays = null)
        {
            _autoDiscovery = enabled;
            _wanAutoDiscovery = enabled;
            _torAutoDiscovery = enabled;
            _nostrRelays = nostrRelays;
            return this;
        }

        public QuicPunchBuilder WithWanNostrDiscovery(bool enabled = true, string[]? nostrRelays = null)
        {
            _wanAutoDiscovery = enabled;
            if (nostrRelays != null) _nostrRelays = nostrRelays;
            return this;
        }

        public QuicPunchBuilder WithTorNostrDiscovery(bool enabled = true, string[]? torNostrRelays = null)
        {
            _torAutoDiscovery = enabled;
            if (torNostrRelays != null) _torNostrRelays = torNostrRelays;
            return this;
        }

        public QuicPunchBuilder WithNostrRelays(string[]? relays)
        {
            _nostrRelays = relays;
            return this;
        }

        public QuicPunchBuilder WithTorNostrRelays(string[]? relays)
        {
            _torNostrRelays = relays;
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

        private bool _autoConnectOnDiscovery;

        public QuicPunchBuilder WithAutoConnectOnDiscovery(bool autoConnect = true)
        {
            _autoConnectOnDiscovery = autoConnect;
            return this;
        }

        private CancellationToken _cancellationToken;

        public QuicPunchBuilder WithCancellationToken(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            return this;
        }

        public QuicPunchBuilder WithCancellationTokenSource(CancellationTokenSource cts)
        {
            _cts = cts;
            _cancellationToken = cts?.Token ?? default;
            return this;
        }

        private string? _appDataPath;
        public QuicPunchBuilder WithAppDataPath(string appDataPath)
        {
            _appDataPath = appDataPath;
            return this;
        }

        public QuicPunch Build(CancellationToken cancellationToken = default)
        {
            bool wanEnabled = _wanAutoDiscovery ?? _autoDiscovery;
            bool torEnabled = _torAutoDiscovery ?? _autoDiscovery;
            var discoveryId = (wanEnabled || torEnabled) ? _poolId : null;
            var quicPunch = new QuicPunch(cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken, discoveryId, _connectionPassword, _autoAcceptConnections, _discoveryPort, appDataPath: _appDataPath);
            quicPunch.Discovery.WanNostrDiscoveryEnabled = wanEnabled;
            quicPunch.Discovery.TorNostrDiscoveryEnabled = torEnabled;

            if (_poolId != null)
            {
                quicPunch.PoolId = _poolId;
            }

            if (_nostrRelays != null && _nostrRelays.Length > 0)
            {
                quicPunch.Discovery.NostrRelays = _nostrRelays;
            }

            if (_torNostrRelays != null && _torNostrRelays.Length > 0)
            {
                quicPunch.Discovery.TorNostrRelays = _torNostrRelays;
            }

            if (!string.IsNullOrWhiteSpace(_peerName))
            {
                quicPunch.CurrentPeer.Name = _peerName;
            }

            if (!string.IsNullOrWhiteSpace(_torPeerName))
            {
                quicPunch.TorCurrentPeer.Name = _torPeerName;
            }

            quicPunch.EnableUpnp = _enableUpnp;
            quicPunch.AutoConnectOnDiscovery = _autoConnectOnDiscovery;

            return quicPunch;
        }

        public async Task<QuicPunch> BuildAndStartAsync(CancellationToken cancellationToken = default)
        {
            var quicPunch = Build(cancellationToken);

            await quicPunch.StartAsync(cancellationToken).ConfigureAwait(false);

            return quicPunch;
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
