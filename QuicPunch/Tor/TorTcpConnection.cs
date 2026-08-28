#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch;

public sealed class TorTcpConnection : Stream, IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private int _disposed;

    internal TorTcpConnection(
        TcpClient client,
        string? remoteOnion,
        int remoteVirtualPort,
        bool inbound)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = client.GetStream();
        RemoteOnion = remoteOnion;
        RemoteVirtualPort = remoteVirtualPort;
        IsInbound = inbound;
    }

    public string? RemoteOnion { get; }
    public int RemoteVirtualPort { get; }
    public bool IsInbound { get; }

    public EndPoint? LocalSocketEndPoint => _client.Client.LocalEndPoint;
    public EndPoint? TorProxySocketEndPoint => _client.Client.RemoteEndPoint;

    public NetworkStream NetworkStream => _stream;
    internal Socket Socket => _client.Client;

    public void ShutdownSend()
    {
        ThrowIfDisposed();
        try { _client.Client.Shutdown(SocketShutdown.Send); }
        catch (SocketException) { }
    }

    public void ShutdownReceive()
    {
        ThrowIfDisposed();
        try { _client.Client.Shutdown(SocketShutdown.Receive); }
        catch (SocketException) { }
    }

    public void Abort(QuicAbortDirection direction, long errorCode = 0)
    {
        ThrowIfDisposed();
        SocketShutdown shutdown = direction switch
        {
            QuicAbortDirection.Read => SocketShutdown.Receive,
            QuicAbortDirection.Write => SocketShutdown.Send,
            QuicAbortDirection.Both => SocketShutdown.Both,
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };

        try { _client.Client.Shutdown(shutdown); } catch { }
        if (direction == QuicAbortDirection.Both)
            Dispose();
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0 && _stream.CanWrite;
    public override bool CanTimeout => _stream.CanTimeout;

    public override int ReadTimeout
    {
        get => _stream.ReadTimeout;
        set => _stream.ReadTimeout = value;
    }

    public override int WriteTimeout
    {
        get => _stream.WriteTimeout;
        set => _stream.WriteTimeout = value;
    }

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _stream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _stream.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _stream.Read(buffer);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return 0;
        try
        {
            return await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or SocketException or IOException)
        {
            return 0;
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return 0;
        try
        {
            return await _stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or SocketException or IOException)
        {
            return 0;
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        _stream.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _stream.Write(buffer);

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    public override async Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            await _stream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NullReferenceException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _stream.Dispose(); } finally { _client.Dispose(); }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        finally { _client.Dispose(); }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
