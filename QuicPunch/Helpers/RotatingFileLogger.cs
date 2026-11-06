using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace QuicPunch.Helpers
{
    public sealed class RotatingFileLogger : IAsyncDisposable, IDisposable
    {
        private sealed record Command(string? Message = null, string? Directory = null, bool Rotate = false,
            TaskCompletionSource? Completion = null);
        private readonly Channel<Command> _channel;
        private readonly Task _workerTask;
        private readonly object _lock = new();
        private readonly Func<string, Stream>? _streamFactory;

        private string _logDirectory;
        private string _filePrefix = "quicpunch";
        private long _maxFileSizeBytes = 50 * 1024 * 1024; // 50 MB
        private CompressionLevel _compressionLevel = CompressionLevel.Optimal;

        private StreamWriter? _currentWriter;
        private string? _currentFilePath;
        private string? _currentDateStr;
        private long _currentFileLength;
        private int _isDisposed;

        public string LogDirectory
        {
            get => _logDirectory;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("LogDirectory cannot be empty.", nameof(value));
                string newDir = Path.GetFullPath(value);
                Directory.CreateDirectory(newDir); // Validate before asking the writer to leave its current directory.
                QueueControlAsync(directory: newDir).GetAwaiter().GetResult();
            }
        }

        public string FilePrefix
        {
            get => _filePrefix;
            set => _filePrefix = string.IsNullOrWhiteSpace(value) ? "quicpunch" : value;
        }

        public long MaxFileSizeBytes
        {
            get => _maxFileSizeBytes;
            set => _maxFileSizeBytes = Math.Max(0, value);
        }

        public CompressionLevel CompressionLevel
        {
            get => _compressionLevel;
            set => _compressionLevel = value;
        }

        public RotatingFileLogger(string? logDirectory = null, int queueCapacity = 10_000)
            : this(logDirectory, queueCapacity, null) { }

        internal RotatingFileLogger(string? logDirectory, int queueCapacity, Func<string, Stream>? streamFactory)
        {
            _streamFactory = streamFactory;
            string baseDir;
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                baseDir = logDirectory;
            }
            else if (OperatingSystem.IsAndroid())
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                baseDir = Path.Combine(string.IsNullOrEmpty(localApp) ? Path.GetTempPath() : localApp, "logs");
            }
            else
            {
                baseDir = Path.Combine(AppContext.BaseDirectory, "logs");
            }

            _logDirectory = Path.GetFullPath(baseDir);
            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch
            {
                try
                {
                    _logDirectory = Path.Combine(Path.GetTempPath(), "quicpunch_logs");
                    Directory.CreateDirectory(_logDirectory);
                }
                catch { }
            }

            var channelOptions = new BoundedChannelOptions(Math.Max(100, queueCapacity))
            {
                SingleReader = true,
                SingleWriter = false,
                // Data writes drop on overload; control barriers must never be evicted.
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<Command>(channelOptions);

            _workerTask = Task.Run(ProcessLogQueueAsync);

            // Compress any uncompressed leftover logs from previous dates on startup
            _ = Task.Run(() => CompressOldLogsSafeAsync(CancellationToken.None));
        }

        public void Write(string message)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return;

            _channel.Writer.TryWrite(new(Message: message));
        }

        public void Flush()
        {
            FlushAsync().GetAwaiter().GetResult();
        }

        public Task FlushAsync()
        {
            return Volatile.Read(ref _isDisposed) != 0 ? _workerTask : QueueControlAsync();
        }

        private async Task QueueControlAsync(string? directory = null, bool rotate = false, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _channel.Writer.WriteAsync(new(Directory: directory, Rotate: rotate, Completion: completion), ct).ConfigureAwait(false);
            await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        private async Task ProcessLogQueueAsync()
        {
            var reader = _channel.Reader;
            Command? current = null;
            Exception? failure = null;
            try
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out current))
                    {
                        if (current.Message is { } message)
                        {
                            EnsureWriterReady();
                            await _currentWriter!.WriteLineAsync(message.AsMemory()).ConfigureAwait(false);
                            _currentFileLength += Encoding.UTF8.GetByteCount(message) + Environment.NewLine.Length;
                        }
                        else
                        {
                            if (_currentWriter != null) await _currentWriter.FlushAsync().ConfigureAwait(false);
                            if (current.Directory is { } directory && directory != _logDirectory)
                            {
                                if (_currentWriter != null) await _currentWriter.DisposeAsync().ConfigureAwait(false);
                                lock (_lock)
                                {
                                    _currentWriter = null; _currentFilePath = null; _currentDateStr = null;
                                    _logDirectory = directory;
                                }
                            }
                            if (current.Rotate) RotateWriter(DateTime.Now.ToString("yyyy-MM-dd"));
                            current.Completion!.TrySetResult();
                        }
                        current = null;
                    }

                    if (_currentWriter != null)
                    {
                        await _currentWriter.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                current?.Completion?.TrySetException(ex);
                Console.Error.WriteLine($"[RotatingFileLogger] Worker error: {ex.Message}");
            }
            finally
            {
                _channel.Writer.TryComplete(failure);
                while (reader.TryRead(out var abandoned))
                    abandoned.Completion?.TrySetException(failure ?? new ObjectDisposedException(nameof(RotatingFileLogger)));
                lock (_lock)
                {
                    try
                    {
                        _currentWriter?.Flush();
                        _currentWriter?.Dispose();
                    }
                    catch { }
                    _currentWriter = null;
                }
            }
        }

        private void EnsureWriterReady()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");

            bool needsRotation = _currentWriter == null
                                 || today != _currentDateStr
                                 || (_maxFileSizeBytes > 0 && _currentFileLength >= _maxFileSizeBytes);

            if (needsRotation)
            {
                RotateWriter(today);
            }
        }

        private void RotateWriter(string today)
        {
            lock (_lock)
            {
                string? previousLogFile = _currentFilePath;

                if (_currentWriter != null)
                {
                    try
                    {
                        _currentWriter.Flush();
                        _currentWriter.Dispose();
                    }
                    catch { }
                    _currentWriter = null;
                }

                Directory.CreateDirectory(_logDirectory);

                string fileName = $"{_filePrefix}_{today}.log";
                string fullPath = Path.Combine(_logDirectory, fileName);

                if (today == _currentDateStr && File.Exists(fullPath) && _maxFileSizeBytes > 0)
                {
                    int index = 1;
                    while (File.Exists(fullPath))
                    {
                        fileName = $"{_filePrefix}_{today}_{index:D3}.log";
                        fullPath = Path.Combine(_logDirectory, fileName);
                        index++;
                    }
                }

                Stream? stream = null;
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        stream = _streamFactory?.Invoke(fullPath) ?? new FileStream(
                            fullPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite,
                            bufferSize: 65536,
                            useAsync: true);
                        break;
                    }
                    catch (IOException) when (retry < 4)
                    {
                        Thread.Sleep(50);
                    }
                }

                if (stream != null)
                {
                    _currentWriter = new StreamWriter(stream, Encoding.UTF8, bufferSize: 65536)
                    {
                        AutoFlush = false
                    };
                    _currentFilePath = fullPath;
                    _currentDateStr = today;
                    _currentFileLength = stream.Length;
                }

                // When rotating away from a previous file, compress old inactive logs in background
                if (previousLogFile != null && previousLogFile != fullPath)
                {
                    _ = Task.Run(() => CompressOldLogsSafeAsync(CancellationToken.None));
                }
            }
        }

        public async Task RotateAndCompressAsync(CancellationToken ct = default)
        {
            await QueueControlAsync(rotate: true, ct: ct).ConfigureAwait(false);
            await CompressOldLogsSafeAsync(ct).ConfigureAwait(false);
        }

        public async Task CompressOldLogsSafeAsync(CancellationToken ct = default)
        {
            try
            {
                string activeFile;
                lock (_lock)
                {
                    activeFile = _currentFilePath ?? string.Empty;
                }

                if (!Directory.Exists(_logDirectory))
                    return;

                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string todayActiveName = $"{_filePrefix}_{today}.log";

                var logFiles = Directory.GetFiles(_logDirectory, $"{_filePrefix}_*.log");
                foreach (var logFile in logFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.Equals(logFile, activeFile, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Never compress today's active non-rotated log file
                    if (Path.GetFileName(logFile).Equals(todayActiveName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    await CompressFileToZstandardAsync(logFile, ct, _compressionLevel).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RotatingFileLogger] Compression error: {ex.Message}");
            }
        }

        public static async Task CompressFileToZstandardAsync(string sourceLogPath, CancellationToken ct = default, CompressionLevel level = CompressionLevel.Optimal)
        {
            if (!File.Exists(sourceLogPath))
                return;

            string destCompressedPath = sourceLogPath + ".zst";
            string tempCompressedPath = destCompressedPath + ".tmp";

            try
            {
                await using (var sourceStream = new FileStream(sourceLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 65536, useAsync: true))
                await using (var destStream = new FileStream(tempCompressedPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                await using (var zstdStream = new ZstandardStream(destStream, level, leaveOpen: false))
                {
                    await sourceStream.CopyToAsync(zstdStream, 65536, ct).ConfigureAwait(false);
                }

                if (File.Exists(destCompressedPath))
                    File.Delete(destCompressedPath);

                File.Move(tempCompressedPath, destCompressedPath);
                try
                {
                    File.Delete(sourceLogPath);
                }
                catch (IOException)
                {
                    // File might be opened by external reader; left to be cleaned on next rotation
                }
            }
            catch (IOException)
            {
                // File locked by active writer or another process; leave uncompressed until idle
                try
                {
                    if (File.Exists(tempCompressedPath))
                        File.Delete(tempCompressedPath);
                }
                catch { }
            }
            catch
            {
                try
                {
                    if (File.Exists(tempCompressedPath))
                        File.Delete(tempCompressedPath);
                }
                catch { }
                throw;
            }
        }

        public static async Task DecompressZstandardToFileAsync(string sourceCompressedPath, string destinationLogPath, CancellationToken ct = default)
        {
            if (!File.Exists(sourceCompressedPath))
                throw new FileNotFoundException("Source compressed file not found.", sourceCompressedPath);

            await using var sourceStream = new FileStream(sourceCompressedPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
            await using var zstdStream = new ZstandardStream(sourceStream, CompressionMode.Decompress, leaveOpen: false);
            await using var destStream = new FileStream(destinationLogPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);

            await zstdStream.CopyToAsync(destStream, 65536, ct).ConfigureAwait(false);
        }

        public static async Task DecompressLogFileAsync(string sourceCompressedPath, string destinationLogPath, CancellationToken ct = default)
        {
            if (sourceCompressedPath.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            {
                await using var sourceStream = new FileStream(sourceCompressedPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true);
                await using var brotliStream = new BrotliStream(sourceStream, CompressionMode.Decompress, leaveOpen: false);
                await using var destStream = new FileStream(destinationLogPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true);
                await brotliStream.CopyToAsync(destStream, 65536, ct).ConfigureAwait(false);
                return;
            }

            await DecompressZstandardToFileAsync(sourceCompressedPath, destinationLogPath, ct).ConfigureAwait(false);
        }


        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0) _channel.Writer.TryComplete();
            await _workerTask.ConfigureAwait(false); // Drain accepted data and barriers before closing the writer.
        }
    }
}
