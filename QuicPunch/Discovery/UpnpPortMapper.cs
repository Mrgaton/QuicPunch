using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using QuicPunch.Helpers;
using QuicPunch.Discovery.PortMapping;

namespace QuicPunch.Discovery;

/// <summary>
/// Universal Plug and Play (UPnP) Internet Gateway Device (IGD) client.
/// Automatically discovers local network gateways and provisions port mappings (port forwarding)
/// for QuicPunch's UDP control and hole punching socket without requiring manual router configuration.
/// </summary>
public sealed class UpnpPortMapper : IPortMapper
{
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;

    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1"
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private int _disposed;

    public Uri? ControlUrl { get; private set; }
    public string? ServiceType { get; private set; }
    public IPAddress? LocalAddress { get; private set; }
    public IPAddress? GatewayAddress { get; private set; }
    public bool IsAvailable => ControlUrl != null && !string.IsNullOrEmpty(ServiceType);
    public string ProtocolName => "UPnP IGD";

    public async Task<PortMappingResult> TryMapPortAsync(
        int internalPort,
        int requestedExternalPort,
        ProtocolType protocol = ProtocolType.Udp,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        try
        {
            string protoStr = protocol == ProtocolType.Tcp ? "TCP" : "UDP";
            int leaseDurationSeconds = (int)(lifetime?.TotalSeconds ?? 0);
            bool mapped = await AddPortMappingAsync(
                requestedExternalPort, internalPort, null, protoStr, "QuicPunch", leaseDurationSeconds, ct).ConfigureAwait(false);
            if (!mapped)
                return PortMappingResult.Failed(ProtocolName, "UPnP port mapping failed or was rejected by IGD.");

            var extIp = await GetExternalIpAddressAsync(ct).ConfigureAwait(false);
            return PortMappingResult.Succeeded(ProtocolName, extIp, requestedExternalPort, internalPort, lifetime ?? TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            return PortMappingResult.Failed(ProtocolName, ex.Message);
        }
    }

    public Task<bool> TryUnmapPortAsync(
        int externalPort,
        ProtocolType protocol = ProtocolType.Udp,
        CancellationToken ct = default)
    {
        string protoStr = protocol == ProtocolType.Tcp ? "TCP" : "UDP";
        return DeletePortMappingAsync(externalPort, protoStr, ct);
    }

    /// <summary>
    /// Searches for an active UPnP IGD router on the local network.
    /// </summary>
    public async Task<bool> DiscoverAsync(CancellationToken ct = default)
    {
        if (IsAvailable)
            return true;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(2.5));
        CancellationToken timeoutToken = linkedCts.Token;

        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

            var targetEndpoint = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);

            // Send SSDP M-SEARCH for each target
            foreach (var st in SearchTargets)
            {
                string searchMessage =
                    "M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
                    $"ST: {st}\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n\r\n";

                byte[] searchBytes = Encoding.ASCII.GetBytes(searchMessage);
                await udp.SendAsync(searchBytes, targetEndpoint, timeoutToken).ConfigureAwait(false);
            }

            // Listen for unicast responses from the router
            while (!timeoutToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeoutToken).ConfigureAwait(false);
                string response = Encoding.ASCII.GetString(result.Buffer);

                string? location = ParseHeader(response, "LOCATION");
                if (string.IsNullOrEmpty(location))
                    continue;

                if (await TryParseDeviceDescriptionAsync(location, result.RemoteEndPoint.Address, timeoutToken).ConfigureAwait(false))
                {
                    GatewayAddress = result.RemoteEndPoint.Address;
                    LocalAddress = DetectLocalIpForGateway(GatewayAddress);
                    QuicPunchLog.Info($"[UPnP] Discovered IGD at {location} (Gateway: {GatewayAddress}, Local: {LocalAddress})");
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[UPnP] Discovery notice: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Downloads and parses the IGD device XML description to extract the control URL and service type.
    /// </summary>
    internal async Task<bool> TryParseDeviceDescriptionAsync(string locationUrl, IPAddress gatewayIp, CancellationToken ct)
    {
        try
        {
            var baseUri = new Uri(locationUrl);
            string xml = await _http.GetStringAsync(baseUri, ct).ConfigureAwait(false);

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(reader);

            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("u", "urn:schemas-upnp-org:device-1-0");

            var serviceNodes = doc.SelectNodes("//u:service", nsmgr) ?? doc.SelectNodes("//*[local-name()='service']");
            if (serviceNodes == null) return false;

            foreach (XmlNode service in serviceNodes)
            {
                string serviceType = service.SelectSingleNode("*[local-name()='serviceType']")?.InnerText ?? "";
                string controlUrl = service.SelectSingleNode("*[local-name()='controlURL']")?.InnerText ?? "";

                if ((serviceType.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) ||
                     serviceType.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrEmpty(controlUrl))
                {
                    ServiceType = serviceType.Trim();
                    ControlUrl = new Uri(baseUri, controlUrl.Trim());
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[UPnP] Could not parse description from {locationUrl}: {ex.Message}");
        }

        return false;
    }

    /// <summary>
    /// Adds a port mapping rule on the router for the specified external UDP port.
    /// </summary>
    public async Task<bool> AddPortMappingAsync(
        int externalPort,
        int internalPort,
        IPAddress? localIp = null,
        string protocol = "UDP",
        string description = "QuicPunch Control",
        int leaseSeconds = 0,
        CancellationToken ct = default)
    {
        if (!IsAvailable && !await DiscoverAsync(ct).ConfigureAwait(false))
            return false;

        IPAddress targetIp = localIp ?? LocalAddress ?? IPAddress.Loopback;

        string soapBody =
            $"<u:AddPortMapping xmlns:u=\"{ServiceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{externalPort}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>" +
            $"<NewInternalPort>{internalPort}</NewInternalPort>" +
            $"<NewInternalClient>{targetIp}</NewInternalClient>" +
            "<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{description}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{leaseSeconds}</NewLeaseDuration>" +
            "</u:AddPortMapping>";

        bool success = await SendSoapRequestAsync("AddPortMapping", soapBody, ct).ConfigureAwait(false);
        if (success)
        {
            QuicPunchLog.Info($"[UPnP] Successfully mapped external {protocol} port {externalPort} -> {targetIp}:{internalPort}");
        }
        else
        {
            QuicPunchLog.Info($"[UPnP] Failed to map external {protocol} port {externalPort} on {ControlUrl}");
        }

        return success;
    }

    /// <summary>
    /// Deletes an existing port mapping rule from the router.
    /// </summary>
    public async Task<bool> DeletePortMappingAsync(
        int externalPort,
        string protocol = "UDP",
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return false;

        string soapBody =
            $"<u:DeletePortMapping xmlns:u=\"{ServiceType}\">" +
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{externalPort}</NewExternalPort>" +
            $"<NewProtocol>{protocol}</NewProtocol>" +
            "</u:DeletePortMapping>";

        bool success = await SendSoapRequestAsync("DeletePortMapping", soapBody, ct).ConfigureAwait(false);
        if (success)
        {
            QuicPunchLog.Info($"[UPnP] Successfully removed port mapping for {protocol} port {externalPort}");
        }
        return success;
    }

    /// <summary>
    /// Queries the router's external public IP address via UPnP.
    /// </summary>
    public async Task<IPAddress?> GetExternalIpAddressAsync(CancellationToken ct = default)
    {
        if (!IsAvailable && !await DiscoverAsync(ct).ConfigureAwait(false))
            return null;

        string soapBody = $"<u:GetExternalIPAddress xmlns:u=\"{ServiceType}\"></u:GetExternalIPAddress>";

        string? responseXml = await SendSoapRequestWithResponseAsync("GetExternalIPAddress", soapBody, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(responseXml))
            return null;

        try
        {
            int start = responseXml.IndexOf("<NewExternalIPAddress>", StringComparison.OrdinalIgnoreCase);
            int end = responseXml.IndexOf("</NewExternalIPAddress>", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end > start)
            {
                string ipStr = responseXml.Substring(start + 22, end - (start + 22)).Trim();
                if (IPAddress.TryParse(ipStr, out var ip) && Utilities.IsValidPeerAddress(ip))
                {
                    return ip;
                }
            }
        }
        catch { }

        return null;
    }

    private async Task<bool> SendSoapRequestAsync(string action, string innerBodyXml, CancellationToken ct)
    {
        string? response = await SendSoapRequestWithResponseAsync(action, innerBodyXml, ct).ConfigureAwait(false);
        return response != null;
    }

    private async Task<string?> SendSoapRequestWithResponseAsync(string action, string innerBodyXml, CancellationToken ct)
    {
        if (ControlUrl == null || string.IsNullOrEmpty(ServiceType))
            return null;

        string soapEnvelope =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
            "  <s:Body>\r\n" +
            $"    {innerBodyXml}\r\n" +
            "  </s:Body>\r\n" +
            "</s:Envelope>";

        using var request = new HttpRequestMessage(HttpMethod.Post, ControlUrl);
        request.Headers.Add("SOAPAction", $"\"{ServiceType}#{action}\"");
        request.Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");

        try
        {
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            QuicPunchLog.Info($"[UPnP] SOAP {action} request failed: {ex.Message}");
        }

        return null;
    }

    private static string? ParseHeader(string response, string headerName)
    {
        var lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            int idx = line.IndexOf(':');
            if (idx > 0)
            {
                string name = line.Substring(0, idx).Trim();
                if (string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(idx + 1).Trim();
                }
            }
        }
        return null;
    }

    private static IPAddress DetectLocalIpForGateway(IPAddress gatewayIp)
    {
        try
        {
            using var s = new Socket(gatewayIp.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(gatewayIp, 80));
            if (s.LocalEndPoint is IPEndPoint lep)
            {
                return lep.Address;
            }
        }
        catch { }

        return IPAddress.Loopback;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _http.Dispose();
    }
}
