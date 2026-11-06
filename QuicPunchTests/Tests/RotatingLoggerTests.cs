using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using QuicPunch;
using QuicPunch.Helpers;

namespace QuicPunchTests.Tests;
    public static class RotatingLoggerTests
    {
        private static async Task<string> ReadAllTextSharedAsync(string filePath)
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        public static async Task RunAsync()
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("   ROTATING FILE LOGGER & ZSTANDARD COMPRESSION    ");
            Console.WriteLine("==================================================");

            string baseTestDir = Path.Combine(Path.GetTempPath(), "qp_logger_test_" + Guid.NewGuid().ToString("N"));
            string testLogsDir = Path.Combine(baseTestDir, "logs");
            string testLogsDirGlobal = Path.Combine(baseTestDir, "logs_global");

            try
            {
                await VerifyFlushBarrier(Path.Combine(baseTestDir, "barriers"));
                await VerifyFailedWrite(Path.Combine(baseTestDir, "failed-write"));
                // Test 1: Write logs to date-formatted file in "logs" folder
                Console.Write("[TEST 1] Logging writes to date-formatted file in logs directory... ");
                var logger = new RotatingFileLogger(testLogsDir, queueCapacity: 1000);
                string todayStr = DateTime.Now.ToString("yyyy-MM-dd");
                string expectedLogFile = Path.Combine(testLogsDir, $"quicpunch_{todayStr}.log");

                logger.Write("Hello World - First Log Entry");
                logger.Write("Connection established from 192.168.1.50:5000");
                await logger.FlushAsync();

                if (!File.Exists(expectedLogFile))
                    throw new Exception($"Expected log file '{expectedLogFile}' was not created.");

                string logContent = await ReadAllTextSharedAsync(expectedLogFile);
                if (!logContent.Contains("Hello World - First Log Entry") || !logContent.Contains("Connection established from 192.168.1.50:5000"))
                    throw new Exception("Log file does not contain written log messages.");

                Console.WriteLine("PASSED");

                // Test 2: Compression of past day logs with Zstandard algorithm (.zst)
                Console.Write("[TEST 2] Rotating and compressing historical logs to .zst (Zstandard)... ");
                string pastDate = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
                string pastLogFile = Path.Combine(testLogsDir, $"quicpunch_{pastDate}.log");

                var sb = new StringBuilder();
                for (int i = 0; i < 500; i++)
                {
                    sb.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] [INFO] Handshake cycle #{i} with peer {Guid.NewGuid()} payload verified successfully.");
                }
                string originalPastText = sb.ToString();
                await File.WriteAllTextAsync(pastLogFile, originalPastText);

                long uncompressedSize = new FileInfo(pastLogFile).Length;

                // Trigger compression of historical logs
                await logger.CompressOldLogsSafeAsync();

                string compressedFile = pastLogFile + ".zst";
                if (!File.Exists(compressedFile))
                    throw new Exception($"Zstandard compressed file '{compressedFile}' was not found.");

                if (File.Exists(pastLogFile))
                    throw new Exception($"Uncompressed original file '{pastLogFile}' should have been removed after compression.");

                long compressedSize = new FileInfo(compressedFile).Length;
                if (compressedSize >= uncompressedSize)
                    throw new Exception($"Compressed size ({compressedSize}) is not smaller than original ({uncompressedSize}).");

                Console.WriteLine($"PASSED (Original: {uncompressedSize:N0} bytes -> Zstandard: {compressedSize:N0} bytes, Ratio: {(double)uncompressedSize / compressedSize:F1}x)");

                // Test 3: Decompress Zstandard file and verify data integrity
                Console.Write("[TEST 3] Zstandard decompression and byte-for-byte content integrity... ");
                string decompressedFile = Path.Combine(testLogsDir, $"decompressed_{pastDate}.log");
                await RotatingFileLogger.DecompressZstandardToFileAsync(compressedFile, decompressedFile);

                string decompressedText = await File.ReadAllTextAsync(decompressedFile);
                if (decompressedText != originalPastText)
                    throw new Exception("Decompressed log content does not match the original uncompressed log text!");

                // Also verify DecompressLogFileAsync unified dispatcher
                string decompressedUnified = Path.Combine(testLogsDir, $"decompressed_unified_{pastDate}.log");
                await RotatingFileLogger.DecompressLogFileAsync(compressedFile, decompressedUnified);
                string decompressedUnifiedText = await File.ReadAllTextAsync(decompressedUnified);
                if (decompressedUnifiedText != originalPastText)
                    throw new Exception("Unified DecompressLogFileAsync content does not match original log text!");

                Console.WriteLine("PASSED");

                // Cleanly dispose manual test logger
                await logger.DisposeAsync();

                // Test 4: QuicPunchLog global static logger writes to logs folder
                Console.Write("[TEST 4] QuicPunchLog global static logger writes to logs folder... ");
                QuicPunchLog.LogDirectory = testLogsDirGlobal;
                string expectedGlobalLogFile = Path.Combine(testLogsDirGlobal, $"quicpunch_{todayStr}.log");

                QuicPunchLog.Info("Integration test info message via QuicPunchLog");
                QuicPunchLog.Error("Integration test error message via QuicPunchLog", new InvalidOperationException("Test socket failure"));
                await QuicPunchLog.FlushAsync();

                if (!File.Exists(expectedGlobalLogFile))
                    throw new Exception($"Expected global log file '{expectedGlobalLogFile}' was not created.");

                string currentLog = await ReadAllTextSharedAsync(expectedGlobalLogFile);
                if (!currentLog.Contains("Integration test info message via QuicPunchLog") ||
                    !currentLog.Contains("Integration test error message via QuicPunchLog") ||
                    !currentLog.Contains("Test socket failure"))
                {
                    throw new Exception("QuicPunchLog messages were not flushed to active log file.");
                }
                Console.WriteLine("PASSED");

                // Dispose the global logger and reset to default
                await QuicPunchLog.FileLogger.DisposeAsync();
                QuicPunchLog.FileLogger = null!;

                Console.WriteLine("==================================================");
                Console.WriteLine("   ALL ROTATING LOGGER TESTS PASSED SUCCESSFULLY! ");
                Console.WriteLine("==================================================");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(baseTestDir))
                        Directory.Delete(baseTestDir, true);
                }
                catch { }
            }
        }

        private sealed class DelayedStream : MemoryStream
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
                await base.WriteAsync(bytes, ct);
            }
        }

        private static async Task VerifyFailedWrite(string directory)
        {
            using var sink = new DelayedStream();
            await using var logger = new RotatingFileLogger(directory, 100, _ => sink);
            try
            {
                logger.Write(new string('F', 131072));
                await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task first = logger.FlushAsync(), second = logger.FlushAsync();
                Task closing = logger.DisposeAsync().AsTask();
                sink.Release.TrySetException(new IOException("Injected disk write failure"));
                foreach (Task barrier in new[] { first, second })
                {
                    try { await barrier.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("A failed write completed its barrier successfully."); }
                    catch (IOException ex) when (ex.Message == "Injected disk write failure") { }
                }
                await closing.WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine("PASS: disk failure faults all accepted Flush barriers and concurrent disposal completes.");
            }
            finally { sink.Release.TrySetResult(); }
        }

        private static async Task VerifyFlushBarrier(string directory)
        {
            var streams = new System.Collections.Concurrent.ConcurrentBag<MemoryStream>();
            using var delayed = new DelayedStream();
            int opens = 0;
            await using var logger = new RotatingFileLogger(directory, 100, _ =>
            {
                MemoryStream stream = Interlocked.Increment(ref opens) == 1 ? delayed : new MemoryStream();
                streams.Add(stream); return stream;
            });
            try
            {
                string line = new('A', 131072);
                logger.Write(line);
                await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task firstFlush = logger.FlushAsync();
                Task secondFlush = logger.FlushAsync();
                if (firstFlush.IsCompleted || secondFlush.IsCompleted)
                    throw new Exception("Flush returned before the dequeued disk write completed.");
                // Saturation must not discard either of the queued flush barriers.
                for (int i = 0; i < 1000; i++) logger.Write("queued " + i);
                Task rotation = logger.RotateAndCompressAsync();
                Task move = Task.Run(() => logger.LogDirectory = Path.Combine(directory, "next"));
                delayed.Release.TrySetResult();
                await Task.WhenAll(firstFlush, secondFlush, rotation, move).WaitAsync(TimeSpan.FromSeconds(5));
                logger.Write("after-directory-change");
                await logger.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                logger.Write("drained-on-dispose");
                await logger.DisposeAsync();
                string content = string.Concat(streams.Select(s => Encoding.UTF8.GetString(s.ToArray())));
                if (!content.Contains(line) || !content.Contains("after-directory-change") || !content.Contains("drained-on-dispose"))
                    throw new Exception("The writer lost admitted data across flush/rotation/directory change.");
                Console.WriteLine("PASS: pending IO, concurrent Flush barriers, queue overload, rotation, directory change and graceful drain.");
            }
            finally { delayed.Release.TrySetResult(); }
        }
    }
