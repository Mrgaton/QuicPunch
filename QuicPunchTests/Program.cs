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
using UdpPunchHoleTest.Plugins.LocalNet;

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
        var localNetConfig = LocalNetConfig.LoadOrCreate(Path.Combine(AppContext.BaseDirectory, "localnet.json"));
        var localNetHandler = new LocalNetHandler(localNetConfig);
        var allowedPeers = LoadAllowedPeers(Path.Combine(AppContext.BaseDirectory, "allowed_peers.txt"));

        qcc.RegisterProtocol(chatHandler);
        qcc.RegisterProtocol(localNetHandler);

        string myToken = await qcc.GetToken();
        Console.WriteLine($"Your token: {myToken}\n");

        string quickUri = $"https://gato.ovh/protred?uri=QPHP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(myToken))}";
        Console.WriteLine($"Share this url for quick connection: {quickUri}\n");
        DiyClipper.SetText(quickUri);

        if (allowedPeers.Count > 0)
        {
            Console.WriteLine("Peer filter enabled:");
            foreach (var peer in allowedPeers)
            {
                Console.WriteLine($"  {peer}");
            }
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("Peer filter disabled. Add peer tokens or ip:port lines to allowed_peers.txt to only connect with specific PCs.\n");
        }


        var peerReady = new TaskCompletionSource<PeerInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        qcc.TrackerScanner.OnPeerFound += (peer) =>
        {
            if (!IsAllowedPeer(peer))
            {
                Console.WriteLine($"Ignoring peer not in allowed_peers.txt: {peer}");
                return;
            }

            Console.WriteLine($"Peer found: {peer} starting interogation...");

            _ = qcc.PeerInterogation(peer, new CancellationTokenSource());
        };

        qcc.OnPeerAvilable += (peer) =>
        {
            if (!IsAllowedPeer(peer.EndPoint))
            {
                Console.WriteLine($"Ignoring available peer not in allowed_peers.txt: {peer.EndPoint}");
                return;
            }

            Console.WriteLine($"New Peer Avillablle:  {peer.Name}");
            peerReady.TrySetResult(peer);
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

        async Task<PeerInfo> WaitForPeerAsync()
        {
            while (qcc.AvilablePeers.Count == 0)
            {
                Console.WriteLine("No peer is fully available yet. Waiting for the UDP interogation to complete...");

                var delayTask = Task.Delay(TimeSpan.FromSeconds(15), cts.Token);
                var finishedTask = await Task.WhenAny(peerReady.Task, delayTask);

                if (finishedTask == peerReady.Task)
                    return await peerReady.Task;

                Console.WriteLine("Still waiting. If your friend already sees you, keep both apps open a little longer.");
            }

            return qcc.AvilablePeers.ElementAt(0).Value;
        }

        Console.WriteLine("Press enter to connect to someone:");
        Console.ReadLine();

        var selectedPeer = await WaitForPeerAsync();

        Console.WriteLine("Choose protocol:");
        Console.WriteLine("  1 - LocalNet TCP tunnel");
        Console.WriteLine("  2 - Chat");
        Console.Write("> ");

        string? protocolChoice = Console.ReadLine();
        QuicPunchCore.IProtocolHandler selectedHandler =
            protocolChoice == "2" ? chatHandler : localNetHandler;

        await qcc.InitPeerConection(
            selectedHandler.ProtocolId,
            selectedPeer,
            (ushort)Random.Shared.Next(1024, 65535),
            cts);

        await Task.Delay(-1);

        HashSet<IPEndPoint> LoadAllowedPeers(string path)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(
                    path,
                    "# Put one allowed peer per line.\r\n# Accepted formats:\r\n#   vgjnSQu4\r\n#   190.8.231.73:3000\r\n",
                    Encoding.UTF8);

                return [];
            }

            var peers = new HashSet<IPEndPoint>();

            foreach (var rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Split('#')[0].Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    if (line.Contains(':'))
                    {
                        var parts = line.Split(':', 2);
                        peers.Add(new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
                    }
                    else
                    {
                        peers.Add(DecodeEndpointToken(line));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Invalid allowed peer '{line}': {ex.Message}");
                }
            }

            return peers;
        }

        IPEndPoint DecodeEndpointToken(string token)
        {
            byte[] data = Convert.FromBase64String(token);

            if (data.Length != 6)
                throw new FormatException("Endpoint token must decode to 6 bytes.");

            var ip = new IPAddress(data.Take(4).ToArray());
            int port = (data[4] << 8) | data[5];

            return new IPEndPoint(ip, port);
        }

        bool IsAllowedPeer(IPEndPoint peer)
        {
            return allowedPeers.Count == 0 || allowedPeers.Contains(peer);
        }
    }

}
