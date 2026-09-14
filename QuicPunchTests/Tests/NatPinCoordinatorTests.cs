using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Discovery;

namespace QuicPunchTests.Tests;
    public static class NatPinCoordinatorTests
    {
        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   NAT PIN COORDINATOR & 3-BURST STUN TESTS       ");
            Console.WriteLine("==================================================");

            // Test 1: Bombardeo de 16 servidores por ráfaga y rotación justa
            Console.Write("[TEST 1] 16-server burst bombing and fair randomized rotation... ");
            var dummyServers = new List<IPEndPoint>();
            for (int i = 1; i <= 48; i++)
            {
                dummyServers.Add(new IPEndPoint(IPAddress.Parse($"192.0.2.{i}"), 3478));
            }

            var coordinator = new NatPinCoordinator(dummyServers);
            if (coordinator.Servers.Count != 48)
                throw new Exception($"Expected 48 servers, found {coordinator.Servers.Count}");

            var batch1 = coordinator.GetNextBurstBatch(16);
            if (batch1.Count != 16)
                throw new Exception($"Expected batch size 16, got {batch1.Count}");
            if (batch1.Distinct().Count() != 16)
                throw new Exception("Batch 1 contains duplicate server endpoints.");

            var batch2 = coordinator.GetNextBurstBatch(16);
            if (batch2.Count != 16)
                throw new Exception($"Expected batch size 16, got {batch2.Count}");
            if (batch1.Intersect(batch2).Any())
                throw new Exception("Batch 2 overlaps with Batch 1 before exhausting catalog!");

            var batch3 = coordinator.GetNextBurstBatch(16);
            if (batch3.Count != 16)
                throw new Exception($"Expected batch size 16, got {batch3.Count}");
            if (batch3.Intersect(batch1).Any() || batch3.Intersect(batch2).Any())
                throw new Exception("Batch 3 overlaps with previous batches before cycle completion!");

            // Total consumed so far: 48 servers without repetition
            var allCombined = batch1.Concat(batch2).Concat(batch3).Distinct().ToList();
            if (allCombined.Count != 48)
                throw new Exception("Full rotation did not cover all 48 servers in catalog.");

            // Batch 4 should re-shuffle and start next fair cycle
            var batch4 = coordinator.GetNextBurstBatch(16);
            if (batch4.Count != 16)
                throw new Exception($"Expected batch size 16 for cycle 2, got {batch4.Count}");

            Console.WriteLine("PASSED");

            // Test 2: Búfer de 3 chequeos anteriores (FIFO sliding capacity)
            Console.Write("[TEST 2] 3-burst history sliding buffer capacity (FIFO retention)... ");
            var testCoord = new NatPinCoordinator();

            var ep1 = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40001);
            var ep2 = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40002);
            var ep3 = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40003);
            var ep4 = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40004);
            var ep5 = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40005);

            testCoord.AddManualSample(ep1);
            if (testCoord.CurrentMapping.HistorySamplesCount != 1)
                throw new Exception("Expected 1 history sample.");

            testCoord.AddManualSample(ep2);
            testCoord.AddManualSample(ep3);
            if (testCoord.CurrentMapping.HistorySamplesCount != 3)
                throw new Exception($"Expected 3 history samples, got {testCoord.CurrentMapping.HistorySamplesCount}");

            // Adding 4th sample should evict ep1
            testCoord.AddManualSample(ep4);
            if (testCoord.CurrentMapping.HistorySamplesCount != 3)
                throw new Exception("Buffer exceeded maximum 3-burst capacity.");

            var hits = testCoord.CurrentMapping.AggregatedHits;
            if (hits.ContainsKey(ep1))
                throw new Exception("Sample 1 was not evicted after 4th burst.");
            if (!hits.ContainsKey(ep2) || !hits.ContainsKey(ep3) || !hits.ContainsKey(ep4))
                throw new Exception("Recent samples (2, 3, 4) were not retained in 3-burst buffer.");

            // Adding 5th sample should evict ep2
            testCoord.AddManualSample(ep5);
            hits = testCoord.CurrentMapping.AggregatedHits;
            if (hits.ContainsKey(ep2))
                throw new Exception("Sample 2 was not evicted after 5th burst.");
            if (!hits.ContainsKey(ep3) || !hits.ContainsKey(ep4) || !hits.ContainsKey(ep5))
                throw new Exception("Recent samples (3, 4, 5) were not retained in 3-burst buffer.");

            Console.WriteLine("PASSED");

            // Test 3: Fusión histórica de consenso NAT (Cone vs Symmetric)
            Console.Write("[TEST 3] 3-burst aggregated NAT consensus (Cone vs Symmetric NAT)... ");
            var coneCoord = new NatPinCoordinator();
            var stableEp = new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55000);

            // 3 consecutive identical samples -> Full Cone / Static NAT
            coneCoord.AddManualSample(stableEp, 16);
            coneCoord.AddManualSample(stableEp, 16);
            coneCoord.AddManualSample(stableEp, 16);

            var coneMap = coneCoord.CurrentMapping;
            if (coneMap.ConnectionFlags.IsNat || coneMap.ConnectionFlags.MultipleIps || coneMap.ConnectionFlags.PortMode != PortMode.Single)
                throw new Exception($"Expected a direct single-endpoint mapping, got {coneMap.ConnectionFlags}");
            if (coneMap.MostUsedPort != 55000 || coneMap.PortArray.Length != 1 || coneMap.PortArray[0] != 55000)
                throw new Exception("Port array did not match single stable port.");
            if (coneMap.DiscoveredAddresses.Length != 1 || !coneMap.DiscoveredAddresses[0].Equals(stableEp.Address))
                throw new Exception("Discovered IP addresses mismatch for stable cone mapping.");

            // Symmetric / Dynamic Port NAT scenario
            var symCoord = new NatPinCoordinator();
            symCoord.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55001), 1);
            symCoord.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55002), 1);
            symCoord.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55003), 1);

            var symMap = symCoord.CurrentMapping;
            if (!symMap.ConnectionFlags.IsNat || symMap.ConnectionFlags.PortMode != PortMode.Multiple)
                throw new Exception($"Expected a NAT mapping with multiple ports, got {symMap.ConnectionFlags}");
            if (symMap.PortArray.Length != 3 || symMap.PortArray[0] != 55001 || symMap.PortArray[2] != 55003)
                throw new Exception($"Port array was expected to span 55001..55003, got {string.Join(", ", symMap.PortArray)}");

            Console.WriteLine("PASSED");

            // Test 4: Reset mapping clears history and restores clean state
            Console.Write("[TEST 4] ResetMapping cleanly wipes 3-burst history and restores clean state... ");
            symCoord.ResetMapping();
            if (symCoord.CurrentMapping.HasMapping)
                throw new Exception("ResetMapping did not clear active mapping state.");
            if (symCoord.CurrentMapping.HistorySamplesCount != 0)
                throw new Exception("HistorySamplesCount was not reset to 0.");
            if (symCoord.GetStunHitsSnapshot().Count != 0)
                throw new Exception("GetStunHitsSnapshot still contains hits after ResetMapping.");

            Console.WriteLine("PASSED");

            // Test 5: Dynamic catalog expansion via AddServerEndpoints retains rotation continuity
            Console.Write("[TEST 5] Dynamic AddServerEndpoints preserves rotation cycle without resetting cursor... ");
            var dynCoord = new NatPinCoordinator();
            var initialSeedList = Enumerable.Range(1, 16)
                .Select(i => new IPEndPoint(IPAddress.Parse($"198.51.100.{i}"), 3478))
                .ToList();
            dynCoord.SetServerEndpoints(initialSeedList);

            // Fetch first batch of 8
            var firstBatch = dynCoord.GetNextBurstBatch(8);
            if (firstBatch.Count != 8)
                throw new Exception($"Expected initial batch of 8, got {firstBatch.Count}");

            // Now dynamically add 16 more discovered servers
            var discoveredBatch = Enumerable.Range(17, 16)
                .Select(i => new IPEndPoint(IPAddress.Parse($"198.51.100.{i}"), 3478))
                .ToList();
            dynCoord.AddServerEndpoints(discoveredBatch);

            if (dynCoord.Servers.Count != 32)
                throw new Exception($"Expected 32 total servers after dynamic expansion, got {dynCoord.Servers.Count}");

            // Fetch second batch of 8; it MUST NOT repeat any servers from firstBatch
            var secondBatch = dynCoord.GetNextBurstBatch(8);
            if (secondBatch.Count != 8)
                throw new Exception($"Expected second batch of 8, got {secondBatch.Count}");
            if (firstBatch.Intersect(secondBatch).Any())
                throw new Exception("Cursor was improperly reset by AddServerEndpoints: secondBatch overlaps with firstBatch!");

            Console.WriteLine("PASSED");

            // Test 6: StunGatherer endpoint parsing & fast initial resolution
            Console.Write("[TEST 6] StunGatherer robust parsing, seed gathering & background streaming... ");
            if (!StunGatherer.TryParseStunEndpoint("stun:stun.l.google.com:19302", out var host1, out var port1) || host1 != "stun.l.google.com" || port1 != 19302)
                throw new Exception("Failed to parse stun: scheme prefix.");

            if (!StunGatherer.TryParseStunEndpoint("turn:relay.example.com:3478", out var host2, out var port2) || host2 != "relay.example.com" || port2 != 3478)
                throw new Exception("Failed to parse turn: scheme prefix.");

            if (!StunGatherer.TryParseStunEndpoint("stun.nextcloud.com", out var host3, out var port3) || host3 != "stun.nextcloud.com" || port3 != 3478)
                throw new Exception("Failed to default omitted port to 3478.");

            if (!StunGatherer.TryParseStunEndpoint("[2001:db8::1]:3478", out var host4, out var port4) || host4 != "2001:db8::1" || port4 != 3478)
                throw new Exception("Failed to parse IPv6 bracketed endpoint.");

            var initialEndpoints = await StunGatherer.GatherInitialEndpointsAsync();
            if (initialEndpoints.IsEmpty)
                throw new Exception("GatherInitialEndpointsAsync returned empty collection.");

            // Verify background fetch fast-path streams raw IPs
            var streamedBatches = new List<IReadOnlyList<IPEndPoint>>();
            using var bgCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var bgTask = StunGatherer.StartBackgroundCatalogFetchAsync(batch =>
            {
                lock (streamedBatches)
                {
                    streamedBatches.Add(batch);
                }
            }, bgCts.Token);

            // Give background task a moment to download and yield STUN batches
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                lock (streamedBatches)
                {
                    if (streamedBatches.Count > 0 && streamedBatches.Sum(b => b.Count) >= 5)
                        break;
                }
            }

            int streamedCount = 0;
            lock (streamedBatches)
            {
                streamedCount = streamedBatches.Sum(b => b.Count);
            }

            if (streamedCount == 0)
                throw new Exception("Background catalog gathering failed to yield any streamed STUN batches.");

            Console.WriteLine($"PASSED (streamed {streamedCount} endpoints in background)");

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL NAT PIN COORDINATOR TESTS PASSED!          ");
            Console.WriteLine("==================================================");
            await Task.CompletedTask;
        }
    }
