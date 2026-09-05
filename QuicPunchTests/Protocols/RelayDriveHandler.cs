using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuicPunch;
using QuicConnection = QuicPunch.QuicConnection;

namespace QuicPunchTests.Protocols;

/// <summary>
/// RelayDrive is an intentionally small, ephemeral file shelf. Peers exchange only manifests;
/// file bytes are transferred on demand and materialized into a temporary local cache.
/// </summary>
internal sealed class RelayDriveHandler : QuicPunch.QuicPunch.IProtocolHandler, IDisposable
{
    private const int MaxPublishedFiles = 128;
    private const int MaxManifestBytes = 256 * 1024;
    private const int ChunkBytes = 64 * 1024;
    private const int MaxConcurrentDownloads = 8;
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    internal enum FrameType : byte
    {
        Manifest = 1,
        Request = 2,
        FileStart = 3,
        FileChunk = 4,
        FileEnd = 5,
        Error = 6
    }

    internal sealed record PublishedFile(Guid Id, string Name, string Path, long Size, DateTime LastWriteUtc);
    internal sealed record RemoteFile(Guid PeerId, string PeerName, Guid Id, string Name, long Size);
    internal sealed record CachedFile(Guid PeerId, string PeerName, Guid Id, string Name, string Path, long Size, DateTime MaterializedUtc);
    internal sealed record TransferProgress(Guid PeerId, Guid FileId, string Name, long Received, long ExpectedSize);

    private sealed class DownloadState : IDisposable
    {
        public Guid PeerId { get; }
        public Guid FileId { get; }
        public string Name { get; }
        public long ExpectedSize { get; }
        public string TempPath { get; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileStream? Stream { get; set; }
        public IncrementalHash? Hash { get; set; }
        public long Received;

        public DownloadState(Guid peerId, Guid fileId, string name, long expectedSize, string tempPath)
        {
            PeerId = peerId;
            FileId = fileId;
            Name = name;
            ExpectedSize = expectedSize;
            TempPath = tempPath;
        }

        public void Dispose()
        {
            try { Stream?.Dispose(); } catch { }
            try { Hash?.Dispose(); } catch { }
            Stream = null;
            Hash = null;
        }
    }

    internal sealed class RelayDriveSession : IDisposable
    {
        private readonly RelayDriveHandler _owner;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _sendSlots = new(2, 2);
        private readonly ConcurrentDictionary<Guid, byte> _queuedTransfers = new();
        private int _disposed;

        public PeerInfo Peer { get; }
        public Stream Stream { get; }
        public QuicConnection Connection { get; }

        public RelayDriveSession(RelayDriveHandler owner, PeerInfo peer, Stream stream, QuicConnection connection)
        {
            _owner = owner;
            Peer = peer;
            Stream = stream;
            Connection = connection;
        }

        public async Task SendFrameAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            if (payload.Length > ChunkBytes + 64 * 1024)
                throw new InvalidDataException("RelayDrive frame is too large.");

            byte[] header = new byte[5];
            header[0] = (byte)type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(header, ct).ConfigureAwait(false);
                if (!payload.IsEmpty)
                    await Stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await Stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public bool TryQueueTransfer(Guid fileId)
        {
            if (fileId == Guid.Empty || _queuedTransfers.Count >= 32) return false;
            return _queuedTransfers.TryAdd(fileId, 0);
        }

        public void CompleteQueuedTransfer(Guid fileId) => _queuedTransfers.TryRemove(fileId, out _);

        private Task SendErrorAsync(Guid fileId, string error, CancellationToken ct)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id = fileId, error });
            return SendFrameAsync(FrameType.Error, payload, ct);
        }

        public async Task SendPublishedFileAsync(Guid fileId, CancellationToken ct)
        {
            await _sendSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_owner.TryGetPublished(fileId, out var item) || !File.Exists(item.Path))
                {
                    await SendErrorAsync(fileId, "Unavailable", ct).ConfigureAwait(false);
                    return;
                }

                var current = new FileInfo(item.Path);
                if (current.Length < 0 || current.Length > MaxFileBytes ||
                    current.Length != item.Size || current.LastWriteTimeUtc != item.LastWriteUtc)
                {
                    await SendErrorAsync(fileId, "File changed after it was advertised", ct).ConfigureAwait(false);
                    return;
                }

                byte[] start = JsonSerializer.SerializeToUtf8Bytes(new { id = item.Id, name = item.Name, size = current.Length });
                await SendFrameAsync(FrameType.FileStart, start, ct).ConfigureAwait(false);

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var file = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[ChunkBytes];
                while (true)
                {
                    int read = await file.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    hash.AppendData(buffer.AsSpan(0, read));

                    byte[] payload = new byte[16 + read];
                    item.Id.TryWriteBytes(payload.AsSpan(0, 16));
                    Buffer.BlockCopy(buffer, 0, payload, 16, read);
                    await SendFrameAsync(FrameType.FileChunk, payload, ct).ConfigureAwait(false);
                }

                byte[] end = new byte[48];
                item.Id.TryWriteBytes(end.AsSpan(0, 16));
                hash.GetHashAndReset().CopyTo(end.AsSpan(16));
                await SendFrameAsync(FrameType.FileEnd, end, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendSlots.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _writeLock.Dispose();
            _sendSlots.Dispose();
        }
    }

    private readonly string _rootPath;
    private readonly string _outboxPath;
    private readonly string _cachePath;
    private readonly object _publishedLock = new();
    private readonly ConcurrentDictionary<Guid, PublishedFile> _published = new();
    private sealed record FileObservation(long Size, DateTime LastWriteUtc, int StableScans);

    private readonly ConcurrentDictionary<string, Guid> _publishedByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileObservation> _observed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), RemoteFile> _remote = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), CachedFile> _cached = new();
    private readonly ConcurrentDictionary<(Guid PeerId, Guid FileId), DownloadState> _downloads = new();
    private readonly SemaphoreSlim _downloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _outboxWatcher;
    private int _disposed;

    public Guid ProtocolId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000005");
    public ushort PreferredPort => 0;
    public string ProtocolName => "RelayDrive";
    public ushort StreamPriority => (ushort)QuicPunch.Helpers.QuicStreamPriority.Low;
    public ZstandardCompressionOptions? CompressionOptions => null;

    public static ConcurrentDictionary<Guid, RelayDriveSession> ActiveSessions { get; } = new();
    public string OutboxPath => _outboxPath;
    public string CachePath => _cachePath;
    public IReadOnlyCollection<PublishedFile> PublishedFiles => _published.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyCollection<RemoteFile> RemoteFiles => _remote.Values.OrderBy(x => x.PeerName).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyCollection<CachedFile> CachedFiles => _cached.Values.OrderByDescending(x => x.MaterializedUtc).ToArray();
    public IReadOnlyCollection<TransferProgress> ActiveTransfers => _downloads.Values
        .Select(x => new TransferProgress(x.PeerId, x.FileId, x.Name, Interlocked.Read(ref x.Received), x.ExpectedSize))
        .ToArray();

    public RelayDriveHandler(Guid localNodeId)
    {
        CleanupStaleRoots();
        string node = localNodeId == Guid.Empty ? "local" : localNodeId.ToString("N")[..12];
        _rootPath = Path.Combine(Path.GetTempPath(), "QuicPunch", "RelayDrive", node + "-" + Environment.ProcessId);
        _outboxPath = Path.Combine(_rootPath, "Outbox");
        _cachePath = Path.Combine(_rootPath, "Cache");
        Directory.CreateDirectory(_outboxPath);
        Directory.CreateDirectory(_cachePath);
        _outboxWatcher = Task.Run(() => WatchOutboxAsync(_lifetimeCts.Token));
    }

    public Task DeniedAsync(PeerInfo peer, CancellationToken ct)
    {
        Console.WriteLine($"[RELAYDRIVE] Session with {peer.Name} was denied or failed.");
        return Task.CompletedTask;
    }

    public async Task HandleAsync(QuicConnection connection, Stream stream, PeerInfo peer, CancellationToken ct)
    {
        QuicPunch.Helpers.MsQuicTuner.TrySetStreamPriority(stream, QuicPunch.Helpers.QuicStreamPriority.Low);
        var session = new RelayDriveSession(this, peer, stream, connection);
        if (ActiveSessions.TryRemove(peer.Id, out var previous))
        {
            try { previous.Stream.Close(); } catch { }
            try { await previous.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
            previous.Dispose();
        }
        ActiveSessions[peer.Id] = session;

        Console.WriteLine($"[RELAYDRIVE] Virtual shelf connected to {peer.Name}.");
        try
        {
            RefreshPublishedFiles();
            await SendManifestAsync(session, ct).ConfigureAwait(false);

            byte[] header = new byte[5];
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                FrameType type = (FrameType)header[0];
                int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                int max = type == FrameType.Manifest ? MaxManifestBytes : ChunkBytes + 64 * 1024;
                if (length < 0 || length > max)
                    throw new InvalidDataException("Invalid RelayDrive frame length.");

                byte[] payload = new byte[length];
                if (length > 0)
                    await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);

                switch (type)
                {
                    case FrameType.Manifest:
                        ProcessManifest(peer, payload);
                        break;
                    case FrameType.Request:
                        if (payload.Length == 16)
                        {
                            Guid requested = new(payload);
                            if (session.TryQueueTransfer(requested))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await session.SendPublishedFileAsync(requested, ct).ConfigureAwait(false); }
                                    catch (Exception ex) { Console.WriteLine($"[RELAYDRIVE] Send failed: {ex.Message}"); }
                                    finally { session.CompleteQueuedTransfer(requested); }
                                }, CancellationToken.None);
                            }
                        }
                        break;
                    case FrameType.FileStart:
                        await BeginIncomingFileAsync(peer, payload, ct).ConfigureAwait(false);
                        break;
                    case FrameType.FileChunk:
                        await AppendIncomingChunkAsync(peer, payload, ct).ConfigureAwait(false);
                        break;
                    case FrameType.FileEnd:
                        await CompleteIncomingFileAsync(peer, payload, ct).ConfigureAwait(false);
                        break;
                    case FrameType.Error:
                        ProcessErrorFrame(peer, payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAYDRIVE] Session error with {peer.Name}: {ex.Message}");
        }
        finally
        {
            bool wasCurrent = ActiveSessions.TryRemove(new KeyValuePair<Guid, RelayDriveSession>(peer.Id, session));
            session.Dispose();
            if (wasCurrent)
            {
                foreach (var key in _remote.Keys.Where(k => k.PeerId == peer.Id).ToArray())
                    _remote.TryRemove(key, out _);
                FailDownloadsForPeer(peer.Id, "RelayDrive session ended.");
                Console.WriteLine($"[RELAYDRIVE] Virtual shelf with {peer.Name} ended.");
            }
        }
    }

    public bool RefreshPublishedFiles()
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        Directory.CreateDirectory(_outboxPath);
        bool changed = false;
        lock (_publishedLock)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(_outboxPath).Take(MaxPublishedFiles * 2))
            {
                FileInfo info;
                try { info = new FileInfo(path); }
                catch { continue; }
                if (!info.Exists || info.Name.Contains(".qp-part.", StringComparison.OrdinalIgnoreCase) || info.Length < 0 || info.Length > MaxFileBytes)
                    continue;
                seen.Add(info.FullName);

                if (_publishedByPath.TryGetValue(info.FullName, out Guid existingId)
                    && _published.TryGetValue(existingId, out var existing)
                    && existing.Size == info.Length
                    && existing.LastWriteUtc == info.LastWriteTimeUtc)
                {
                    _observed[info.FullName] = new FileObservation(info.Length, info.LastWriteTimeUtc, 2);
                    continue;
                }

                if (existingId != Guid.Empty)
                {
                    _publishedByPath.TryRemove(info.FullName, out _);
                    _published.TryRemove(existingId, out _);
                    changed = true;
                }

                if (!_observed.TryGetValue(info.FullName, out var observation)
                    || observation.Size != info.Length
                    || observation.LastWriteUtc != info.LastWriteTimeUtc)
                {
                    _observed[info.FullName] = new FileObservation(info.Length, info.LastWriteTimeUtc, 1);
                    continue;
                }

                int stableScans = Math.Min(2, observation.StableScans + 1);
                _observed[info.FullName] = observation with { StableScans = stableScans };
                if (stableScans < 2 || _published.Count >= MaxPublishedFiles) continue;

                Guid id = Guid.NewGuid();
                var item = new PublishedFile(id, info.Name, info.FullName, info.Length, info.LastWriteTimeUtc);
                _published[id] = item;
                _publishedByPath[info.FullName] = id;
                changed = true;
            }

            foreach (var pair in _publishedByPath.ToArray())
            {
                if (seen.Contains(pair.Key)) continue;
                _publishedByPath.TryRemove(pair.Key, out _);
                _published.TryRemove(pair.Value, out _);
                changed = true;
            }
            foreach (string observedPath in _observed.Keys.ToArray())
            {
                if (!seen.Contains(observedPath)) _observed.TryRemove(observedPath, out _);
            }
        }
        return changed;
    }

    public async Task<PublishedFile> PublishAsync(string fileName, Stream source, long? declaredLength, CancellationToken ct)
    {
        if (declaredLength is > MaxFileBytes)
            throw new InvalidDataException("File is larger than the RelayDrive limit.");

        string safeName = SanitizeFileName(fileName);
        string destination = UniquePath(_outboxPath, safeName);
        string partial = destination + ".qp-part." + Guid.NewGuid().ToString("N");
        long total = 0;
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[ChunkBytes];
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxFileBytes)
                        throw new InvalidDataException("File is larger than the RelayDrive limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(partial, destination, false);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            try { File.Delete(destination); } catch { }
            throw;
        }

        PublishedFile published;
        string full = Path.GetFullPath(destination);
        lock (_publishedLock)
        {
            if (_published.Count >= MaxPublishedFiles)
            {
                try { File.Delete(destination); } catch { }
                throw new InvalidOperationException($"RelayDrive already has {MaxPublishedFiles} published files.");
            }
            var info = new FileInfo(destination);
            Guid id = Guid.NewGuid();
            published = new PublishedFile(id, info.Name, info.FullName, info.Length, info.LastWriteTimeUtc);
            _published[id] = published;
            _publishedByPath[info.FullName] = id;
            _observed[info.FullName] = new FileObservation(info.Length, info.LastWriteTimeUtc, 2);
        }

        await BroadcastManifestAsync(ct).ConfigureAwait(false);
        return published;
    }

    public async Task<bool> RemovePublishedAsync(Guid fileId, CancellationToken ct)
    {
        if (!_published.TryGetValue(fileId, out var item)) return false;
        try { File.Delete(item.Path); } catch { return false; }
        RefreshPublishedFiles();
        await BroadcastManifestAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<string> MaterializeAsync(Guid peerId, Guid fileId, CancellationToken ct)
    {
        if (_cached.TryGetValue((peerId, fileId), out var cached) && File.Exists(cached.Path))
            return cached.Path;
        if (!_remote.TryGetValue((peerId, fileId), out var remote))
            throw new FileNotFoundException("The remote file is no longer advertised.");
        if (!ActiveSessions.TryGetValue(peerId, out var session))
            throw new InvalidOperationException("RelayDrive is not connected to this peer.");
        if (remote.Size < 0 || remote.Size > MaxFileBytes)
            throw new InvalidDataException("Remote file size is invalid.");

        var key = (peerId, fileId);
        while (true)
        {
            if (_downloads.TryGetValue(key, out var existing))
                return await existing.Completion.Task.WaitAsync(ct).ConfigureAwait(false);

            if (!await _downloadSlots.WaitAsync(0, ct).ConfigureAwait(false))
                throw new InvalidOperationException($"RelayDrive already has {MaxConcurrentDownloads} active materializations.");

            string temp = Path.Combine(_cachePath, $"{peerId:N}_{fileId:N}.part");
            var state = new DownloadState(peerId, fileId, remote.Name, remote.Size, temp);
            if (!_downloads.TryAdd(key, state))
            {
                state.Dispose();
                _downloadSlots.Release();
                continue;
            }

            try
            {
                byte[] request = fileId.ToByteArray();
                await session.SendFrameAsync(FrameType.Request, request, ct).ConfigureAwait(false);

                using (var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    startTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await state.Started.Task.WaitAsync(startTimeout.Token).ConfigureAwait(false);
                }

                using var transferTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                transferTimeout.CancelAfter(TimeSpan.FromMinutes(20));
                return await state.Completion.Task.WaitAsync(transferTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                if (_downloads.TryRemove(key, out var removed))
                {
                    removed.Dispose();
                    try { File.Delete(removed.TempPath); } catch { }
                }
                throw;
            }
            finally
            {
                _downloadSlots.Release();
            }
        }
    }

    public bool TryGetCached(Guid peerId, Guid fileId, out CachedFile file)
    {
        if (_cached.TryGetValue((peerId, fileId), out var found) && File.Exists(found.Path))
        {
            file = found;
            return true;
        }
        file = null!;
        return false;
    }

    public void OpenOutboxFolder() => OpenFolder(_outboxPath);

    public void OpenCacheFolder() => OpenFolder(_cachePath);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private bool TryGetPublished(Guid id, out PublishedFile item)
    {
        if (_published.TryGetValue(id, out var found))
        {
            item = found;
            return true;
        }
        item = null!;
        return false;
    }

    private async Task WatchOutboxAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (RefreshPublishedFiles())
                    await BroadcastManifestAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[RELAYDRIVE] Outbox watcher stopped: {ex.Message}");
        }
    }

    private async Task BroadcastManifestAsync(CancellationToken ct)
    {
        foreach (RelayDriveSession session in ActiveSessions.Values.ToArray())
        {
            try { await SendManifestAsync(session, ct).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task SendManifestAsync(RelayDriveSession session, CancellationToken ct)
    {
        RefreshPublishedFiles();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(_published.Values.Take(MaxPublishedFiles).Select(x => new { id = x.Id, name = x.Name, size = x.Size }));
        if (payload.Length > MaxManifestBytes)
            throw new InvalidDataException("RelayDrive manifest is too large.");
        await session.SendFrameAsync(FrameType.Manifest, payload, ct).ConfigureAwait(false);
    }

    private void ProcessManifest(PeerInfo peer, byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return;
            var seen = new HashSet<Guid>();
            int count = 0;
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (++count > MaxPublishedFiles) break;
                if ((!element.TryGetProperty("id", out var idEl) && !element.TryGetProperty("Id", out idEl)) || !Guid.TryParse(idEl.GetString(), out Guid id) || id == Guid.Empty) continue;
                string? rawName = element.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : (element.TryGetProperty("Name", out var nameEl2) ? nameEl2.GetString() : null);
                string name = SanitizeFileName(rawName ?? "file");
                long size = (element.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out long parsed)) ? parsed : ((element.TryGetProperty("Size", out var sizeEl2) && sizeEl2.TryGetInt64(out long parsed2)) ? parsed2 : -1);
                if (size < 0 || size > MaxFileBytes) continue;
                seen.Add(id);
                _remote[(peer.Id, id)] = new RemoteFile(peer.Id, peer.Name ?? "Peer", id, name, size);
            }
            foreach (var key in _remote.Keys.Where(k => k.PeerId == peer.Id && !seen.Contains(k.FileId)).ToArray())
                _remote.TryRemove(key, out _);
        }
        catch (JsonException) { }
    }

    private async Task BeginIncomingFileAsync(PeerInfo peer, byte[] payload, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if ((!root.TryGetProperty("id", out var idEl) && !root.TryGetProperty("Id", out idEl)) || !Guid.TryParse(idEl.GetString(), out Guid id)) return;
            var key = (peer.Id, id);
            if (!_downloads.TryGetValue(key, out var state)) return;
            string? rawName = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : (root.TryGetProperty("Name", out var nameEl2) ? nameEl2.GetString() : null);
            string name = SanitizeFileName(rawName ?? state.Name);
            long size = (root.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out long parsed)) ? parsed : ((root.TryGetProperty("Size", out var sizeEl2) && sizeEl2.TryGetInt64(out long parsed2)) ? parsed2 : -1);
            if (size != state.ExpectedSize || size < 0 || size > MaxFileBytes) throw new InvalidDataException("Remote file metadata changed during transfer.");

            state.Dispose();
            try { File.Delete(state.TempPath); } catch { }
            state.Stream = new FileStream(state.TempPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            state.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            state.Received = 0;
            state.Started.TrySetResult(true);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            FailDownload(peer.Id, TryReadFileId(payload), ex);
        }
    }

    private async Task AppendIncomingChunkAsync(PeerInfo peer, byte[] payload, CancellationToken ct)
    {
        if (payload.Length < 16) return;
        Guid id = new(payload.AsSpan(0, 16));
        if (!_downloads.TryGetValue((peer.Id, id), out var state) || state.Stream == null || state.Hash == null) return;
        int count = payload.Length - 16;
        Interlocked.Add(ref state.Received, count);
        if (Interlocked.Read(ref state.Received) > state.ExpectedSize)
        {
            FailDownload(peer.Id, id, new InvalidDataException("Remote file exceeded the advertised size."));
            return;
        }
        state.Hash.AppendData(payload.AsSpan(16, count));
        await state.Stream.WriteAsync(payload.AsMemory(16, count), ct).ConfigureAwait(false);
    }

    private async Task CompleteIncomingFileAsync(PeerInfo peer, byte[] payload, CancellationToken ct)
    {
        if (payload.Length != 48) return;
        Guid id = new(payload.AsSpan(0, 16));
        var key = (peer.Id, id);
        if (!_downloads.TryRemove(key, out var state)) return;

        try
        {
            if (state.Stream == null || state.Hash == null || Interlocked.Read(ref state.Received) != state.ExpectedSize)
                throw new InvalidDataException("Remote file transfer was incomplete.");
            await state.Stream.FlushAsync(ct).ConfigureAwait(false);
            state.Stream.Dispose();
            state.Stream = null;
            byte[] actualHash = state.Hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualHash, payload.AsSpan(16, 32)))
                throw new InvalidDataException("Remote file hash did not match.");

            string final = UniquePath(_cachePath, state.Name);
            File.Move(state.TempPath, final, true);
            var cached = new CachedFile(peer.Id, peer.Name ?? "Peer", id, state.Name, final, state.ExpectedSize, DateTime.UtcNow);
            _cached[key] = cached;
            state.Completion.TrySetResult(final);
        }
        catch (Exception ex)
        {
            try { File.Delete(state.TempPath); } catch { }
            state.Completion.TrySetException(ex);
        }
        finally
        {
            state.Dispose();
        }
    }

    private void ProcessErrorFrame(PeerInfo peer, byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            if ((!root.TryGetProperty("id", out var idEl) && !root.TryGetProperty("Id", out idEl)) || !Guid.TryParse(idEl.GetString(), out Guid id) || id == Guid.Empty) return;
            string? rawError = root.TryGetProperty("error", out var errorEl) ? errorEl.GetString() : (root.TryGetProperty("Error", out var errorEl2) ? errorEl2.GetString() : null);
            string error = rawError ?? "Unavailable";
            FailDownload(peer.Id, id, new IOException(error));
        }
        catch (JsonException) { }
    }

    private void FailDownloadsForPeer(Guid peerId, string reason)
    {
        foreach (var key in _downloads.Keys.Where(k => k.PeerId == peerId).ToArray())
            FailDownload(key.PeerId, key.FileId, new IOException(reason));
    }

    private void FailDownload(Guid peerId, Guid fileId, Exception ex)
    {
        if (fileId == Guid.Empty) return;
        if (!_downloads.TryRemove((peerId, fileId), out var state)) return;
        state.Dispose();
        try { File.Delete(state.TempPath); } catch { }
        state.Started.TrySetException(ex);
        state.Completion.TrySetException(ex);
    }

    private static Guid TryReadFileId(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if ((document.RootElement.TryGetProperty("id", out var idEl) || document.RootElement.TryGetProperty("Id", out idEl)) && Guid.TryParse(idEl.GetString(), out Guid id))
                return id;
            return Guid.Empty;
        }
        catch { return Guid.Empty; }
    }

    private static void CleanupStaleRoots()
    {
        try
        {
            string basePath = Path.Combine(Path.GetTempPath(), "QuicPunch", "RelayDrive");
            if (!Directory.Exists(basePath)) return;
            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
            foreach (string directory in Directory.EnumerateDirectories(basePath))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if (info.LastWriteTimeUtc < cutoff) Directory.Delete(directory, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string SanitizeFileName(string value)
    {
        string name = Path.GetFileName(value.Trim());
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        if (name.Length > 180) name = name[..180];
        return name;
    }

    private static string UniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = Path.Combine(directory, fileName);
        for (int i = 1; File.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{baseName} ({i}){extension}");
        return candidate;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetimeCts.Cancel(); } catch { }
        try { _outboxWatcher.Wait(TimeSpan.FromSeconds(2)); } catch { }
        foreach (var session in ActiveSessions.Values.ToArray()) session.Dispose();
        foreach (var download in _downloads.Values.ToArray()) download.Dispose();
        ActiveSessions.Clear();
        _downloads.Clear();
        _downloadSlots.Dispose();
        _lifetimeCts.Dispose();
        try { Directory.Delete(_rootPath, true); } catch { }
    }
}
