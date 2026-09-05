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
        private readonly Channel<string> _channel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _workerTask;
        private readonly object _lock = new();

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
                lock (_lock)
                {
                    if (string.IsNullOrWhiteSpace(value))
                        throw new ArgumentException("LogDirectory cannot be empty.", nameof(value));

                    string newDir = Path.GetFullPath(value);
                    if (newDir != _logDirectory)
                    {
                        if (_currentWriter != null)
                        {
                            try
                            {
                                _currentWriter.Flush();
                                _currentWriter.Dispose();
                            }
                            catch { }
                            _currentWriter = null;
                            _currentFilePath = null;
                            _currentDateStr = null;
                        }
                        _logDirectory = newDir;
                        Directory.CreateDirectory(_logDirectory);
                    }
                }
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
        {
            string baseDir = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
            _logDirectory = Path.GetFullPath(baseDir);
            Directory.CreateDirectory(_logDirectory);

            var channelOptions = new BoundedChannelOptions(Math.Max(100, queueCapacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            };
            _channel = Channel.CreateBounded<string>(channelOptions);
            _cts = new CancellationTokenSource();

            _workerTask = Task.Run(ProcessLogQueueAsync);

            // Compress any uncompressed leftover logs from previous dates on startup
            _ = Task.Run(() => CompressOldLogsSafeAsync(CancellationToken.None));
        }

        public void Write(string message)
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return;

            _channel.Writer.TryWrite(message);
        }

        public void Flush()
        {
            FlushAsync().GetAwaiter().GetResult();
        }

        public async Task FlushAsync()
        {
            if (Volatile.Read(ref _isDisposed) != 0)
                return;

            int spinCount = 0;
            while (_channel.Reader.Count > 0 && spinCount++ < 200)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }

            lock (_lock)
            {
                _currentWriter?.Flush();
            }
        }

        private async Task ProcessLogQueueAsync()
        {
            var reader = _channel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var message))
                    {
                        EnsureWriterReady();

                        if (_currentWriter != null)
                        {
                            await _currentWriter.WriteLineAsync(message.AsMemory(), _cts.Token).ConfigureAwait(false);
                            _currentFileLength += Encoding.UTF8.GetByteCount(message) + Environment.NewLine.Length;
                        }
                    }

                    if (_currentWriter != null)
                    {
                        await _currentWriter.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RotatingFileLogger] Worker error: {ex.Message}");
            }
            finally
            {
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

                FileStream? stream = null;
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        stream = new FileStream(
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
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            RotateWriter(today);
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

                var logFiles = Directory.GetFiles(_logDirectory, $"{_filePrefix}_*.log");
                foreach (var logFile in logFiles)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.Equals(logFile, activeFile, StringComparison.OrdinalIgnoreCase))
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
                await using (var sourceStream = new FileStream(sourceLogPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, useAsync: true))
                await using (var destStream = new FileStream(tempCompressedPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536, useAsync: true))
                await using (var zstdStream = new ZstandardStream(destStream, level, leaveOpen: false))
                {
                    await sourceStream.CopyToAsync(zstdStream, 65536, ct).ConfigureAwait(false);
                }

                if (File.Exists(destCompressedPath))
                    File.Delete(destCompressedPath);

                File.Move(tempCompressedPath, destCompressedPath);
                File.Delete(sourceLogPath);
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

        [Obsolete("Use CompressFileToZstandardAsync instead.")]
        public static Task CompressFileToBrotliAsync(string sourceLogPath, CancellationToken ct = default, CompressionLevel level = CompressionLevel.Optimal)
            => CompressFileToZstandardAsync(sourceLogPath, ct, level);

        [Obsolete("Use DecompressZstandardToFileAsync instead.")]
        public static Task DecompressBrotliToFileAsync(string sourceCompressedPath, string destinationLogPath, CancellationToken ct = default)
            => DecompressLogFileAsync(sourceCompressedPath, destinationLogPath, ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _channel.Writer.TryComplete();
            _cts.Cancel();
            try { _workerTask.GetAwaiter().GetResult(); } catch { }
            _cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _channel.Writer.TryComplete();
            _cts.Cancel();
            try { await _workerTask.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}
