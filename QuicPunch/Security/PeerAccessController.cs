using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunch.Security
{
    public sealed class PeerAccessController : IPeerAccessController
    {
        private readonly Func<PeerStore?> _peerStoreProvider;
        private readonly Func<Guid, PeerInfo?> _peerByIdResolver;
        private readonly PeerByCertHashResolver? _peerByCertHashResolver;

        public ExpectedPeerCertSet ExpectedPeerCerts { get; } = new();

        public bool AutoAcceptConnections { get; set; } = true;
        public bool AutoAcceptUntrustedConnections { get; set; } = false;

        private readonly ConcurrentDictionary<Guid, bool> _autoAcceptPeers = new();
        private readonly ConcurrentDictionary<byte[], bool> _autoAcceptCertHashes = new(Utilities.ByteArrayComparer.Instance);

        public PeerAccessController(
            Func<PeerStore?>? peerStoreProvider = null,
            Func<Guid, PeerInfo?>? peerByIdResolver = null,
            PeerByCertHashResolver? peerByCertHashResolver = null,
            bool autoAcceptConnections = true,
            bool autoAcceptUntrusted = false)
        {
            _peerStoreProvider = peerStoreProvider ?? (() => null);
            _peerByIdResolver = peerByIdResolver ?? (_ => null);
            _peerByCertHashResolver = peerByCertHashResolver;
            AutoAcceptConnections = autoAcceptConnections;
            AutoAcceptUntrustedConnections = autoAcceptUntrusted;
        }

        public void TrustPeer(byte[] certHash)
        {
            ArgumentNullException.ThrowIfNull(certHash);
            ExpectedPeerCerts.Add(certHash);
        }

        public void UntrustPeer(byte[] certHash)
        {
            ArgumentNullException.ThrowIfNull(certHash);
            ExpectedPeerCerts.Remove(certHash);
        }

        public bool IsTrusted(byte[]? certHash)
        {
            if (certHash == null) return false;
            if (ExpectedPeerCerts.Contains(certHash)) return true;

            var store = _peerStoreProvider();
            return store != null && store.TryGet(certHash, out _);
        }

        public bool IsTrusted(PeerInfo? peer) =>
            peer != null && peer.TryGetCertificateHash(out var certHash) && IsTrusted(certHash);

        public bool IsTrusted(Guid peerId)
        {
            var peer = _peerByIdResolver(peerId);
            return peer != null && IsTrusted(peer);
        }

        public void SetAutoAcceptAll(bool autoAccept)
        {
            AutoAcceptConnections = autoAccept;
            AutoAcceptUntrustedConnections = false;
        }

        public void SetPeerAutoAccept(Guid peerId, bool autoAccept)
        {
            if (autoAccept)
            {
                var peer = _peerByIdResolver(peerId);
                if (peer == null || !IsTrusted(peer))
                    return;

                _autoAcceptPeers[peerId] = true;
                if (peer.TryGetCertificateHash(out var certHash))
                    _autoAcceptCertHashes[certHash] = true;
            }
            else
            {
                _autoAcceptPeers.TryRemove(peerId, out _);
                var peer = _peerByIdResolver(peerId);
                if (peer != null && peer.TryGetCertificateHash(out var certHash))
                    _autoAcceptCertHashes.TryRemove(certHash, out _);
            }
        }

        public void SetPeerAutoAccept(byte[] certHash, bool autoAccept)
        {
            if (certHash == null) return;

            if (autoAccept)
            {
                if (!IsTrusted(certHash))
                    return;

                _autoAcceptCertHashes[certHash] = true;
                if (_peerByCertHashResolver != null && _peerByCertHashResolver(certHash, out var peer) && peer != null)
                {
                    _autoAcceptPeers[peer.Id] = true;
                }
            }
            else
            {
                _autoAcceptCertHashes.TryRemove(certHash, out _);
                if (_peerByCertHashResolver != null && _peerByCertHashResolver(certHash, out var peer) && peer != null)
                {
                    _autoAcceptPeers.TryRemove(peer.Id, out _);
                }
            }
        }

        public bool IsAutoAccepted(Guid peerId)
        {
            if (AutoAcceptUntrustedConnections) return true;

            var peer = _peerByIdResolver(peerId);
            if (peer == null || !IsTrusted(peer))
                return false;

            if (_autoAcceptPeers.ContainsKey(peerId)) return true;
            return peer.TryGetCertificateHash(out var certHash) && IsAutoAccepted(certHash);
        }

        public bool IsAutoAccepted(byte[] certHash)
        {
            if (AutoAcceptUntrustedConnections) return true;
            if (certHash == null || !IsTrusted(certHash)) return false;
            if (_autoAcceptCertHashes.ContainsKey(certHash)) return true;
            return AutoAcceptConnections;
        }

        public IReadOnlyList<Guid> GetAutoAcceptedPeers()
        {
            var result = new List<Guid>();
            foreach (var kvp in _autoAcceptPeers)
            {
                if (IsAutoAccepted(kvp.Key))
                    result.Add(kvp.Key);
            }
            return result;
        }

        public void EnsureAllowedForApplication(PeerInfo peer)
        {
            if (peer == null)
                throw new ArgumentNullException(nameof(peer));

            if (!AutoAcceptUntrustedConnections && !IsTrusted(peer))
                throw new InvalidOperationException("Peer must be trusted before starting application connections.");
        }
    }

    public delegate bool PeerByCertHashResolver(byte[] certHash, out PeerInfo? peer);

    public class ExpectedPeerCertSet
    {
        private readonly ConcurrentDictionary<byte[], byte> _dict = new(Utilities.ByteArrayComparer.Instance);
        public void Add(byte[] certHash) { if (certHash != null) _dict[certHash] = 0; }
        public bool Contains(byte[] certHash) => certHash != null && _dict.ContainsKey(certHash);
        public bool Remove(byte[] certHash) => certHash != null && _dict.TryRemove(certHash, out _);
        public void Clear() => _dict.Clear();
        public IReadOnlyCollection<byte[]> ToList() => _dict.Keys.ToList();
    }

    public interface IPeerAccessController
    {
        bool AutoAcceptConnections { get; set; }
        bool AutoAcceptUntrustedConnections { get; set; }

        void TrustPeer(byte[] certHash);
        void UntrustPeer(byte[] certHash);
        bool IsTrusted(byte[]? certHash);
        bool IsTrusted(PeerInfo? peer);
        bool IsTrusted(Guid peerId);

        void SetAutoAcceptAll(bool autoAccept);
        void SetPeerAutoAccept(Guid peerId, bool autoAccept);
        void SetPeerAutoAccept(byte[] certHash, bool autoAccept);
        bool IsAutoAccepted(Guid peerId);
        bool IsAutoAccepted(byte[] certHash);
        IReadOnlyList<Guid> GetAutoAcceptedPeers();

        void EnsureAllowedForApplication(PeerInfo peer);
    }
}
