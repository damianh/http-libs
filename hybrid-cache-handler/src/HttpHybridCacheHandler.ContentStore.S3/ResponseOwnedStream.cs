using Amazon.S3.Model;

namespace DamianH.HttpHybridCacheHandler;

internal sealed class ResponseOwnedStream(GetObjectResponse response) : CompatibleStream
{
    private bool _disposed;
    private Stream Inner
    {
        get
        {
            Guard.NotDisposed(_disposed, this);
            return response.ResponseStream;
        }
    }

    public override bool CanRead => !_disposed && response.ResponseStream.CanRead;
    public override bool CanSeek => !_disposed && response.ResponseStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => Inner.Length;
    public override long Position { get => Inner.Position; set => Inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => Inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void Flush() => Inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            response.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}
