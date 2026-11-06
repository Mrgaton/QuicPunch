using System.Net;
using System.Reflection;
using QuicPunch;
using QuicPunch.Discovery;
using QuicPunch.Helpers;

namespace QuicPunchTests.Fixtures;

// Opt-in isolation for node integration tests. This affects only this test process.
internal static class LoopbackOnlyNetwork
{
    internal static void Configure()
    {
        // Time-sensitive wire tests use the same system clock, without background NTP/HTTPS requests.
        typeof(PreciseTime).GetField("_nextSyncAttempt", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, long.MaxValue);
        Array.Fill(NostrDiscovery.DefaultRelays, "ws://127.0.0.1:9");
        Array.Fill(StunGatherer.BuiltInStunHosts, "127.0.0.1:9");
        Array.Fill(StunGatherer.RemoteStunUrls, "http://127.0.0.1:9/stun-fixture");
        StunGatherer.StunEndpointsCachePath = Path.Combine(Path.GetTempPath(), "qp-isolated-stun-" + Guid.NewGuid().ToString("N"));
        // Block automatic reputation/catalog HTTP requests from ever leaving loopback.
        HttpClient.DefaultProxy = new LoopbackProxy();
        Console.WriteLine("Network fixtures: loopback relays/STUN, system clock, nonlocal HTTP blocked by a loopback proxy.");
    }

    private sealed class LoopbackProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => IsBypassed(destination) ? destination : new Uri("http://127.0.0.1:9");
        public bool IsBypassed(Uri destination) => destination.IsLoopback;
    }
}
