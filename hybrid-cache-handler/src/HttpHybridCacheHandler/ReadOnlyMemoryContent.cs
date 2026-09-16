// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>
/// HttpContent implementation that wraps ReadOnlyMemory to avoid byte array copying.
/// </summary>
internal sealed class ReadOnlyMemoryContent(ReadOnlyMemory<byte> content) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => stream.WriteAsync(content).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = content.Length;
        return true;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
        => Task.FromResult<Stream>(new ReadOnlyMemoryStream(content));

    /// <summary>
    /// Stream implementation that reads from ReadOnlyMemory without allocating byte arrays.
    /// </summary>
    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> memory) : CompatibleStream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => memory.Length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > memory.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = memory.Length - _position;
            var toRead = Math.Min(count, remaining);
            if (toRead > 0)
            {
                memory.Span.Slice(_position, toRead).CopyTo(buffer.AsSpan(offset, toRead));
                _position += toRead;
            }

            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, Ct ct = default)
        {
            var remaining = memory.Length - _position;
            var toRead = Math.Min(buffer.Length, remaining);
            if (toRead > 0)
            {
                memory.Slice(_position, toRead).CopyTo(buffer);
                _position += toRead;
            }

            return new ValueTask<int>(toRead);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var newPosition = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => memory.Length + offset,
                _ => throw new ArgumentException("Invalid seek origin", nameof(origin))
            };

            if (newPosition < 0 || newPosition > memory.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            _position = (int)newPosition;
            return _position;
        }

        public override void Flush() { }
        public override Task FlushAsync(Ct ct) => Task.CompletedTask;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
