namespace QuicPunchTests.Tests;

internal static class TokenReadinessTests
{
    internal static async Task RunAsync()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "qp_token_readiness_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var cancellationSource = new CancellationTokenSource();
            await using var quicPunch = new QuicPunch.QuicPunch(
                cancellationSource,
                null,
                null,
                0,
                appDataPath: temporaryDirectory)
            {
                    PeerTransport = new() { AutoConnectDiscoveredPeers = false },
                WanEnabled = false
            };

            await quicPunch.StartAsync(cancellationSource.Token);
            string token = quicPunch.GetWanToken();
            if (!string.IsNullOrEmpty(token))
                throw new InvalidOperationException("A WAN token was returned before a valid WAN port existed.");

            Console.WriteLine("[PASSED] Token generation safely returned empty while the WAN endpoint was unavailable.");
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
    }
}
