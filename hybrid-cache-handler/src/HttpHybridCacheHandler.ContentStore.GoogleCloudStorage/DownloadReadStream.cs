using System.IO.Pipelines;

namespace DamianH.HttpHybridCacheHandler;

internal sealed class DownloadReadStream : CompatibleStream
{
    private readonly Pipe _pipe;
    private readonly Stream _reader;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _producer;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    public DownloadReadStream(Func<Stream, CancellationToken, Task> download, int bufferSize, CancellationToken ct)
    {
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: bufferSize,
            resumeWriterThreshold: bufferSize / 2,
            useSynchronizationContext: false));
        _reader = _pipe.Reader.AsStream(leaveOpen: true);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken.None);
        // The owned task also isolates synchronous SDK/custom-client writes from the opening caller.
        _producer = Task.Run(() => ProduceAsync(download));
    }

    private async Task ProduceAsync(Func<Stream, CancellationToken, Task> download)
    {
        Exception? failure = null;
        try
        {
            using var destination = new DownloadDestinationStream(_pipe.Writer, _lifetime.Token);
            await download(destination, _lifetime.Token).ConfigureAwait(false);
            _lifetime.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await _pipe.Writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    public override bool CanRead => _disposeTask is null;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        Guard.NotDisposed(_disposeTask is not null, this);
        _lifetime.Token.ThrowIfCancellationRequested();
        return _reader.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposeTask is not null, this);
        _lifetime.Token.ThrowIfCancellationRequested();
#if NETSTANDARD2_0 || NETFRAMEWORK
        using var registration = cancellationToken.Register(
#else
        using var registration = cancellationToken.UnsafeRegister(
#endif
            static state => ((CancellationTokenSource)state!).Cancel(), _lifetime);
        return await _reader.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAsync().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private Task StopAsync()
    {
        lock (_disposeLock)
        {
            return _disposeTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            try
            {
#if NETSTANDARD2_0 || NETFRAMEWORK
                _lifetime.Cancel();
#else
                await _lifetime.CancelAsync().ConfigureAwait(false);
#endif
            }
            finally
            {
                // Even a failing custom cancellation callback must not leave the producer unobserved.
                try
                {
                    await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _producer.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _reader.Dispose();
            _lifetime.Dispose();
        }
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Split arbitrary SDK writes before flushing: PipeWriter.AsStream alone permits an unbounded
// single write to exceed the pause threshold before backpressure can take effect.
internal sealed class DownloadDestinationStream(PipeWriter writer, CancellationToken lifetime) : CompatibleStream
{
    private const int MaximumWriteSize = 16 * 1024;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            lifetime.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length, MaximumWriteSize);
            buffer[..length].CopyTo(writer.GetSpan(length));
            writer.Advance(length);
            CheckFlush(writer.FlushAsync(lifetime).AsTask().GetAwaiter().GetResult());
            buffer = buffer[length..];
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        while (!buffer.IsEmpty)
        {
            linked.Token.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length, MaximumWriteSize);
            CheckFlush(await writer.WriteAsync(buffer[..length], linked.Token).ConfigureAwait(false));
            buffer = buffer[length..];
        }
    }

    public override void Flush() => CheckFlush(writer.FlushAsync(lifetime).AsTask().GetAwaiter().GetResult());
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        CheckFlush(await writer.FlushAsync(linked.Token).ConfigureAwait(false));
    }

    private static void CheckFlush(FlushResult result)
    {
        if (result.IsCanceled || result.IsCompleted)
        {
            throw new OperationCanceledException("The download reader was closed.");
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
