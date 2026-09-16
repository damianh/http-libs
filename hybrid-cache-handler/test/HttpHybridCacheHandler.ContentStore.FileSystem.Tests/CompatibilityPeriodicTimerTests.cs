using Microsoft.Extensions.Time.Testing;

namespace DamianH.HttpHybridCacheHandler.ContentStore.FileSystem.Tests;

public sealed class CompatibilityPeriodicTimerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(-1)]
    [InlineData(4294967295)]
    public void RejectsPeriodsOutsidePeriodicTimerRange(double milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompatibilityPeriodicTimer(TimeSpan.FromMilliseconds(milliseconds), new FakeTimeProvider()));
    }

    [Fact]
    public async Task UsesSuppliedClockAndCoalescesMissedTicks()
    {
        var time = new FakeTimeProvider();
        using var timer = new CompatibilityPeriodicTimer(TimeSpan.FromMinutes(1), time);
        var first = timer.WaitForNextTickAsync().AsTask();
        Assert.False(first.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(first.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromMinutes(20));
        Assert.True(await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken));
        var next = timer.WaitForNextTickAsync().AsTask();
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationCanBeJoinedAndDoesNotStopFutureTicks()
    {
        var time = new FakeTimeProvider();
        using var timer = new CompatibilityPeriodicTimer(TimeSpan.FromMinutes(1), time);
        using var cancellation = new CancellationTokenSource();
        var canceled = timer.WaitForNextTickAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(cancellation.Token, error.CancellationToken);

        var next = timer.WaitForNextTickAsync().AsTask();
        Assert.False(next.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposalReleasesWaiterAndStopsFutureTicks()
    {
        var time = new FakeTimeProvider();
        using var timer = new CompatibilityPeriodicTimer(TimeSpan.FromMinutes(1), time);
        var waiting = timer.WaitForNextTickAsync().AsTask();
        timer.Dispose();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromDays(1));
        Assert.False(await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken));
        timer.Dispose();
    }

    [Fact]
    public async Task RejectsOverlappingConsumers()
    {
        var time = new FakeTimeProvider();
        using var timer = new CompatibilityPeriodicTimer(TimeSpan.FromMinutes(1), time);
        var waiting = timer.WaitForNextTickAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => timer.WaitForNextTickAsync().AsTask());
        timer.Dispose();
        Assert.False(await waiting);
    }
}
