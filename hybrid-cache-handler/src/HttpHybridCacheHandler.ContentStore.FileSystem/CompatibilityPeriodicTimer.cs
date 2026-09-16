namespace DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;

// One consumer, one pending tick, and scheduling through the supplied public TimeProvider.
internal sealed class CompatibilityPeriodicTimer : IDisposable
{
    private readonly object _gate = new();
    private readonly ITimer _timer;
    private TaskCompletionSource<bool>? _waiter;
    private bool _signaled;
    private bool _disposed;

    public CompatibilityPeriodicTimer(TimeSpan period, TimeProvider timeProvider)
    {
        if (period.TotalMilliseconds < 1 || period.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
        Guard.NotNull(timeProvider);
        _timer = timeProvider.CreateTimer(static state => ((CompatibilityPeriodicTimer)state!).Tick(),
            this, period, period);
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_waiter is null || _waiter.Task.IsCanceled)
            {
                _signaled = true;
            }
            else
            {
                _waiter.TrySetResult(true);
            }
        }
    }

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> waiter;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return false;
            }
            if (_waiter is not null)
            {
                throw new InvalidOperationException("Only one tick consumer is allowed.");
            }
            if (_signaled)
            {
                _signaled = false;
                return true;
            }

            _waiter = waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            var tick = await waiter.Task.ConfigureAwait(false);
            lock (_gate)
            {
                return tick && !_disposed;
            }
        }
        finally
        {
            lock (_gate)
            {
                _waiter = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _signaled = false;
            _waiter?.TrySetResult(false);
        }
        _timer.Dispose();
    }
}
