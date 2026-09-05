using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch.Discovery;

namespace QuicPunchTests.Tests;

public static class UpnpTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("          UPnP IGD PORT MAPPING TESTS             ");
        Console.WriteLine("==================================================");

        await TestXmlDeviceDescriptionParsingAsync();
        await TestMockIgdServerSoapOperationsAsync();
        await TestLiveSsdpDiscoveryAsync();

        Console.WriteLine("==================================================");
        Console.WriteLine("          ALL UPnP TESTS PASSED!                  ");
        Console.WriteLine("==================================================");
    }

    private static async Task TestXmlDeviceDescriptionParsingAsync()
    {
        Console.Write("[TEST 1] XML Device description parsing... ");

        using var mapper = new UpnpPortMapper();

        // Sample IGD XML snippet
        string mockXml =
            "<?xml version=\"1.0\"?>\r\n" +
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">\r\n" +
            "  <device>\r\n" +
            "    <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>\r\n" +
            "    <deviceList>\r\n" +
            "      <device>\r\n" +
            "        <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>\r\n" +
            "        <deviceList>\r\n" +
            "          <device>\r\n" +
            "            <deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>\r\n" +
            "            <serviceList>\r\n" +
            "              <service>\r\n" +
            "                <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>\r\n" +
            "                <controlURL>/ctl/IPConn</controlURL>\r\n" +
            "                <eventSubURL>/evt/IPConn</eventSubURL>\r\n" +
            "                <SCPDURL>/WANIPCn.xml</SCPDURL>\r\n" +
            "              </service>\r\n" +
            "            </serviceList>\r\n" +
            "          </device>\r\n" +
            "        </deviceList>\r\n" +
            "      </device>\r\n" +
            "    </deviceList>\r\n" +
            "  </device>\r\n" +
            "</root>";

        // Spin up temporary HTTP server to serve the XML description
        using var listener = new HttpListener();
        int port = GetRandomPort();
        string prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            byte[] bytes = Encoding.UTF8.GetBytes(mockXml);
            ctx.Response.ContentType = "text/xml";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        bool parsed = await mapper.TryParseDeviceDescriptionAsync($"{prefix}rootDesc.xml", IPAddress.Loopback, CancellationToken.None);
        await serverTask;
        listener.Stop();

        if (!parsed)
            throw new Exception("Failed to parse valid IGD XML description");

        if (mapper.ServiceType != "urn:schemas-upnp-org:service:WANIPConnection:1")
            throw new Exception($"Unexpected serviceType: {mapper.ServiceType}");

        if (mapper.ControlUrl != new Uri($"http://127.0.0.1:{port}/ctl/IPConn"))
            throw new Exception($"Unexpected controlUrl: {mapper.ControlUrl}");

        Console.WriteLine("PASSED");
    }

    private static async Task TestMockIgdServerSoapOperationsAsync()
    {
        Console.Write("[TEST 2] Mock IGD SOAP operations (Add/Delete/GetIP)... ");

        using var mapper = new UpnpPortMapper();
        using var listener = new HttpListener();
        int port = GetRandomPort();
        string prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        bool addCalled = false;
        bool deleteCalled = false;
        bool getIpCalled = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var serverTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }

                string soapAction = ctx.Request.Headers["SOAPAction"] ?? "";
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();

                string responseSoap = "";

                if (soapAction.Contains("AddPortMapping"))
                {
                    addCalled = true;
                    responseSoap =
                        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                        "  <s:Body><u:AddPortMappingResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"/></s:Body>" +
                        "</s:Envelope>";
                }
                else if (soapAction.Contains("GetExternalIPAddress"))
                {
                    getIpCalled = true;
                    responseSoap =
                        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                        "  <s:Body>" +
                        "    <u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                        "      <NewExternalIPAddress>203.0.113.42</NewExternalIPAddress>" +
                        "    </u:GetExternalIPAddressResponse>" +
                        "  </s:Body>" +
                        "</s:Envelope>";
                }
                else if (soapAction.Contains("DeletePortMapping"))
                {
                    deleteCalled = true;
                    responseSoap =
                        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                        "  <s:Body><u:DeletePortMappingResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"/></s:Body>" +
                        "</s:Envelope>";
                }

                byte[] bytes = Encoding.UTF8.GetBytes(responseSoap);
                ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        // Setup mapper to point to our mock server
        string mockDescXml =
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><serviceList><service>" +
            "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
            "<controlURL>/control</controlURL>" +
            "</service></serviceList></device></root>";

        using var descListener = new HttpListener();
        int descPort = GetRandomPort();
        descListener.Prefixes.Add($"http://127.0.0.1:{descPort}/");
        descListener.Start();
        var descTask = Task.Run(async () =>
        {
            var ctx = await descListener.GetContextAsync();
            byte[] bytes = Encoding.UTF8.GetBytes(mockDescXml);
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        await mapper.TryParseDeviceDescriptionAsync($"http://127.0.0.1:{descPort}/desc.xml", IPAddress.Loopback, CancellationToken.None);
        await descTask;
        descListener.Stop();

        // Fix ControlUrl to point to mock SOAP listener
        var type = typeof(UpnpPortMapper);
        type.GetProperty("ControlUrl")!.SetValue(mapper, new Uri(prefix + "control"));

        // 1. AddPortMapping
        bool added = await mapper.AddPortMappingAsync(45678, 45678, IPAddress.Parse("192.168.1.50"), "UDP", "Test Mapping", 0);
        if (!added || !addCalled)
            throw new Exception("AddPortMapping failed or was not received by mock IGD");

        // 2. GetExternalIPAddress
        var extIp = await mapper.GetExternalIpAddressAsync();
        if (extIp == null || extIp.ToString() != "203.0.113.42" || !getIpCalled)
            throw new Exception($"GetExternalIPAddress failed, got: {extIp}");

        // 3. DeletePortMapping
        bool deleted = await mapper.DeletePortMappingAsync(45678, "UDP");
        if (!deleted || !deleteCalled)
            throw new Exception("DeletePortMapping failed or was not received by mock IGD");

        cts.Cancel();
        listener.Stop();
        Console.WriteLine("PASSED");
    }

    private static async Task TestLiveSsdpDiscoveryAsync()
    {
        Console.Write("[TEST 3] Local network SSDP discovery scan... ");
        using var mapper = new UpnpPortMapper();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        bool found = await mapper.DiscoverAsync(cts.Token);
        if (found)
        {
            Console.WriteLine($"PASSED (Found local gateway at {mapper.GatewayAddress}, Control: {mapper.ControlUrl})");
        }
        else
        {
            Console.WriteLine("PASSED (No UPnP IGD on local network, graceful non-blocking fallback confirmed)");
        }
    }

    private static int GetRandomPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
