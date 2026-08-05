using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web;
using Microsoft.Win32;
using QuicPunch;

namespace QuicPunchTests;

internal static class Program
{

    public static Process CurrentProcess = Process.GetCurrentProcess();
    
    public static string FileName = CurrentProcess.MainModule.FileName;
    
    private static readonly byte[] PoolId = Encoding.UTF8.GetBytes("QuicPunch🔥V1.2");//File.ReadAllBytes(FileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

    private static VirtualLanHandler _friendsLanHandler;

    [STAThread]
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0].Contains("://"))
        {
            args = args[0].Split("/").Skip(2).Select(e => HttpUtility.UrlDecode(e)).ToArray();
        }
        // args = ["vgjnSaIPkdhdVT3GVATmCT4u/6nX7E0JZx582cDqA8vUu0CGd0BfzfO7/7bAgoOb9kOlvS9H"];
        
        if (args.Length > 0)
        {
            PeerStore ps = new PeerStore(Path.Combine(QuicPunch.QuicPunch.AppDataPath, "peers.db"));
            ps.AddOrUpdate(args[0]);
            ps.Dispose();
            return;
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

        Console.Write("Emter the password for auto conections:");
        string password = "sdasd";//Console.ReadLine();

        var cts = new CancellationTokenSource();
        QuicPunch.QuicPunch qcc = new QuicPunch.QuicPunch(cts, null, Encoding.UTF8.GetBytes(password), true) { AutoAcceptConnections = false, SharePeers = true };

        _friendsLanHandler = new VirtualLanHandler();
        var chatHandler = new ChatHandler();
        var voiceCallHandler = new VoiceCallHandler();

        qcc.RegisterProtocol(_friendsLanHandler);
        qcc.RegisterProtocol(chatHandler);
        qcc.RegisterProtocol(voiceCallHandler);

        _friendsLanHandler.SetupTun();

        var webUi = new WebUiServer(qcc, chatHandler, _friendsLanHandler, voiceCallHandler, cts);
        webUi.Start();

        string myToken = qcc.GetToken();
        Console.WriteLine($"Your public endpoints: {string.Join(", ", qcc.CurrentPeer.Addresses)}\n");
        Console.WriteLine($"Your token: {myToken}\n");

        string quickUri = $"https://gato.ovh/protred?uri=QPHP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(myToken))}";
        Console.WriteLine($"Share this url for quick connection: {quickUri}\n");
        DiyClipper.SetText(quickUri);

        if (qcc.TrackerScanner != null)
        {
            qcc.TrackerScanner.OnPeerFound += (peer) =>
            {
                Console.WriteLine($"Peer found: {peer} starting interrogation...");

                //_ = qcc.PeerInterogation(peer, new CancellationTokenSource());
            };
        }
                
        qcc.OnPeerAvailable += (peer) =>
        {
            Console.WriteLine($"New Peer Available:  {peer.Name}");
        };

        // WebUiServer handles HandshakeRequested petitions for the UI

        while (true)
        {
            try
            {
                Console.WriteLine("\nPress enter to connect to someone");
                Console.WriteLine("Select a peer to connect:\n");

                Console.WriteLine("0: Enter token manualy");

                for (int i = 1; i < qcc.AvailablePeers.Count + 1; i++)
                {
                    Console.WriteLine($"{i}: {qcc.AvailablePeers.ElementAt(i - 1).Value.Name} ({qcc.AvailablePeers.ElementAt(i - 1).Value.Ping.ToString()}) - {qcc.AvailablePeers.ElementAt(i - 1).Key}");
                }
                Console.WriteLine("Refresh list: R");

                var input = Console.ReadKey();

                if (input.KeyChar.ToString().ToLower() == "r")
                    continue;

                if (input.KeyChar == '0')
                {
                    Console.Write("Enter the tokent to connect: ");
                    string? token = Console.ReadLine();
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        _ = qcc.PeerInterrogation(token, cts);
                    }
                    continue;
                }

                int index = input.KeyChar - '1';
                if (index >= 0 && index < qcc.AvailablePeers.Count)
                {
                    var peer = qcc.AvailablePeers.ElementAt(index).Value;

                    Console.WriteLine("\nSelect a protocol to use:\n");

                    for (int i = 0; i < qcc.ProtocolHandlers.Count; i++)
                    {
                        Console.WriteLine($"{i}: {qcc.ProtocolHandlers.ElementAt(i).Value.ProtocolName} - {qcc.ProtocolHandlers.ElementAt(i).Key}");
                    }

                    var protocolInput = Console.ReadKey();
                    int protoIndex = protocolInput.KeyChar - '0';
                    if (protoIndex >= 0 && protoIndex < qcc.ProtocolHandlers.Count)
                    {
                        var protocolId = qcc.ProtocolHandlers.ElementAt(protoIndex).Key;
                        _ = Task.Run(async () => await qcc.InitQuicConnection(protocolId, peer, (ushort)Random.Shared.Next(1024, 65535), cts));
                    }
                }
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(5000, cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Console loop error: {ex.Message}");
                await Task.Delay(1000, cts.Token);
            }
        }
        await Task.Delay(-1);
    }
}