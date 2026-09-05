using System.Diagnostics;
using System.Net;
using QuicPunchTests.Protocols;

namespace QuicPunchTests.Tests;

public static class VirtualLanPoolingTests
{
public static async Task RunAsync()
{
Console.WriteLine("==================================================");
Console.WriteLine("     VIRTUAL LAN ZERO-ALLOCATION POOLING TESTS    ");
Console.WriteLine("==================================================");

await TestRefCountingAndDisposalAsync();
await TestQueueCapacityAndCleanDroppingAsync();
await TestMultiPeerBroadcastSharingAsync();
await TestZeroAllocationPumpingAsync();

Console.WriteLine("==================================================");
Console.WriteLine("   ALL VIRTUAL LAN POOLING TESTS PASSED!          ");
Console.WriteLine("==================================================");
}

private static Task TestRefCountingAndDisposalAsync()
{
Console.Write("[TEST 1] PooledPacket ref-counting and ArrayPool recycling... ");

byte[] payload = new byte[1420];
for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);

var packet = PooledPacket.Create(payload);
if (packet.Length != payload.Length)
throw new Exception($"Length mismatch: expected {payload.Length}, got {packet.Length}");

if (!packet.Span.SequenceEqual(payload))
throw new Exception("Span content mismatch");

// Add 2 additional references (total 3)
packet.AddRef();
packet.AddRef();

// Dispose 1st ref: buffer should still be valid
packet.Dispose();
if (packet.Length != 1420)
throw new Exception("Packet prematurely invalidated on first dispose");

// Dispose 2nd ref: buffer should still be valid
packet.Dispose();
if (packet.Length != 1420)
throw new Exception("Packet prematurely invalidated on second dispose");

// Dispose 3rd ref: buffer is returned to pool
packet.Dispose();

// Create a new packet: should reuse an instance from instance pool
var reused = PooledPacket.Create(payload.AsSpan(0, 500));
if (reused.Length != 500)
throw new Exception("Reused packet invalid length");
reused.Dispose();

Console.WriteLine("PASSED");
return Task.CompletedTask;
}

private static async Task TestQueueCapacityAndCleanDroppingAsync()
{
Console.Write("[TEST 2] PooledPacketQueue capacity, FIFO drop & clean disposal... ");

const int capacity = 16;
using var queue = new PooledPacketQueue(capacity);
long droppedCount = 0;

byte[] data = new byte[100];

// Enqueue exactly 16 packets
for (int i = 0; i < capacity; i++)
{
data[0] = (byte)i;
var packet = PooledPacket.Create(data);
queue.Enqueue(packet, ref droppedCount);
}

if (queue.Count != capacity)
throw new Exception($"Expected queue count {capacity}, got {queue.Count}");
if (droppedCount != 0)
throw new Exception($"Expected 0 dropped, got {droppedCount}");

// Now enqueue 5 more packets (they should evict the oldest 5 packets: 0..4)
for (int i = capacity; i < capacity + 5; i++)
{
data[0] = (byte)i;
var packet = PooledPacket.Create(data);
queue.Enqueue(packet, ref droppedCount);
}

if (queue.Count != capacity)
throw new Exception($"Queue count should remain {capacity}, got {queue.Count}");
if (droppedCount != 5)
throw new Exception($"Expected 5 dropped, got {droppedCount}");

// Dequeue first available: should be packet with payload[0] == 5
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var first = await queue.DequeueAsync(cts.Token);
if (first == null)
throw new Exception("Dequeued packet was null");

if (first.Span[0] != 5)
throw new Exception($"Expected oldest surviving packet to have id 5, got {first.Span[0]}");

first.Dispose();

// Empty the queue
while (queue.Count > 0)
{
var p = await queue.DequeueAsync(cts.Token);
p?.Dispose();
}

if (queue.Count != 0)
throw new Exception("Queue should be empty");

Console.WriteLine("PASSED");
}

private static async Task TestMultiPeerBroadcastSharingAsync()
{
Console.Write("[TEST 3] Multi-peer broadcast single-buffer sharing & atomic ref recycling... ");

const int peerCount = 5;
var queues = new PooledPacketQueue[peerCount];
for (int i = 0; i < peerCount; i++)
queues[i] = new PooledPacketQueue(64);

long[] droppedCounters = new long[peerCount];

try
{
byte[] broadcastData = new byte[1200];
Random.Shared.NextBytes(broadcastData);

// Broadcast pattern: create 1 PooledPacket with refCount = 1
using (var packet = PooledPacket.Create(broadcastData))
{
// Enqueue to all 5 peers
for (int i = 0; i < peerCount; i++)
{
packet.AddRef();
queues[i].Enqueue(packet, ref droppedCounters[i]);
}
} // Using block exits, decrements the base reference. Active refCount is now peerCount (5).

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

// All peers dequeue and verify identical content
for (int i = 0; i < peerCount; i++)
{
var dequeued = await queues[i].DequeueAsync(cts.Token);
if (dequeued == null)
throw new Exception($"Peer {i} received null packet");

if (!dequeued.Span.SequenceEqual(broadcastData))
throw new Exception($"Peer {i} received corrupted packet data");

// Each peer disposes its reference
dequeued.Dispose();
}

// After all 5 peers dispose, the underlying buffer has been returned to ArrayPool.
// Verify all queues are empty
for (int i = 0; i < peerCount; i++)
{
if (queues[i].Count != 0)
throw new Exception($"Peer {i} queue not empty");
}
}
finally
{
for (int i = 0; i < peerCount; i++)
queues[i].Dispose();
}

Console.WriteLine("PASSED");
}

private static async Task TestZeroAllocationPumpingAsync()
{
Console.Write("[TEST 4] Zero-allocation steady-state packet pumping verification... ");

using var queue = new PooledPacketQueue(256);
long dropped = 0;
byte[] samplePacket = new byte[1420];
Random.Shared.NextBytes(samplePacket);

using var cts = new CancellationTokenSource();

// Warm up pools and JIT
for (int i = 0; i < 500; i++)
{
var p = PooledPacket.Create(samplePacket);
queue.Enqueue(p, ref dropped);
var d = await queue.DequeueAsync(cts.Token);
d?.Dispose();
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

const int iterations = 10_000;
for (int i = 0; i < iterations; i++)
{
var p = PooledPacket.Create(samplePacket);
queue.Enqueue(p, ref dropped);
var d = await queue.DequeueAsync(cts.Token);
d?.Dispose();
}

long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
long totalAllocated = allocatedAfter - allocatedBefore;
double bytesPerPacket = (double)totalAllocated / iterations;

// In steady state with ArrayPool and PooledPacket reuse, average allocation is negligible (< 32 bytes/packet for any async state machines)
if (bytesPerPacket > 32)
{
throw new Exception($"Allocations too high: {bytesPerPacket:F1} bytes/packet (Total: {totalAllocated} bytes for {iterations} packets)");
}

Console.WriteLine($"PASSED (Measured: {bytesPerPacket:F2} bytes/packet across {iterations:N0} packets)");
}
}

