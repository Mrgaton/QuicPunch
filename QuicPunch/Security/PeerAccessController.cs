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
        private readonly object _trustGate = new();
        private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);

        public ExpectedPeerCertSet ExpectedPeerCerts { get; } = new();

        public PeerAccessController(
            Func<PeerStore?>? peerStoreProvider = null,
            Func<Guid, PeerInfo?>? peerByIdResolver = null)
        {
            _peerStoreProvider = peerStoreProvider ?? (() => null);
            _peerByIdResolver = peerByIdResolver ?? (_ => null);
        }

        public void TrustPeer(byte[] certHash)
        {
            ArgumentNullException.ThrowIfNull(certHash);
            lock (_trustGate)
            {
                _peerStoreProvider()?.SetTrusted(certHash, true);
                _revoked.Remove(Convert.ToHexString(certHash));
                ExpectedPeerCerts.Add(certHash);
            }
        }

        public void UntrustPeer(byte[] certHash)
        {
            ArgumentNullException.ThrowIfNull(certHash);
            lock (_trustGate)
            {
                _revoked.Add(Convert.ToHexString(certHash));
                ExpectedPeerCerts.Remove(certHash);
                _peerStoreProvider()?.SetTrusted(certHash, false);
            }
        }

        public bool IsTrusted(byte[]? certHash)
        {
            if (certHash == null) return false;
            lock (_trustGate)
            {
                if (_revoked.Contains(Convert.ToHexString(certHash))) return false;
                var store = _peerStoreProvider();
                if (store != null && store.TryGet(certHash, out var saved)) return saved!.Trusted;
                return ExpectedPeerCerts.Contains(certHash);
            }
        }

        public bool IsTrusted(PeerInfo? peer) =>
            peer != null && peer.TryGetCertificateHash(out var certHash) && IsTrusted(certHash);

        public bool IsTrusted(Guid peerId)
        {
            var peer = _peerByIdResolver(peerId);
            return peer != null && IsTrusted(peer);
        }

        public void EnsureAllowedForApplication(PeerInfo peer)
        {
            if (peer == null)
                throw new ArgumentNullException(nameof(peer));

            if (!IsTrusted(peer))
                throw new InvalidOperationException("Peer must be trusted before starting application connections.");
        }
    }

    public class ExpectedPeerCertSet
    {
        private readonly ConcurrentDictionary<string, byte> _dict = new(StringComparer.Ordinal);
        public void Add(byte[] certHash) { if (certHash != null) _dict[Convert.ToHexString(certHash)] = 0; }
        public bool Contains(byte[] certHash) => certHash != null && _dict.ContainsKey(Convert.ToHexString(certHash));
        public bool Remove(byte[] certHash) => certHash != null && _dict.TryRemove(Convert.ToHexString(certHash), out _);
        public void Clear() => _dict.Clear();
        public IReadOnlyCollection<byte[]> ToList() => _dict.Keys.Select(Convert.FromHexString).ToList();
    }

    public interface IPeerAccessController
    {
        void TrustPeer(byte[] certHash);
        void UntrustPeer(byte[] certHash);
        bool IsTrusted(byte[]? certHash);
        bool IsTrusted(PeerInfo? peer);
        bool IsTrusted(Guid peerId);

        void EnsureAllowedForApplication(PeerInfo peer);
    }
}
