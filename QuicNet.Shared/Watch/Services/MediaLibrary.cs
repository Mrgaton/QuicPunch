using QuicPunch;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Protocols;

namespace QuicNet.Watch.Services;

public sealed class MediaLibrary : IDisposable
{
    public const int LogicalChunkBytes = MediaTransferHandler.MaxRangeBytes;
    public const long MaximumMediaBytes = 64L * 1024 * 1024 * 1024;
    internal const int MaximumCaches = 8;
    internal const int MaximumPeerCaches = 4;
    internal const int CacheBytes = 4 * 1024 * 1024; // 32 MiB globally / 16 MiB per origin.
    private const int MaximumChunks = 1024;

    private sealed class RemoteCache(MediaManifest manifest, MediaTransferHandler transfer)
    {
        private readonly Dictionary<long, (byte[] Data, long Used)> _chunks = new();
        private readonly SemaphoreSlim _fetchLock = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private long _clock;
        public MediaManifest Manifest { get; } = manifest;
        public long CachedBytes { get; private set; }
        public int Readers;
        public bool Retired;
        public long Used;

        public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            ct = lifetime.Token;
            await _fetchLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                byte[] result = new byte[length];
                int copied = 0;
                while (copied < length)
                {
                    long position = offset + copied;
                    long chunk = position / Manifest.ChunkSize;
                    if (!_chunks.TryGetValue(chunk, out var entry))
                    {
                        long chunkOffset = chunk * Manifest.ChunkSize;
                        int chunkLength = (int)Math.Min(Manifest.ChunkSize, Manifest.Size - chunkOffset);
                        byte[] data = await transfer.RequestRangeAsync(Manifest.SourcePeerId, Manifest.MediaId, chunkOffset, chunkLength, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        while (CachedBytes + data.Length > CacheBytes || _chunks.Count >= MaximumChunks)
                        {
                            long oldest = _chunks.MinBy(x => x.Value.Used).Key;
                            CachedBytes -= _chunks[oldest].Data.Length;
                            _chunks.Remove(oldest);
                        }
                        entry = (data, ++_clock);
                        _chunks.Add(chunk, entry);
                        CachedBytes += data.Length;
                    }
                    _chunks[chunk] = (entry.Data, ++_clock);
                    int within = (int)(position % Manifest.ChunkSize);
                    int count = Math.Min(length - copied, entry.Data.Length - within);
                    entry.Data.AsSpan(within, count).CopyTo(result.AsSpan(copied));
                    copied += count;
                }
                return result;
            }
            finally { _fetchLock.Release(); }
        }

        public void Retire() { Retired = true; _stop.Cancel(); }
        public void Dispose()
        {
            // The parent counts active readers and waiters before allowing disposal.
            _chunks.Clear(); CachedBytes = 0;
            _fetchLock.Dispose(); _stop.Dispose();
        }
    }

    private readonly MediaTransferHandler _transfer;
    private readonly Dictionary<Guid, RemoteCache> _remoteCaches = new();
    private readonly object _gate = new();
    private long _clock;
    private bool _disposed;
    private MediaManifest? _localManifest;
    private string? _localPath;
    public event Action<Guid, long, long>? CacheProgress;
    internal Func<Guid, Guid, bool>? RemoteReadAllowed { get; set; }
    internal int RemoteCacheCount { get { lock (_gate) return _remoteCaches.Count; } }
    internal long RetainedBytes { get { lock (_gate) return _remoteCaches.Values.Sum(c => c.CachedBytes); } }

    public MediaLibrary(MediaTransferHandler transfer, string cacheRoot)
    {
        _transfer = transfer;
        // Old GUID.cache files are disposable. Never delete a file in use by another instance.
        if (Directory.Exists(cacheRoot))
            foreach (string path in Directory.EnumerateFiles(cacheRoot, "*.cache", SearchOption.TopDirectoryOnly))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                    try { using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
        _transfer.RangeRequested = ServeRemoteRangeAsync;
    }

    public MediaManifest SelectLocalMedia(string path, Guid localPeerId)
    {
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("El archivo de vídeo no existe.", fullPath);
        var manifest = new MediaManifest(Guid.NewGuid(), info.Name, info.Length, MimeTypes.ForPath(fullPath), LogicalChunkBytes, localPeerId);
        ValidateManifest(manifest);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _localPath = fullPath; _localManifest = manifest;
        }
        KeepRemoteMedia(null);
        return manifest;
    }

    private static void ValidateManifest(MediaManifest manifest)
    {
        if (manifest.MediaId == Guid.Empty || manifest.SourcePeerId == Guid.Empty || manifest.Size is <= 0 or > MaximumMediaBytes ||
            manifest.ChunkSize is <= 0 or > MediaTransferHandler.MaxRangeBytes)
            throw new InvalidDataException("Medio no válido o superior al límite de 64 GiB.");
    }

    public void RegisterRemoteMedia(MediaManifest manifest)
    {
        ValidateManifest(manifest);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_remoteCaches.TryGetValue(manifest.MediaId, out var existing))
            {
                if (existing.Retired || existing.Manifest != manifest) throw new InvalidDataException("Media identity already belongs to another manifest.");
                existing.Used = ++_clock; return;
            }
            bool FromPeer(RemoteCache cache) => cache.Manifest.SourcePeerId == manifest.SourcePeerId;
            while (_remoteCaches.Count >= MaximumCaches || _remoteCaches.Values.Count(FromPeer) >= MaximumPeerCaches)
            {
                bool peerFull = _remoteCaches.Values.Count(FromPeer) >= MaximumPeerCaches;
                var oldest = _remoteCaches.Values.Where(c => c.Readers == 0 && (!peerFull || FromPeer(c))).MinBy(c => c.Used);
                if (oldest == null) throw new IOException("La caché de vídeo está ocupada; espera a que terminen las lecturas.");
                RemoveCache(oldest);
            }
            _remoteCaches.Add(manifest.MediaId, new(manifest, _transfer) { Used = ++_clock });
        }
    }

    private void RemoveCache(RemoteCache cache)
    {
        if (!cache.Retired) cache.Retire();
        _remoteCaches.Remove(cache.Manifest.MediaId);
        cache.Dispose();
    }

    internal void KeepRemoteMedia(Guid? mediaId)
    {
        lock (_gate)
            foreach (var cache in _remoteCaches.Values.Where(c => c.Manifest.MediaId != mediaId).ToArray())
            {
                if (cache.Readers == 0) RemoveCache(cache);
                else if (!cache.Retired) cache.Retire();
            }
    }

    public MediaManifest? GetManifest(Guid mediaId)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_localManifest?.MediaId == mediaId) return _localManifest;
            return _remoteCaches.TryGetValue(mediaId, out var cache) && !cache.Retired ? cache.Manifest : null;
        }
    }

    public async Task<byte[]> ReadRangeAsync(Guid mediaId, long offset, int length, CancellationToken ct)
    {
        MediaManifest manifest;
        string? localPath = null;
        RemoteCache? cache = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_localManifest?.MediaId == mediaId) { manifest = _localManifest; localPath = _localPath; }
            else
            {
                if (!_remoteCaches.TryGetValue(mediaId, out cache) || cache.Retired) throw new FileNotFoundException("Media is not available.");
                manifest = cache.Manifest;
            }
            if (offset < 0 || length is <= 0 or > MediaTransferHandler.MaxRangeBytes || offset > manifest.Size - length)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (cache != null) { cache.Readers++; cache.Used = ++_clock; }
        }
        if (cache == null)
        {
            await using var source = new FileStream(localPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            source.Seek(offset, SeekOrigin.Begin);
            byte[] result = new byte[length];
            await source.ReadExactlyAsync(result, ct).ConfigureAwait(false);
            return result;
        }
        try
        {
            byte[] result = await cache.ReadAsync(offset, length, ct).ConfigureAwait(false);
            CacheProgress?.Invoke(mediaId, cache.CachedBytes, manifest.Size);
            return result;
        }
        finally { lock (_gate) { cache.Readers--; if (cache.Retired && cache.Readers == 0) RemoveCache(cache); } }
    }

    private async Task ServeRemoteRangeAsync(PeerInfo peer, MediaRangeRequest request, CancellationToken ct)
    {
        if (RemoteReadAllowed?.Invoke(peer.Id, request.MediaId) != true) throw new UnauthorizedAccessException("Media access denied.");
        string path;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_localManifest?.MediaId != request.MediaId || _localPath == null || request.Offset < 0 ||
                request.Length is <= 0 or > MediaTransferHandler.MaxRangeBytes || request.Offset > _localManifest.Size - request.Length)
                throw new InvalidDataException("Invalid media range.");
            path = _localPath;
        }
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await _transfer.SendRangeAsync(peer.Id, request, source, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        KeepRemoteMedia(null);
    }
}
