using Microsoft.Win32;
using QuicPunch;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Web;
using Wintun;

internal static class Program
{

    public static Process CurrentProcess = Process.GetCurrentProcess();
    public static string FileName = CurrentProcess.MainModule.FileName;
    private static readonly byte[] PoolId = Encoding.UTF8.GetBytes("QuicPunch🔥");//File.ReadAllBytes(FileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

    public static WintunSession _session;
    private static FriendsLanHandler _friendsLanHandler;
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

        try
        {
            uint version = WintunAdapter.GetRunningDriverVersion();
            Console.WriteLine($"Running driver version: {version}");
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("wintun.dll not found in the output directory. Please copy it.");
            return;
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"Win32Exception while checking driver (expected if not installed/admin): {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex}");
        }

        Console.Write("Write your last ip digit 10.0.0.x:");
        string ipDigit = Console.ReadLine();

        string ip = $"10.0.0.{ipDigit}";

        Console.WriteLine("\nTrying to create QuicPunch adapter...");
        try
        {
            var adapter = WintunAdapter.Create("QuicPunchAdapter", "QuicPunchTunnel");
            Console.WriteLine($"Adapter created successfully! LUID: {adapter.Luid}");


            SetAdapterIP("QuicPunchAdapter", ip, "255.0.0.0");
            SetAdapterMTU("QuicPunchAdapter", 65535);

            _session = adapter.StartSession();
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"Failed to create adapter (requires Administrator privileges): {ex.Message} (Error code: {ex.NativeErrorCode})");
            if (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                Console.WriteLine("Verification result: The library successfully called wintun.dll! Access Denied is the expected outcome without Administrator privileges.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error during creation: {ex}");
        }

        var cts = new CancellationTokenSource();
        QuicPunchCore qcc = new QuicPunchCore(cts, PoolId, (ushort)(Debugger.IsAttached ? 4001: 4002));

        _friendsLanHandler = new FriendsLanHandler(IPAddress.Parse(ip));


        Task.Run(() => CaptureWintunAndSendToInternet(10));

        var chatHandler = new ChatHandler();

        qcc.RegisterProtocol(_friendsLanHandler);
        qcc.RegisterProtocol(chatHandler);


        string myToken = qcc.GetToken();
        Console.WriteLine($"Your token: {myToken}\n");

        string quickUri = $"https://gato.ovh/protred?uri=QPHP://{HttpUtility.UrlEncode(HttpUtility.UrlEncode(myToken))}";
        Console.WriteLine($"Share this url for quick connection: {quickUri}\n");
        DiyClipper.SetText(quickUri);


        qcc.TrackerScanner.OnPeerFound += (peer) =>
        {
            Console.WriteLine($"Peer found: {peer} starting interogation...");

            _ = qcc.PeerInterogation(peer, new CancellationTokenSource());
        };

        qcc.OnPeerAvilable += (peer) =>
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

        while (true)
        {
            try
            {
                Console.WriteLine("Press enter to conect to someone");
                Console.WriteLine("Select a peer to connect:\n");
                for (int i = 0; i < qcc.AvilablePeers.Count; i++)
                {
                    Console.WriteLine($"{i}: {qcc.AvilablePeers.ElementAt(i).Value.Name} - {qcc.AvilablePeers.ElementAt(i).Key}");
                }
                Console.WriteLine("Refresh list: R");

                var input = Console.ReadKey();

                if (input.KeyChar.ToString().ToLower() == "r")
                {
                    continue;
                }

                var peer = qcc.AvilablePeers.ElementAt(input.KeyChar - '0').Value;


                Console.WriteLine("\nSelect a protocol to use:\n");

                for (int i = 0; i < qcc.ProtocolHandlers.Count; i++)
                {
                    Console.WriteLine($"{i}: {qcc.ProtocolHandlers.ElementAt(i).Value.ProtocolName} - {qcc.ProtocolHandlers.ElementAt(i).Key}");
                }

                var protocolInput = Console.ReadKey();
                var protocolId = qcc.ProtocolHandlers.ElementAt(protocolInput.KeyChar - '0').Key;

                _ = Task.Run(async () => await qcc.InitQuicConection(protocolId, peer, (ushort)Random.Shared.Next(1024, 65535), cts));
            }
            catch { }

        }
        await Task.Delay(-1);
    }
    public static void SetAdapterIP(string adapterName, string ipAddress, string subnetMask)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ip set address name=\"{adapterName}\" static {ipAddress} {subnetMask}",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
    }
    public static void SetAdapterMTU(string adapterName, int mtu)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface ipv4 set subinterface \"{adapterName}\" mtu={mtu} store=persistent",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
    }

    /*private static void CaptureWintunAndSendToInternet()
    {
            _session.ReadWaitEvent.WaitOne();

            while (true)
            {

                try
                {
                    var destIp = new IPAddress(new ReadOnlySpan<byte>(packetPointer + 16, 4));

                    if (_friendsLanHandler.ActivePeers.TryGetValue(destIp, out var stream))
                    {
                        stream.Write(BitConverter.GetBytes(packetSize));

                        var packetSpan = new ReadOnlySpan<byte>(packetPointer, (int)packetSize);
                        stream.Write(packetSpan);
                    }
                }
            catch(Exception ex)
            {
                CategoryAttribut
            }
        }
    }*/

    private static unsafe void CaptureWintunAndSendToInternet(byte ipFirstDigit)
    {
        const int ERROR_NO_MORE_ITEMS = 259;

        Span<byte> sizeBuffer = stackalloc byte[4];

        while (true)
        {
            _session.ReadWaitEvent.WaitOne();

            while (true)
            {
                byte* packetPointer =
                    WintunApi.WintunReceivePacket(_session.Handle, out uint packetSize);

                if (packetPointer == null)
                {
                    int error = Marshal.GetLastWin32Error();

                    if (error == ERROR_NO_MORE_ITEMS)
                        break;

                    throw new Win32Exception(error);
                }

                try
                {
                    uint destIp =
                        BinaryPrimitives.ReverseEndianness(
                            *(uint*)(packetPointer + 16));

                    if (packetPointer[16] > 200)
                        continue;

                    LogPacket(packetPointer, packetSize);

                    if (destIp == 0)
                    {
                        WintunApi.WintunSendPacket(_session.Handle, packetPointer);
                    }

                    if (packetPointer[16] != ipFirstDigit)
                         continue;



                    if (packetPointer[16 + 3] == 255)
                    {
                        foreach(var stream in _friendsLanHandler.ActivePeers.Values)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(sizeBuffer, packetSize);
                            stream.Write(sizeBuffer);
                            stream.Write(new ReadOnlySpan<byte>(packetPointer, (int)packetSize));
                        }
                    }
                    else if (_friendsLanHandler.ActivePeers.TryGetValue(destIp, out var stream))
                    //if (_friendsLanHandler.ActivePeers.Count > 0)
                    {
                        //var stream = _friendsLanHandler.ActivePeers.ElementAt(0).Value;

                        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuffer, packetSize);
                        stream.Write(sizeBuffer);
                        stream.Write(new ReadOnlySpan<byte>(packetPointer, (int)packetSize));
                    }
                }
                finally
                {
                    WintunApi.WintunReleaseReceivePacket(
                        _session.Handle,
                        packetPointer);
                }
            }
        }
    }
    public static unsafe void LogPacket(byte* packetPointer, uint packetSize)
    {
        byte versionAndIhl = packetPointer[0];
        int version = versionAndIhl >> 4;
        int ihl = (versionAndIhl & 0x0F) * 4;

        byte protocol = packetPointer[9];
        byte ttl = packetPointer[8];

        ushort identification =
            BinaryPrimitives.ReadUInt16BigEndian(
                new ReadOnlySpan<byte>(packetPointer + 4, 2));

        ushort totalLength =
            BinaryPrimitives.ReadUInt16BigEndian(
                new ReadOnlySpan<byte>(packetPointer + 2, 2));

        var srcIp = new IPAddress(
            new ReadOnlySpan<byte>(packetPointer + 12, 4));

        var dstIp = new IPAddress(
            new ReadOnlySpan<byte>(packetPointer + 16, 4));

        string protocolName = protocol switch
        {
            1 => "ICMP",
            6 => "TCP",
            17 => "UDP",
            _ => $"UNKNOWN({protocol})"
        };

        Console.WriteLine(
            $"IPv{version} {protocolName} " +
            $"{srcIp} -> {dstIp} " +
            $"TTL={ttl} " +
            $"LEN={totalLength} " +
            $"ID={identification} " +
            $"HDR={ihl} " +
            $"PKT={packetSize}");
    }
}