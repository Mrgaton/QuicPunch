using Microsoft.Win32;
using QuicPunch;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web;
using UdpPunchHoleTest;

internal static class Program
{

    public static Process CurrentProcess = Process.GetCurrentProcess();
    public static string FileName = CurrentProcess.MainModule.FileName;
    private static readonly byte[] PoolId = File.ReadAllBytes(FileName);
    private static async Task Main(string[] args)
    {
        //args = ["vgjnSQG7"];

        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0].Contains("://"))
        {
            args = args[0].Split("/").Skip(2).Select(e => HttpUtility.UrlDecode(e)).ToArray();
        }

        const string scheme = "QPHP";
        const string appId = "1504191031804035112";

        string exe = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        string prefix = $"{scheme}://join/";

        bool IsAdmin()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        void RelaunchAsAdmin()
        {
            Process.Start(new ProcessStartInfo(exe, "--elevated")
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }

        bool IsRegistered()
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}\shell\open\command");
            return k?.GetValue("") is string s && s.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }

        void RegisterProtocol()
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            k.SetValue("", $"URL:{scheme} Protocol");
            k.SetValue("URL Protocol", "");

            using var cmd = k.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }

        try
        {
            if (!IsRegistered())
            {
                try
                {
                    RegisterProtocol();
                }
                catch (UnauthorizedAccessException)
                {
                    if (!IsAdmin())
                    {
                        RelaunchAsAdmin();
                    }

                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Protocol setup failed: {ex.Message}");
        }

        var cts = new CancellationTokenSource();
        QuicPunchCore qcc = new QuicPunchCore(cts, PoolId);
        var chatHandler = new ChatHandler();

        qcc.RegisterProtocol(chatHandler);

        string myToken = await qcc.GetToken();
        Console.WriteLine($"Your token: {myToken}\n");

        string quickUri = $"https://gato.ovh/protred?uri=QPHP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(myToken))}";
        Console.WriteLine($"Share this url for quick connection: {quickUri}\n");
        DiyClipper.SetText(quickUri);


        qcc.TrackerScanner.OnPeerFound += async (peer) =>
        {
            Console.WriteLine($"Peer found: {peer} starting interogation...");

            qcc.PeerInterogation(peer, new CancellationTokenSource());
        };

        qcc.OnPeerAvilable += async (peer) =>
        {
            Console.WriteLine($"New Peer Avillablle:  {peer.Name}");
        };

        qcc.Manager.HandshakeRequested += async (request, ct) =>
        {
            Console.WriteLine("Incoming connection request");

            var protocol = qcc.ProtocolHandlers[request.ProtocolId];

            Console.WriteLine($"Id: {request.Id}");
            Console.WriteLine($"ProtocolId: {request.ProtocolId}");
            Console.WriteLine($"Remote: {request.RemoteEndPoint}");

            Console.Write($"New conection request from {request.RemoteEndPoint} for type {request.ProtocolId}.");

            var res = MessageBox.Show("New connection request", $"Id: {request.Id}\nType: {request.ProtocolId}\nName: {protocol.ProtocolName}\nRemote: {request.RemoteEndPoint}\n\nAccept?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);


            bool accepted = res == DialogResult.Yes;

            if (!accepted)
                return new HandshakeDecision(false, null, null);

            return new HandshakeDecision(true, (ushort)Random.Shared.Next(1024, 65535), cts.Token);
        };

        Console.WriteLine("Press enter to conect to someone:");
        Console.ReadLine();

        await qcc.InitPeerConection(chatHandler.ProtocolId, qcc.AvilablePeers.ElementAt(0).Value, (ushort)Random.Shared.Next(1024, 65535), cts);

        await Task.Delay(-1);
    }

}