using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using QuicPunch.Security;

namespace QuicPunchTests.Tests;

public static class IpReputationFilterTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("       IP REPUTATION & THREAT FILTER TESTS        ");
        Console.WriteLine("==================================================");

        string tempCacheFile = Path.Combine(Path.GetTempPath(), $"ipsum_test_{Guid.NewGuid():N}.cache");

        try
        {
            // Test 1: Basic In-Memory Whitelist & Blacklist Overrides
            Console.Write("[TEST 1] Manual Whitelist / Blacklist overrides... ");
            using (var filter = new IpReputationFilter(level: 3, autoUpdateInterval: null, autoLoadCache: false, autoStart: false))
            {
                var targetIp = IPAddress.Parse("198.51.100.42");
                var endPoint = new IPEndPoint(targetIp, 4433);

                if (filter.IsBlocked(targetIp) || filter.IsBlocked(endPoint))
                    throw new Exception("IP should not be blocked initially.");

                filter.AddToBlacklist(targetIp);
                if (!filter.IsBlocked(targetIp) || !filter.IsBlocked(endPoint) || filter.IsAllowed(targetIp))
                    throw new Exception("IP should be blocked after AddToBlacklist.");

                // Whitelist overrides blacklist
                filter.AddToWhitelist(targetIp);
                if (filter.IsBlocked(targetIp) || !filter.IsAllowed(targetIp))
                    throw new Exception("Whitelisted IP should not be blocked.");

                filter.RemoveFromWhitelist(targetIp);
                filter.AddToBlacklist(targetIp);
                filter.RemoveFromBlacklist(targetIp);
                if (filter.IsBlocked(targetIp))
                    throw new Exception("IP should not be blocked after removal from blacklist.");
            }
            Console.WriteLine("PASSED");

            // Test 2: Cache Save and Load (Simulated Feed)
            Console.Write("[TEST 2] Cache persistence & offline warming... ");
            File.WriteAllLines(tempCacheFile, new[]
            {
                "# Comment header in cache",
                "1.2.3.4",
                "192.0.2.100",
                "   192.0.2.101\t5   ", // with tab and spaces
                "invalid.ip.address",
                ""
            });

            using (var cachedFilter = new IpReputationFilter(level: 3, cacheFilePath: tempCacheFile, autoUpdateInterval: null, autoLoadCache: true, autoStart: false))
            {
                if (cachedFilter.BlockedCount != 3)
                    throw new Exception($"Expected 3 IPs from cache, got {cachedFilter.BlockedCount}");

                if (!cachedFilter.IsBlocked(IPAddress.Parse("1.2.3.4")))
                    throw new Exception("1.2.3.4 should be blocked.");

                if (!cachedFilter.IsBlocked(IPAddress.Parse("192.0.2.100")))
                    throw new Exception("192.0.2.100 should be blocked.");

                if (!cachedFilter.IsBlocked(IPAddress.Parse("192.0.2.101")))
                    throw new Exception("192.0.2.101 should be blocked.");

                // IPv4-mapped IPv6 check
                var v6Mapped = IPAddress.Parse("::ffff:1.2.3.4");
                if (!cachedFilter.IsBlocked(v6Mapped))
                    throw new Exception("IPv4-mapped IPv6 ::ffff:1.2.3.4 should be detected and blocked.");

                // Clean random IP
                if (cachedFilter.IsBlocked(IPAddress.Parse("8.8.8.8")))
                    throw new Exception("8.8.8.8 should not be blocked.");
            }
            Console.WriteLine("PASSED");

            // Test 3: Live Feed Download from IPsum GitHub repository
            Console.Write("[TEST 3] Live IPsum feed download (Level 4)... ");
            using (var liveFilter = new IpReputationFilter(level: 4, cacheFilePath: tempCacheFile, autoUpdateInterval: null, autoLoadCache: false, autoStart: false))
            {
                int loaded = await liveFilter.UpdateAsync();
                if (loaded < 100)
                    throw new Exception($"Expected at least 100 IPs from Level 4, got {loaded}");

                if (liveFilter.BlockedCount != loaded)
                    throw new Exception($"BlockedCount mismatch: {liveFilter.BlockedCount} vs {loaded}");

                if (liveFilter.LastUpdated == null)
                    throw new Exception("LastUpdated timestamp was not set.");

                // Verify cache file was written to disk
                if (!File.Exists(tempCacheFile) || new FileInfo(tempCacheFile).Length == 0)
                    throw new Exception("Cache file was not saved after live update.");
            }
            Console.WriteLine("PASSED");

            Console.WriteLine("\n[✓] ALL IP REPUTATION FILTER TESTS PASSED SUCCESSFULLY.");
        }
        finally
        {
            try { if (File.Exists(tempCacheFile)) File.Delete(tempCacheFile); } catch { }
            try { if (File.Exists(tempCacheFile + ".tmp")) File.Delete(tempCacheFile + ".tmp"); } catch { }
        }
    }
}
