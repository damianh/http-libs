namespace DamianH.HttpHybridCacheHandler;

// Presents a fixed-length, zero-based view without transferring ownership to the SDK.
internal sealed class UploadSourceStream(Stream source, long length) : CompatibleStream
{
    private readonly long _start = source.Position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => source.Position - _start;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - Position);
        var read = source.Read(buffer[..count]);
        if (read == 0 && count != 0)
        {
            throw new EndOfStreamException("The upload source ended before contentLength.");
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = (int)Math.Min(buffer.Length, length - Position);
        var read = await source.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        if (read == 0 && count != 0)
        {
            throw new EndOfStreamException("The upload source ended before contentLength.");
        }
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(Position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > length)
        {
            throw new IOException("Cannot seek outside the upload content.");
        }
        source.Position = checked(_start + position);
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
