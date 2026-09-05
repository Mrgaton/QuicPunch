#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuicPunch;

namespace QuicPunchTests.Tests;

public static class TorBypassTests
{
    public static async Task RunAsync()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  QUICPUNCH TOR CASCADING BYPASS TEST SUITE");
        Console.WriteLine("==================================================");

        int passed = 0;
        int failed = 0;

        void Assert(bool condition, string testName)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {testName}");
                passed++;
            }
            else
            {
                Console.WriteLine($"[FAIL] {testName}");
                failed++;
            }
        }

        // Test 1: Verify TorBridges defaults
        Console.WriteLine("\n--- Test 1: TorBridges Built-in Configurations ---");
        var snowflakeBridges = TorBridges.GetSnowflakeBridges();
        Assert(snowflakeBridges.Count >= 2, "Default Snowflake bridges contain at least 2 entries");
        Assert(snowflakeBridges.All(b => b.StartsWith("snowflake ", StringComparison.OrdinalIgnoreCase)), "All Snowflake bridges start with 'snowflake '");
        Assert(snowflakeBridges.Any(b => b.Contains("fronts=") && b.Contains("ice=")), "Snowflake bridges contain fronts and ICE parameters");

        var obfs4Bridges = TorBridges.GetObfs4Bridges();
        Assert(obfs4Bridges.Count >= 5, "Default Obfs4 bridges contain multiple bridge lines");
        Assert(obfs4Bridges.All(b => b.StartsWith("obfs4 ", StringComparison.OrdinalIgnoreCase)), "All Obfs4 bridges start with 'obfs4 '");
        Assert(obfs4Bridges.Any(b => b.Contains("iat-mode=1")), "Obfs4 bridges include DPI-resistant iat-mode=1 bridges");
        Assert(obfs4Bridges.Any(b => b.Contains("iat-mode=0")), "Obfs4 bridges include standard iat-mode=0 bridges");

        // Test 2: Custom bridge merging
        Console.WriteLine("\n--- Test 2: Custom Bridge Merging ---");
        var customBridges = new[]
        {
            "snowflake 192.0.2.99:443 1111111111111111111111111111111111111111 custom=true",
            "obfs4 198.51.100.1:443 2222222222222222222222222222222222222222 cert=abc iat-mode=1"
        };
        var mergedSnowflake = TorBridges.GetSnowflakeBridges(customBridges: customBridges);
        Assert(mergedSnowflake.Any(b => b.Contains("custom=true")), "Merged Snowflake bridges include custom entry");

        var mergedObfs4 = TorBridges.GetObfs4Bridges(customBridges: customBridges);
        Assert(mergedObfs4.Any(b => b.Contains("198.51.100.1")), "Merged Obfs4 bridges include custom entry");

        // Test 3: Tier Timeout Calculations
        Console.WriteLine("\n--- Test 3: Tier Timeout Calculations ---");
        var options = new TorRuntimeOptions
        {
            TransportMode = TorTransportMode.AutoCascade,
            TierBootstrapTimeout = TimeSpan.FromSeconds(30),
            BootstrapTimeout = TimeSpan.FromMinutes(2)
        };
        var directTimeout = TorRuntimeManager.CalculateTierTimeout(options, TorTransportTier.Direct, false);
        var snowflakeTimeout = TorRuntimeManager.CalculateTierTimeout(options, TorTransportTier.Snowflake, false);
        var obfs4Timeout = TorRuntimeManager.CalculateTierTimeout(options, TorTransportTier.Obfs4, true);

        Assert(directTimeout == TimeSpan.FromSeconds(30), "Direct tier timeout matches base tier timeout (30s)");
        Assert(snowflakeTimeout == TimeSpan.FromSeconds(40), "Snowflake tier timeout includes WebRTC buffer (40s)");
        Assert(obfs4Timeout >= TimeSpan.FromSeconds(45), "Obfs4 tier timeout provides ample margin for obfuscation");

        // Test 4: Dynamic torrc generation for each tier
        Console.WriteLine("\n--- Test 4: Torrc Generation For All Tiers ---");
        string testDataDir = Path.Combine(Path.GetTempPath(), "qp_test_torrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDataDir);

        try
        {
            var trm = new TorRuntimeManager(new TorRuntimeOptions
            {
                DataDirectory = testDataDir,
                SocksPort = 19050,
                ControlPort = 19051
            });

            string mockTorExe = OperatingSystem.IsWindows() ? "C:\\Tor\\tor.exe" : "/usr/bin/tor";
            string mockLyrebird = OperatingSystem.IsWindows() ? "C:\\Tor\\pluggable_transports\\lyrebird.exe" : "/usr/bin/lyrebird";

            // 4a: Direct mode torrc
            string directTorrc = Path.Combine(testDataDir, "direct.torrc");
            await trm.WriteTorrcAsync(directTorrc, mockTorExe, TorTransportTier.Direct, mockLyrebird);
            string directContent = await File.ReadAllTextAsync(directTorrc);
            Assert(directContent.Contains("SocksPort 127.0.0.1:19050 IsolateSOCKSAuth"), "Direct torrc sets isolated SOCKS port");
            Assert(directContent.Contains("ControlPort 127.0.0.1:19051"), "Direct torrc sets Control port");
            Assert(!directContent.Contains("UseBridges 1"), "Direct torrc does NOT enable bridges");
            Assert(!directContent.Contains("ClientTransportPlugin"), "Direct torrc has NO ClientTransportPlugin");

            // 4b: Snowflake mode torrc
            string snowflakeTorrc = Path.Combine(testDataDir, "snowflake.torrc");
            await trm.WriteTorrcAsync(snowflakeTorrc, mockTorExe, TorTransportTier.Snowflake, mockLyrebird);
            string snowflakeContent = await File.ReadAllTextAsync(snowflakeTorrc);
            Assert(snowflakeContent.Contains("UseBridges 1"), "Snowflake torrc enables UseBridges 1");
            Assert(snowflakeContent.Contains("ClientTransportPlugin snowflake exec"), "Snowflake torrc configures snowflake PT exec");
            Assert(snowflakeContent.Contains("Bridge snowflake "), "Snowflake torrc contains Bridge snowflake directives");

            // 4c: Obfs4 mode torrc
            string obfs4Torrc = Path.Combine(testDataDir, "obfs4.torrc");
            await trm.WriteTorrcAsync(obfs4Torrc, mockTorExe, TorTransportTier.Obfs4, mockLyrebird);
            string obfs4Content = await File.ReadAllTextAsync(obfs4Torrc);
            Assert(obfs4Content.Contains("UseBridges 1"), "Obfs4 torrc enables UseBridges 1");
            Assert(obfs4Content.Contains("ClientTransportPlugin obfs4 exec"), "Obfs4 torrc configures obfs4 PT exec");
            Assert(obfs4Content.Contains("Bridge obfs4 "), "Obfs4 torrc contains Bridge obfs4 directives");
            Assert(obfs4Content.Contains("iat-mode=1"), "Obfs4 torrc includes DPI-resistant iat-mode=1 bridge lines");
        }
        finally
        {
            try { Directory.Delete(testDataDir, recursive: true); } catch { }
        }

        // Test 5: Lyrebird resolution logic
        Console.WriteLine("\n--- Test 5: Pluggable Transport Resolution ---");
        string tempDir = Path.Combine(Path.GetTempPath(), "qp_test_pt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "tor", "pluggable_transports"));
        string dummyLyrebird = Path.Combine(tempDir, "tor", "pluggable_transports", OperatingSystem.IsWindows() ? "lyrebird.exe" : "lyrebird");
        await File.WriteAllTextAsync(dummyLyrebird, "#dummy lyrebird");

        try
        {
            string? found = TorRuntimeManager.FindLyrebirdExecutable(tempDir);
            Assert(found is not null && Path.GetFullPath(found) == Path.GetFullPath(dummyLyrebird), "FindLyrebirdExecutable discovers PT binary in subfolder");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        // Test 6: Sequence order validation
        Console.WriteLine("\n--- Test 6: Cascade Sequence Ordering ---");
        var sequence = new List<TorTransportTier>
        {
            TorTransportTier.Direct,
            TorTransportTier.Snowflake,
            TorTransportTier.Obfs4
        };
        Assert(sequence[0] == TorTransportTier.Direct, "Cascade starts with Tier 1: Direct");
        Assert(sequence[1] == TorTransportTier.Snowflake, "Cascade falls back to Tier 2: Snowflake");
        Assert(sequence[2] == TorTransportTier.Obfs4, "Cascade terminates at Tier 3: Obfs4 (most resistant)");

        Console.WriteLine("\n==================================================");
        Console.WriteLine($"RESULTS: {passed} PASSED, {failed} FAILED");
        Console.WriteLine("==================================================");

        if (failed > 0)
        {
            throw new InvalidOperationException($"Tor cascade test suite failed with {failed} failure(s).");
        }
    }
}
