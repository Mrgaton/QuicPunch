using System.Net;
using System.Net.Sockets;
using QuicNet;

namespace QuicPunchTests.Fixtures;

// Real HTTP UI without starting a QuicPunch node or external connections.
internal static class WebUiPreview
{
    internal static WebUiServer Start(CancellationTokenSource shutdown)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var server = new WebUiServer(capabilities: NodeCapabilities.CreateDesktop(false), cts: shutdown, port: port);
        server.Start();
        return server;
    }

    internal static async Task<int> ServeAsync()
    {
        using var shutdown = new CancellationTokenSource();
        var server = Start(shutdown);
        Console.WriteLine("WEBUI-TEST-URL=" + server.Url);
        Console.Out.Flush();
        try { await Console.In.ReadLineAsync(); }
        finally { server.Stop(); shutdown.Cancel(); }
        return 0;
    }
}
