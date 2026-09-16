#if STANDARD_ASSETS || NETFRAMEWORK
using System.Net;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class HttpContentCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_disposes_owned_content_and_joins_pending_buffering(bool readStream)
    {
        using var content = new GatedContent();
        using var cancellation = new CancellationTokenSource();
        Task read = readStream
            ? HttpContentCompatibility.ReadAsStreamAsync(content, cancellation.Token)
            : HttpContentCompatibility.ReadAsByteArrayAsync(content, cancellation.Token);
        Assert.Same(content.Started.Task, await Task.WhenAny(content.Started.Task,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(content.Disposed);
        Assert.True(content.SerializationFinished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precancellation_does_not_start_or_take_ownership_of_content(bool readStream)
    {
        using var content = new GatedContent();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readStream
            ? (Task)HttpContentCompatibility.ReadAsStreamAsync(content, cancellation.Token)
            : HttpContentCompatibility.ReadAsByteArrayAsync(content, cancellation.Token));
        Assert.False(content.Started.Task.IsCompleted);
        Assert.False(content.Disposed);
    }

    private sealed class GatedContent : HttpContent
    {
        private readonly TaskCompletionSource<bool> _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public bool SerializationFinished { get; private set; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Started.TrySetResult(true);
            try
            {
                await _disposed.Task;
                throw new ObjectDisposedException(nameof(GatedContent));
            }
            finally
            {
                SerializationFinished = true;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _disposed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }
}
#endif
