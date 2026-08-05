using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch
{
    public class QuicPunchNode : IDisposable
    {
        public QuicPunch Core { get; }

        public string LocalToken => Core.GetToken();
        public PeerInfo LocalPeer => Core.CurrentPeer;

        public TrackerScanner? TrackerScanner => Core.TrackerScanner;
        public HandshakeManager Manager => Core.Manager;

        public ConcurrentDictionary<Guid, PeerInfo> AvailablePeers => Core.AvailablePeers;
        public ConcurrentDictionary<Guid, QuicPunch.IProtocolHandler> ProtocolHandlers => Core.ProtocolHandlers;

        public event Action<PeerInfo>? OnPeerAvailable;
        public event Action<IPEndPoint>? OnPeerDiscovered;

        public QuicPunchNode(QuicPunch core)
        {
            Core = core ?? throw new ArgumentNullException(nameof(core));

            Core.OnPeerAvailable += (peer) => OnPeerAvailable?.Invoke(peer);

            if (Core.TrackerScanner != null)
            {
                Core.TrackerScanner.OnPeerFound += (endpoint) =>
                {
                    OnPeerDiscovered?.Invoke(endpoint);
                    _ = PeerInterrogationAsync(endpoint, Core.CancellationSource);
                };
            }
        }

        public static QuicPunchBuilder CreateBuilder() => new QuicPunchBuilder();

        public void RegisterProtocol(QuicPunch.IProtocolHandler handler) => Core.RegisterProtocol(handler);
        public bool RemoveProtocol(QuicPunch.IProtocolHandler handler) => Core.RemoveProtocol(handler);

        public Task PeerInterrogationAsync(string token, CancellationTokenSource? cts = null)
        {
            return Core.PeerInterrogation(token, cts ?? Core.CancellationSource);
        }

        public Task PeerInterrogationAsync(PeerInfo peer, CancellationTokenSource? cts = null)
        {
            return Core.PeerInterrogation(peer, cts ?? Core.CancellationSource);
        }

        public Task PeerInterrogationAsync(IPEndPoint endpoint, CancellationTokenSource? cts = null)
        {
            var peer = new PeerInfo
            {
                Addresses = new[] { endpoint.Address },
                MinPort = endpoint.Port,
                MaxPort = endpoint.Port
            };
            return Core.PeerInterrogation(peer, cts ?? Core.CancellationSource);
        }

        public Task InitQuicConnectionAsync(Guid protocolId, PeerInfo peer, ushort localPort = 0, CancellationTokenSource? cts = null)
        {
            if (localPort == 0)
            {
                localPort = (ushort)Random.Shared.Next(1024, 65535);
            }
            return Core.InitQuicConnection(protocolId, peer, localPort, cts ?? Core.CancellationSource);
        }

        public Task<(bool Success, UdpClient Client, IPEndPoint remoteEndpoint)> InitUdpConnectionAsync(Guid protocolId, PeerInfo peer, ushort localPort = 0, CancellationTokenSource? cts = null)
        {
            if (localPort == 0)
            {
                localPort = (ushort)Random.Shared.Next(1024, 65535);
            }
            return Core.InitUdpConnection(protocolId, peer, localPort, cts ?? Core.CancellationSource);
        }

        public void Dispose()
        {
            Core.Dispose();
        }
    }
}
