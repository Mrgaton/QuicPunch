using System.Security.Cryptography;
using System.Diagnostics;

namespace QuicNet.Protocols;

/// <summary>A single ordered writer. Only verified content can become a final download.</summary>
internal sealed class ChatFileDownload : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _expectedHash;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private bool _closed;
    private bool _completed;
    private bool _keepPartial;
    private bool _restored;
    private readonly ChatTransferStore? _store;
    private ChatTransferRecord? _record;
    private long _checkpointBytes;
    private long _lastCheckpoint = Stopwatch.GetTimestamp();
    private long _lastActivity = Stopwatch.GetTimestamp();

    public Guid PeerId { get; }
    public Guid FileId { get; }
    public string FileName { get; }
    public long TotalSize { get; }
    public string TempPath { get; }
    public string FinalPath { get; }
    public string Sha256 { get; }
    public long ReceivedBytes { get; private set; }
    public bool IsRestoring => !_restored;
    public TimeSpan Inactivity => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastActivity));
    public DateTime LastProgressBroadcastUtc { get; set; } = DateTime.MinValue;

    internal ChatFileDownload(Guid peerId, Guid fileId, string fileName, long totalSize,
        string sha256, string tempPath, string finalPath, FileStream stream,
        ChatTransferStore? store = null, ChatTransferRecord? record = null)
    {
        if (totalSize < 0 || !ChatHandler.IsValidFileHash(sha256)) throw new InvalidDataException("Invalid file manifest.");
        PeerId = peerId;
        FileId = fileId;
        FileName = fileName;
        TotalSize = totalSize;
        Sha256 = sha256;
        _expectedHash = Convert.FromHexString(sha256);
        TempPath = tempPath;
        FinalPath = finalPath;
        _stream = stream;
        _store = store;
        _record = record;
        _restored = record == null || record.Received == 0;
        ReceivedBytes = _checkpointBytes = record?.Received ?? 0;
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_record == null) return;
            long saved = _record.Received;
            if (saved < 0 || saved > TotalSize || _stream.Length < saved)
                throw new InvalidDataException("The saved download is truncated or invalid. Start a new download.");
            _stream.SetLength(saved); // Discard any tail written after the last durable checkpoint.
            _stream.Position = 0;
            byte[] buffer = new byte[1024 * 1024];
            long remaining = saved;
            while (remaining > 0)
            {
                int read = await _stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), lifetime.Token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The saved download ended early.");
                _hash.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
                Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            }
            if (!string.Equals(Convert.ToHexString(_hash.GetCurrentHash()), _record.PrefixHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The saved download failed its integrity check. Start a new download.");
            ReceivedBytes = _checkpointBytes = saved;
            _restored = true;
        }
        finally { _gate.Release(); }
    }

    private async Task CheckpointAsync()
    {
        if (_store == null || _record == null || _completed || !_restored) return;
        _stream.SetLength(ReceivedBytes);
        await _stream.FlushAsync().ConfigureAwait(false);
        _stream.Flush(flushToDisk: true);
        // Commit the offset only after the corresponding bytes are durable.
        _record = _record with { Received = ReceivedBytes, PrefixHash = Convert.ToHexString(_hash.GetCurrentHash()) };
        _store.Save(_record);
        _checkpointBytes = ReceivedBytes;
        _lastCheckpoint = Stopwatch.GetTimestamp();
    }

    public async Task PauseAsync()
    {
        Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _keepPartial = _store != null;
            if (!_closed)
            {
                try { await CheckpointAsync().ConfigureAwait(false); }
                finally
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _closed = true;
                }
            }
        }
        finally { _gate.Release(); }
        await DisposeAsync().ConfigureAwait(false);
    }

    public async Task<bool> WriteChunkAsync(Guid peerId, long offset, ReadOnlyMemory<byte> data, bool isEnd, CancellationToken ct)
    {
        if (peerId != PeerId) throw new InvalidDataException("The file chunk belongs to another peer.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (_closed) throw new InvalidOperationException("The download is already closed.");
            // A cancelled upload can still have frames in flight when a resume request is sent.
            // These bytes are already hashed; only the exact next range can extend the file.
            if (_store != null && _restored && !isEnd && offset >= 0 && offset < ReceivedBytes &&
                data.Length > 0 && data.Length <= ChatHandler.FileChunkBytes && data.Length <= ReceivedBytes - offset)
                return false;
            if (offset != ReceivedBytes || data.Length > ChatHandler.FileChunkBytes || data.Length > TotalSize - ReceivedBytes ||
                (data.IsEmpty && !isEnd) || (isEnd && data.Length != TotalSize - ReceivedBytes))
                throw new InvalidDataException("Invalid file chunk position, length or end marker.");

            await _stream.WriteAsync(data, lifetime.Token).ConfigureAwait(false);
            Volatile.Write(ref _lastActivity, Stopwatch.GetTimestamp());
            _hash.AppendData(data.Span);
            ReceivedBytes += data.Length;
            if (!isEnd)
            {
                if (ReceivedBytes - _checkpointBytes >= 64L * 1024 * 1024 ||
                    Stopwatch.GetElapsedTime(_lastCheckpoint) >= TimeSpan.FromSeconds(5))
                    await CheckpointAsync().ConfigureAwait(false);
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(_hash.GetCurrentHash(), _expectedHash))
                throw new InvalidDataException("The downloaded file failed its SHA-256 integrity check.");
            await _stream.FlushAsync(lifetime.Token).ConfigureAwait(false);
            _stream.Flush(flushToDisk: true);
            lifetime.Token.ThrowIfCancellationRequested();
            await _stream.DisposeAsync().ConfigureAwait(false);
            _closed = true;
            lifetime.Token.ThrowIfCancellationRequested();
            // Never remove or overwrite another file that appeared during the transfer.
            File.Move(TempPath, FinalPath, overwrite: false);
            _completed = true;
            if (_store != null && _record != null)
                _store.Save(_record with { Received = ReceivedBytes, Completed = true });
            return true;
        }
        finally { _gate.Release(); }
    }

    public void Cancel() => _cancellation.Cancel();

    public async ValueTask DisposeAsync()
    {
        Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_closed) { await _stream.DisposeAsync().ConfigureAwait(false); _closed = true; }
            _hash.Dispose();
            if (!_completed && !_keepPartial)
            {
                try { File.Delete(TempPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _store?.Delete(PeerId, FileId);
            }
        }
        finally { _gate.Release(); }
    }
}
