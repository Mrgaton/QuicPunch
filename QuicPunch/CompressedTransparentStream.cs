using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;

public class CompressedTransparentStream : Stream
{
    private readonly Stream _innerStream;
    private readonly ZstandardStream _compressor;
    private readonly ZstandardStream _decompressor;

    /// <summary>
    /// Initializes a new instance of the CompressedTransparentStream.
    /// </summary>
    /// <param name="innerStream">The underlying bidirectional stream (e.g., QUIC or TCP stream).</param>
    /// <param name="compressionLevel">The level of compression to apply to outgoing data.</param>
    public CompressedTransparentStream(Stream innerStream, ZstandardCompressionOptions compressionOptions)
    {
        if (innerStream == null)
            throw new ArgumentNullException(nameof(innerStream));

        _innerStream = innerStream;

        _compressor = new ZstandardStream(innerStream, compressionOptions, leaveOpen: true);

        _decompressor = new ZstandardStream(innerStream, CompressionMode.Decompress, leaveOpen: true);
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanWrite => _innerStream.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _compressor.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _compressor.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _compressor.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        _compressor.Flush();
        _innerStream.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _compressor.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }


    public override int Read(byte[] buffer, int offset, int count)
    {
        return _decompressor.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _decompressor.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return _decompressor.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _compressor.Dispose();
            }
            catch { }

            try
            {
                _decompressor.Dispose();
            }
            catch { /* Ignore */ }
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _compressor.DisposeAsync().ConfigureAwait(false);
        }
        catch { }

        try
        {
            await _decompressor.DisposeAsync().ConfigureAwait(false);
        }
        catch { }

        await _innerStream.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
