using DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class FileSystemCompatibilityTests : IDisposable
{
    private readonly string _directory = StorageDirectory.New();
    private readonly FakeTimeProvider _clock = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FileSystemContentStore Store(TimeSpan? maximumAge = null) => new(
        Options.Create(new FileSystemContentStoreOptions
        {
            RootDirectory = _directory,
            MaximumAge = maximumAge,
            CleanupInterval = TimeSpan.FromSeconds(1)
        }), _clock, NullLogger<FileSystemContentStore>.Instance);

    [Fact]
    public async Task Replacement_removal_and_store_disposal_preserve_existing_readers()
    {
        await using var store = Store();
        using var input = new MemoryStream(new byte[] { 1, 2, 3 });
        await store.WriteAsync("key", input, 3, null, Ct);
        Assert.True(input.CanRead);
        using var original = await store.OpenReadAsync("key", Ct);
        Assert.NotNull(original);
        using var replacement = new MemoryStream(new byte[] { 4, 5 });
        await store.WriteAsync("key", replacement, 2, null, Ct);
        using var current = await store.OpenReadAsync("key", Ct);
        Assert.NotNull(current);
        await store.RemoveAsync("key", Ct);
        Assert.Null(await store.OpenReadAsync("key", Ct));
        await store.DisposeAsync();
        using var oldBytes = new MemoryStream();
        await original.CopyToAsync(oldBytes, 81920, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, oldBytes.ToArray());
        using var currentBytes = new MemoryStream();
        await current.CopyToAsync(currentBytes, 81920, Ct);
        Assert.Equal(new byte[] { 4, 5 }, currentBytes.ToArray());
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "http-cache-content-v1"), "*.tmp"));
    }

    [Fact]
    public async Task Fake_time_drives_retention_timer_and_disposal_joins_it()
    {
        var store = Store(TimeSpan.FromSeconds(2));
        try
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            await store.WriteAsync("key", input, 3, null, Ct);
            _clock.Advance(TimeSpan.FromSeconds(3));
            var deleted = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var read = await store.OpenReadAsync("key", Ct);
                if (read == null)
                {
                    deleted = true;
                    break;
                }
                await Task.Delay(20, Ct);
            }
            Assert.True(deleted, "TimeProvider-driven background retention did not remove the expired entry.");
        }
        finally
        {
            await store.DisposeAsync();
        }
        _clock.Advance(TimeSpan.FromDays(1));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.OpenReadAsync("key", Ct).AsTask());
    }

    [Fact]
    public async Task Cancellation_keeps_previous_complete_entry()
    {
        await using var store = Store();
        using var initial = new MemoryStream(new byte[] { 9 });
        await store.WriteAsync("key", initial, 1, null, Ct);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var replacement = new MemoryStream(new byte[] { 1, 2 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync("key", replacement, 2, null, canceled.Token).AsTask());
        Assert.True(replacement.CanRead);
        using var reader = await store.OpenReadAsync("key", Ct);
        Assert.NotNull(reader);
        Assert.Equal(9, reader.ReadByte());
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "http-cache-content-v1"), "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
