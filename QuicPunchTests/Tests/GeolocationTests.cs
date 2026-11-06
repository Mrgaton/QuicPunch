using System.Net;
using QuicNet.Geolocation;
using QuicNet.Networking;

namespace QuicPunchTests.Tests;

internal static class GeolocationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task RunAsync()
    {
        foreach (string ip in new[] { "127.0.0.1", "10.2.3.4", "172.16.1.2", "192.168.0.1", "100.64.1.1", "169.254.1.1", "0.0.0.0", "224.0.0.1", "198.51.100.1", "203.0.113.1", "::1", "fe80::1", "fc00::1", "2001:db8::1", "3fff::1", "::ffff:10.2.3.4" })
            Check(!LocalIpGeolocation.IsPublic(IPAddress.Parse(ip)), "Non-public address accepted: " + ip);
        foreach (string ip in new[] { "81.2.69.160", "8.8.8.8", "2001:218::", "::ffff:8.8.8.8" })
            Check(LocalIpGeolocation.IsPublic(IPAddress.Parse(ip)), "Public address rejected: " + ip);
        string directory = Path.Combine(Path.GetTempPath(), "QuicNet-geo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await AutomaticGeolocationTests.RunAsync(Path.Combine(directory, "automatic"));
            using var geo = new LocalIpGeolocation();
            Check(geo.Status is { ready: true, embeddedReady: true, downloading: false, error: null, databaseDate: "2026-10-22" }, "IPTrust must be ready without installation");
            var city = geo.Locate(false, "81.2.69.160:443");
            Check(city is { status: "located", countryCode: "GB", city: "Bracknell", region: "England", advertised: false } &&
                city.source?.StartsWith("IPTrust geolocation", StringComparison.Ordinal) == true, "IPTrust schema/source was not imported: " + city);
            Check(geo.Locate(false, "8.8.8.8:443") is { city: "Mountain View", countryCode: "US" }, "IPTrust IPv4 lookup failed");
            Check(geo.Locate(false, "[2001:4860:4860::8888]:443") is { city: "Mountain View", countryCode: "US" }, "IPTrust IPv6 lookup failed");
            Check(geo.Locate(false, "[2001:218::]:443") is { countryCode: "JP", city: null, region: null, municipality: null, precision: "ip-country" },
                "Country-only IPTrust record incorrectly exposed its nominal capital as the address city");
            Check(geo.Locate(false, "::ffff:81.2.69.160") == city, "IPv4-mapped lookup differs from native IPv4");
            Check(geo.Locate(false, null, ["10.1.1.1", "81.2.69.160"]) is { advertised: true, countryCode: "GB", city: "Bracknell" }, "Advertised address source was lost");
            Check(geo.Locate(false, "81.2.69.160:443").advertised == false, "Cached advertised lookup changed endpoint provenance");
            Check(geo.Locate(true, "81.2.69.160:443", ["8.8.8.8"]).status == "tor", "Tor exposed relay location");
            Check(geo.Locate(false, "192.168.0.1:443", ["81.2.69.160"]).status == "local", "Local endpoint was replaced by advertised public IP");
            Check(geo.Locate(false, "invalid").status == "unknown", "Invalid endpoint produced a location");
            var prior = new ProgressiveLocator().LocateFast(IPAddress.Parse("81.2.69.160")).DatabasePrior;
            Check(prior is not null && prior.City == city.city && prior.Country == city.countryCode && prior.Source == city.source, "Contacts and workbench use different providers");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++) Check(geo.Locate(false, "81.2.69.160:443") == city, "Concurrent cached lookup changed result");
            })));
            using var reload = new LocalIpGeolocation();
            Check(reload.Locate(false, "81.2.69.160:443") == city, "Restart changed embedded IPTrust result");
            Console.WriteLine("Verified embedded IPTrust IPv4/IPv6 and mapped addresses, country-only resolution, contact/workbench parity, concurrent cache, Tor/local exclusions and automatic geolocation lifecycle.");
        }
        finally { Directory.Delete(directory, true); }
    }
}
