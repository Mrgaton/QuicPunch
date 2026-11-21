using System.Security.Cryptography;
using QuicPunch;
using QuicPunch.Discovery;

namespace QuicPunchTests.Tests;

internal static class AvailablePeerRegistryTests
{
    public static Task RunAsync()
    {
        var registry = new AvailablePeerRegistry();
        using var trusted = NewPeer();
        var removed = new List<Guid>();
        bool IsProtected(Guid id, PeerInfo _) => id == trusted.Id;
        void OnRemoved(PeerInfo peer) => removed.Add(peer.Id);

        try
        {
            Check(registry.TryAddAvailablePeer(trusted.Id, trusted, 3, IsProtected, OnRemoved), "Protected peer was not admitted.");
            Parallel.For(0, 32, _ =>
            {
                var candidate = NewPeer();
                if (!registry.TryAddAvailablePeer(candidate.Id, candidate, 3, IsProtected, OnRemoved))
                    candidate.Dispose();
            });
            Check(registry.AvailablePeers.Count == 4 && removed.Count == 29, "Concurrent admission did not enforce the cap.");
            Check(registry.TryGetPeerByCertHash(trusted.CertHash, out var peer) && ReferenceEquals(peer, trusted),
                "Protected peer was evicted or lost its index entry.");

            byte[] originalHash = trusted.CertHash;
            using var replacement = NewPeer();
            registry.AvailablePeers[trusted.Id] = replacement;
            Check(!registry.TryGetPeerByCertHash(originalHash, out _),
                "A legacy dictionary write left an index entry pointing at a different certificate.");
            registry.AvailablePeers[trusted.Id] = trusted;

            registry.PruneDiscoveredPeers(TimeSpan.Zero, 0, IsProtected, OnRemoved);
            Check(registry.AvailablePeers.Count == 1, "Expiry did not remove all unprotected peers.");
            Check(registry.TryRemove(trusted.Id, out peer) && ReferenceEquals(peer, trusted) &&
                !registry.TryGetPeerByCertHash(trusted.CertHash, out _), "Removal left a stale certificate lookup.");
        }
        finally
        {
            registry.ClearAndDispose();
        }

        return Task.CompletedTask;
    }

    private static PeerInfo NewPeer() => new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
