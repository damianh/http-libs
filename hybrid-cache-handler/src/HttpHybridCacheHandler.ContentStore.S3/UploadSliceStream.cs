namespace DamianH.HttpHybridCacheHandler;

// Seekable views let the SDK retry a part without buffering it or escaping its boundaries.
internal sealed class UploadSliceStream(Stream source, long start, long length, int bufferSize, CancellationToken ct) : CompatibleStream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        var count = (int)Math.Min(Math.Min(buffer.Length, bufferSize), length - _position);
        if (count == 0) return 0;
        source.Position = start + _position;
        var read = source.Read(buffer[..count]);
        if (read == 0) throw new EndOfStreamException("The upload input ended before its declared length.");
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ct.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        var count = (int)Math.Min(Math.Min(buffer.Length, bufferSize), length - _position);
        if (count == 0) return 0;
        source.Position = start + _position;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
        var read = await source.ReadAsync(buffer[..count], linked.Token).ConfigureAwait(false);
        if (read == 0) throw new EndOfStreamException("The upload input ended before its declared length.");
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > length) throw new IOException("Cannot seek outside the upload part.");
        return _position = position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
