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
            if (coneMap.NetworkType != QuicPunch.QuicPunch.NetworkType.Static)
                throw new Exception($"Expected Static NAT, got {coneMap.NetworkType}");
            if (coneMap.MostUsedPort != 55000 || coneMap.MinPort != 55000 || coneMap.MaxPort != 55000)
                throw new Exception("Port range did not match single stable port.");
            if (coneMap.DiscoveredAddresses.Length != 1 || !coneMap.DiscoveredAddresses[0].Equals(stableEp.Address))
                throw new Exception("Discovered IP addresses mismatch for stable cone mapping.");

            // Symmetric / Dynamic Port NAT scenario
            var symCoord = new NatPinCoordinator();
            symCoord.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55001), 1);
            symCoord.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55002), 1);
            symCoord.AddManualSample(new IPEndPoint(IPAddress.Parse("93.184.216.34"), 55003), 1);

            var symMap = symCoord.CurrentMapping;
            if (symMap.NetworkType != QuicPunch.QuicPunch.NetworkType.DynamicPort)
                throw new Exception($"Expected DynamicPort (Symmetric NAT), got {symMap.NetworkType}");
            if (symMap.MinPort != 55001 || symMap.MaxPort != 55003)
                throw new Exception($"Port range was expected to span 55001..55003, got {symMap.MinPort}..{symMap.MaxPort}");

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

            Console.WriteLine("==================================================");
            Console.WriteLine("   ALL NAT PIN COORDINATOR TESTS PASSED!          ");
            Console.WriteLine("==================================================");
            await Task.CompletedTask;
        }
    }
