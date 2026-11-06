using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using QuicPunch.Helpers;
using QuicPunch.Security;

namespace QuicPunch;

internal partial class QuicPunchConnection
{
    internal const int MaxBirthdaySockets = 64;
    private static readonly SemaphoreSlim BirthdayPoolGate = new(1, 1);

    internal static bool HasTranslatedSinglePort(PeerInfo peer) =>
        peer.ConnectionFlags.PortMode == PortMode.Single && peer.ConnectionFlags.ExternalPortMayDiffer;

    // Grow a local search window without changing the peer's actual advertisement.
    // Eight small rounds per forward window give nearby allocations a head start.
    internal static (int Minimum, int Maximum) TranslatedSinglePortWindow(int port, int round) =>
        (round <= 16 ? port : Math.Max(1, port - 1024), Math.Min(65535, port + (round <= 8 ? 512 : 1024)));

    internal static IPEndPoint? BirthdayTarget(PeerInfo ownPeer, PeerInfo remotePeer,
        IReadOnlyList<CandidateEndpoint>? candidates, ushort askedPort)
    {
        // A many-socket side helps against one hard NAT and one stable NAT. Two
        // destination-dependent NATs are a different problem, not a reason to flood.
        if ((ownPeer.ConnectionFlags.PortMode == PortMode.Single && !HasTranslatedSinglePort(ownPeer)) ||
            remotePeer.ConnectionFlags.PortMode != PortMode.Single) return null;
        bool Public(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork &&
            !Utilities.IsPrivateLanAddress(address) && !IPAddress.IsLoopback(address);
        var reflexive = candidates?.Where(c => c.Type == CandidateType.ServerReflexive && Public(c.EndPoint.Address))
            .OrderByDescending(c => c.Priority).FirstOrDefault();
        if (reflexive != null) return reflexive.EndPoint;
        IPAddress? address = remotePeer.ActiveEndPoint is { } active && Public(active.Address)
            ? active.Address : remotePeer.Addresses.FirstOrDefault(Public);
        return address != null && askedPort > 0 ? new IPEndPoint(address, askedPort) : null;
    }

    internal static async Task<(UdpClient? Socket, IPEndPoint? Endpoint)> OpenBirthdaySocketsAsync(
        IPEndPoint target, Guid connectionGuid, PeerInfo ownPeer, PeerInfo remotePeer,
        ECDsa key, byte[]? passwordHash, CancellationToken token)
    {
        if (!BirthdayPoolGate.Wait(0)) return (null, null);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sockets = new List<UdpClient>(MaxBirthdaySockets);
        var receivers = new List<Task<(UdpClient Socket, IPEndPoint? Endpoint)>>();
        UdpClient? winner = null;
        Task sender = Task.CompletedTask;
        try
        {
            // LAN/exact candidates get a head start and usually finish before this.
            await Task.Delay(250, token).ConfigureAwait(false);
            for (int i = 0; i < MaxBirthdaySockets; i++)
            {
                var socket = new UdpClient();
                try
                {
                    QuicPunch.ConfigureUdpSocket(socket);
                    socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                    sockets.Add(socket);
                    receivers.Add(Receive(socket));
                }
                catch (SocketException) { socket.Dispose(); break; }
            }
            if (sockets.Count == 0) return (null, null);
            QuicPunchLog.Info($"[BIRTHDAY] Opening {sockets.Count} authenticated UDP paths toward the single-port peer; at most 128 probes/s.");
            sender = SendAsync();
            var pending = new List<Task<(UdpClient Socket, IPEndPoint? Endpoint)>>(receivers);
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                if (result.Endpoint != null)
                {
                    winner = result.Socket;
                    QuicPunchLog.Info("[BIRTHDAY] Verified an additional socket path; retaining the winning local port for QUIC.");
                    return result;
                }
            }
            return (null, null);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return (null, null); }
        finally
        {
            stop.Cancel();
            foreach (var socket in sockets) if (!ReferenceEquals(socket, winner)) socket.Dispose();
            try { await sender.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { await Task.WhenAll(receivers).ConfigureAwait(false); } catch (OperationCanceledException) { }
            BirthdayPoolGate.Release();
        }

        async Task<(UdpClient Socket, IPEndPoint? Endpoint)> Receive(UdpClient socket) =>
            (socket, await ReceiveHoleLoopAsync(socket, connectionGuid, ownPeer, remotePeer, key, passwordHash, stop.Token).ConfigureAwait(false));
        async Task SendAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    byte[] probe = ConnectivityProbe.Create(connectionGuid, Guid.NewGuid(), Guid.Empty,
                        ownPeer, remotePeer, key, passwordHash);
                    foreach (var socket in sockets)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        try { await socket.SendAsync(probe, target, stop.Token).ConfigureAwait(false); }
                        catch (SocketException) { }
                        catch (ObjectDisposedException) { }
                    }
                    await Task.Delay(500, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
