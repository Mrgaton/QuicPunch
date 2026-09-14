#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QuicPunch.Multiplexer
{
    /// <summary>
    /// Wraps a physical QUIC stream belonging to a virtual multiplexed session.
    /// Handles stream preface prefixing and delegates all stream primitives.
    /// </summary>
    public sealed class MultiplexedStreamTransport : IQuicStreamTransport
    {
        private readonly IQuicStreamTransport _inner;
        private readonly ushort _sessionId;
        private readonly bool _openedLocally;
        private int _headerWritten;
        private int _disposed;

        public const int StreamHeaderSize = 6;

        public MultiplexedStreamTransport(
            IQuicStreamTransport inner,
            ushort sessionId,
            bool openedLocally)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _sessionId = sessionId;
            _openedLocally = openedLocally;
            // If accepted remotely, the 6-byte header has already been consumed by the dispatcher.
            _headerWritten = openedLocally ? 0 : 1;
        }

        public long Id => _inner.Id;
        public QuicStreamType Type => _inner.Type;
        public bool CanRead => _inner.CanRead;
        public bool CanWrite => _inner.CanWrite;
        public Stream Stream => new MultiplexedStreamWrapper(this, _inner.Stream);
        public Task ReadsClosed => _inner.ReadsClosed;
        public Task WritesClosed => _inner.WritesClosed;

        public byte Priority
        {
            get => _inner.Priority;
            set => _inner.Priority = value;
        }

        public ushort Priority16
        {
            get => _inner.Priority16;
            set => _inner.Priority16 = value;
        }

        public void CompleteWrites() => _inner.CompleteWrites();

        public void Abort(QuicAbortDirection abortDirection, long errorCode) =>
            _inner.Abort(abortDirection, errorCode);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _inner.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        internal async ValueTask EnsureHeaderWrittenAsync(CancellationToken ct)
        {
            if (_openedLocally && Interlocked.CompareExchange(ref _headerWritten, 1, 0) == 0)
            {
                byte[] header = new byte[StreamHeaderSize];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), _sessionId);
                header[2] = (byte)_inner.Type;
                header[3] = 0; // Flags
                header[4] = 0; // Reserved
                header[5] = 0;

                await _inner.Stream.WriteAsync(header, ct).ConfigureAwait(false);
                await _inner.Stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }

        private sealed class MultiplexedStreamWrapper : Stream
        {
            private readonly MultiplexedStreamTransport _owner;
            private readonly Stream _underlying;

            public MultiplexedStreamWrapper(MultiplexedStreamTransport owner, Stream underlying)
            {
                _owner = owner;
                _underlying = underlying;
            }

            public override bool CanRead => _underlying.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => _underlying.CanWrite;
            public override bool CanTimeout => _underlying.CanTimeout;
            public override int ReadTimeout { get => _underlying.ReadTimeout; set => _underlying.ReadTimeout = value; }
            public override int WriteTimeout { get => _underlying.WriteTimeout; set => _underlying.WriteTimeout = value; }

            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _underlying.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _underlying.FlushAsync(cancellationToken);

            public override int Read(byte[] buffer, int offset, int count) =>
                _underlying.Read(buffer, offset, count);

            public override int Read(Span<byte> buffer) =>
                _underlying.Read(buffer);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                _underlying.ReadAsync(buffer, cancellationToken);

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                _underlying.ReadAsync(buffer, offset, count, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count)
            {
                _owner.EnsureHeaderWrittenAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                _underlying.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _owner.EnsureHeaderWrittenAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                _underlying.Write(buffer);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await _owner.EnsureHeaderWrittenAsync(cancellationToken).ConfigureAwait(false);
                await _underlying.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await _owner.EnsureHeaderWrittenAsync(cancellationToken).ConfigureAwait(false);
                await _underlying.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _owner.Dispose();
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await _owner.DisposeAsync().ConfigureAwait(false);
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
