using QuicPunch;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using UdpPunchHoleTest;

public static class QuicPunchMain
{
    private static readonly byte[] InfoHash = SHA1.HashData(File.ReadAllBytes(Helpers.FileName));

    public static UdpClient? udp = null;
    public static async Task<int> StartScaner(string[] args)
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
        {
            Console.WriteLine("QUIC is not supported on this machine.");
            return 1;
        }

        if (args.Length > 0)
        {
            foreach (var a in args)
            {
                //ConectionHandler.HandleConnection(a);
            }

            await Task.Delay(-1);
        }

        Console.WriteLine($"Local UDP port: {QuicPunchCore.LocalPort}\n");


        string myToken = await QuicPunchCore.GetToken();
        Console.WriteLine($"Your token: {myToken}\n");

        string quickUri = $"https://gato.ovh/protred?uri=QPHP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(myToken))}";
        Console.WriteLine($"Share this url for quick connection: {quickUri}\n");
        DiyClipper.SetText(quickUri);

        udp = new UdpClient();

        if (OperatingSystem.IsWindows())
            udp.Client.IOControl(-1744830452, [0], null);

        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, QuicPunchCore.LocalPort));

        var cts = new CancellationTokenSource();
        var listenTask = QuicPunchCore.ListenLoop(udp, cts);

        var ts = new TrackerScanner(InfoHash, QuicPunchCore.LocalPort);

        ts.Start();

        ts.OnPeerFound += async (peer) =>
        {
            if (peer.Address.Equals(QuicPunchCore.IPv4Address))
                return;

            Console.WriteLine($"Peer found: {peer} starting interogation...");

            QuicPunchCore.PeerInterogation(peer, udp, new CancellationTokenSource());
        };

        QuicPunchCore.OnPeerAvilable += async (peer) =>
        {
            Console.WriteLine($"New Peer Avillablle:  {peer.Name}");
        };

        QuicPunchCore.Manager.HandshakeRequested += async (request, ct) =>
        {
            Console.WriteLine("Incoming connection request");

            Console.WriteLine($"Id: {request.Id}");
            Console.WriteLine($"Type: {request.ConnectionType}");
            Console.WriteLine($"Remote: {request.RemoteEndPoint}");

            Console.Write($"New conection request from {request.RemoteEndPoint} for type {request.ConnectionType} . Accept? (y/n): ");

           var res =  MessageBox.Show("New connection request", $"Id: {request.Id}\nType: {request.ConnectionType}\nRemote: {request.RemoteEndPoint}\n\nAccept?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            

            bool accepted = res == DialogResult.Yes;

            if (!accepted)
                return new HandshakeDecision(false, null, null);

            return new HandshakeDecision(true, (ushort)Random.Shared.Next(1024, 65535), cts);
        };

        Console.WriteLine("Press enter to conect to someone:");
        Console.ReadLine();

        await QuicPunchCore.InitPeerConection(udp, new Guid("00000000-0000-0000-0000-000000000001"), QuicPunchCore.AvilablePeers.ElementAt(0).Value, (ushort)Random.Shared.Next(1024, 65535), cts);

        await Task.Delay(-1);
        return 0;
    }
}