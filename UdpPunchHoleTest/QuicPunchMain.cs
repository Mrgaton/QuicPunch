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

internal static class QuicPunchMain
{
    private static readonly byte[] InfoHash = SHA1.HashData(File.ReadAllBytes(Program.FileName));

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

        await Task.Delay(-1);
        return 0;
    }
}