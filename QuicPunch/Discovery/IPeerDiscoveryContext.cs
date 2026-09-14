using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Helpers;

namespace QuicPunch.Discovery
{
    /// <summary>
    /// Contract defining the host environment and connection engine callbacks required by <see cref="PeerDiscoveryManager"/>.
    /// </summary>
    public interface IPeerDiscoveryContext
    {
        /// <summary>Whether the overall QuicPunch node is started and active.</summary>
        bool IsStarted { get; }

        /// <summary>Whether the Tor onion service and client subsystem are started and active.</summary>
        bool IsTorStarted { get; }

        /// <summary>Cancellation token tied to the lifetime of the QuicPunch instance.</summary>
        CancellationToken LifecycleToken { get; }

        /// <summary>Whether auto-connection on discovery is enabled.</summary>
        bool AutoConnectOnDiscovery { get; }

        /// <summary>Whether incoming connections are automatically accepted.</summary>
        bool AutoAcceptConnections { get; }

        /// <summary>The local WAN peer identity info.</summary>
        PeerInfo? LocalWanPeer { get; }

        /// <summary>The local Tor peer identity info, if available.</summary>
        PeerInfo? LocalTorPeer { get; }

        /// <summary>20-byte pool identification key used for scoped discovery, or empty/null.</summary>
        byte[] PoolId { get; }

        /// <summary>SHA-256 certificate hash of the local WAN node identity.</summary>
        byte[]? LocalWanCertHash { get; }

        /// <summary>SHA-256 certificate hash of the local Tor node identity, if available.</summary>
        byte[]? LocalTorCertHash { get; }

        /// <summary>Public key bytes of the local WAN certificate.</summary>
        byte[]? WanCertPublicKey { get; }

        /// <summary>Public key bytes of the local Tor certificate, if available.</summary>
        byte[]? TorCertPublicKey { get; }

        /// <summary>secp256k1 private key for BIP-340 Schnorr WAN Nostr announcements.</summary>
        byte[]? WanNostrPrivateKey { get; }

        /// <summary>secp256k1 private key for BIP-340 Schnorr Tor Nostr announcements.</summary>
        byte[]? TorNostrPrivateKey { get; }

        /// <summary>Hex-encoded secp256k1 public key for WAN Nostr announcements.</summary>
        string? WanNostrPublicKeyHex { get; }

        /// <summary>Hex-encoded secp256k1 public key for Tor Nostr announcements.</summary>
        string? TorNostrPublicKeyHex { get; }

        /// <summary>The local bound UDP port for WAN communications.</summary>
        int LocalBoundPort { get; }

        /// <summary>The local WAN X509 certificate.</summary>
        X509Certificate2? WanCertificate { get; }

        /// <summary>The local Tor X509 certificate.</summary>
        X509Certificate2? TorCertificate { get; }

        /// <summary>Generates or retrieves the current WAN endpoint discovery token.</summary>
        string? GetWanToken();

        /// <summary>Generates or retrieves the current Tor endpoint discovery token.</summary>
        string? GetTorToken();

        /// <summary>Retrieves the Tor SOCKS5 WebProxy instance if available.</summary>
        IWebProxy? GetTorSocksProxy();

        /// <summary>Reference to the local persistent peer store, if available.</summary>
        PeerStore? PeerStore { get; }

        /// <summary>Attempts to resolve an active or available peer by certificate hash.</summary>
        bool TryGetActivePeer(byte[] certHash, out PeerInfo? peer);

        /// <summary>Retrieves all currently active outbound interrogation sessions.</summary>
        IReadOnlyList<QuicPunch.ActiveInterrogationSession> GetActiveInterrogations();

        /// <summary>Cancels an active interrogation session by ID.</summary>
        void CancelInterrogation(string sessionId);

        /// <summary>Dispatches a background interrogation against the specified WAN peer.</summary>
        Task InterrogatePeerAsync(PeerInfo peer, CancellationToken cancellationToken);

        /// <summary>Dispatches a background Tor connection against the specified onion target.</summary>
        Task ConnectTorAsync(string onionAddress, int port, CancellationToken cancellationToken);

        /// <summary>Generates the byte payload for local LAN discovery interrogation beacons.</summary>
        byte[] GenerateLanBeaconPayload();

        /// <summary>Processes an incoming LAN discovery datagram received on the dedicated LAN discovery socket.</summary>
        Task ProcessIncomingLanPacketAsync(byte[] buffer, int length, EndPoint remoteEndPoint);

        /// <summary>Configures common socket options (buffer sizes, reuse, etc.) on a newly created UDP socket.</summary>
        void ConfigureUdpSocket(UdpClient client);

        /// <summary>The primary WAN UDP client instance, if currently bound.</summary>
        UdpClient? WanUdpClient { get; }
    }
}
