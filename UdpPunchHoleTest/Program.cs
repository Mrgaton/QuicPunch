using Microsoft.Win32;
using QuicPunch;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web;
using UdpPunchHoleTest;

internal static class Program
{
    public static Process CurrentProcess = Process.GetCurrentProcess();
    public static string FileName = CurrentProcess.MainModule.FileName;

    public static string AppDataPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "QuicPunchV3");

    public static PeerInfo CurrentPeer = new PeerInfo()
    {
        Name = Environment.UserName,
        EndPoint = new IPEndPoint(QuicPunchCore.IPv4Address, QuicPunchCore.LocalPort),
        CertHash = CertManager.PeerCertPublicHash,
        CurvePublicKey = CertManager.Curve.ExportSubjectPublicKeyInfo()
    };

    private static void Main(string[] args)
    {
        if (!Directory.Exists(AppDataPath))
            Directory.CreateDirectory(AppDataPath);

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

        QuicPunchMain.StartScaner(args).GetAwaiter().GetResult();
    }

}