using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DamianH.Http.HybridCacheHandler.ContentStore.FileSystem.Tests;

public sealed class FileSystemContentStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "filesystem-test-data", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger _logger = new();

    private string NamespaceDirectory => Path.Combine(_directory, "http-cache-content-v1");

    private FileSystemContentStore CreateStore(TimeSpan? age = null, long? bytes = null) =>
        new(Options.Create(new FileSystemContentStoreOptions
        {
            RootDirectory = _directory,
            MaximumAge = age,
            MaximumTotalBytes = bytes,
        }), _time, _logger);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200_001)]
    [InlineData(8_000_003)]
    public async Task RoundTripsExactBytesWithoutOwningInput(int length)
    {
        await using var store = CreateStore();
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        using var input = new MemoryStream(bytes);

        await store.WriteAsync("key", input, bytes.LongLength, ["advisory"], TestContext.Current.CancellationToken);

        Assert.True(input.CanRead);
        Assert.Equal(bytes.Length, input.Position);
        using var first = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        using var second = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal(bytes, await ReadAll(first));
        Assert.Equal(0, second.Position);
        Assert.Equal(bytes, await ReadAll(second));
    }

    [Fact]
    public async Task WritesFromCurrentPositionAndUsesBoundedReads()
    {
        await using var store = CreateStore();
        using var input = new ObservedStream(new byte[1_000_000]) { Position = 12 };
        await store.WriteAsync("key", input, input.Length - input.Position, null, TestContext.Current.CancellationToken);
        Assert.InRange(input.LargestRead, 1, 64 * 1024);
        using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(999_988, reader!.Length);
    }

    [Fact]
    public async Task MissingIsNullAndRemovalIsIdempotentAndSingleKey()
    {
        await using var store = CreateStore();
        Assert.Null(await store.OpenReadAsync("missing", TestContext.Current.CancellationToken));
        await store.RemoveAsync("missing", TestContext.Current.CancellationToken);
        await Put(store, "first", [1, 2]);
        await Put(store, "second", [3, 4]);
        await store.RemoveAsync("first", TestContext.Current.CancellationToken);
        await store.RemoveAsync("first", TestContext.Current.CancellationToken);
        Assert.Null(await store.OpenReadAsync("first", TestContext.Current.CancellationToken));
        using var remaining = await store.OpenReadAsync("second", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 3, 4 }, await ReadAll(remaining!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task InvalidLengthNeverPublishes(long length)
    {
        await using var store = CreateStore();
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync("key", input, length, null, TestContext.Current.CancellationToken).AsTask());
        Assert.True(input.CanRead);
        Assert.Empty(Directory.GetFiles(NamespaceDirectory));
    }

    [Fact]
    public async Task NonSeekableOrUnreadableInputIsRejected()
    {
        await using var store = CreateStore();
        using var nonSeekable = new CapabilityStream(canRead: true, canSeek: false);
        using var unreadable = new CapabilityStream(canRead: false, canSeek: true);
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("key", nonSeekable, 0, null, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("key", unreadable, 0, null, TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InaccurateStreamLengthDoesNotReplaceExistingBody(bool excess)
    {
        await using var store = CreateStore();
        await Put(store, "key", [9, 8]);
        using var input = new MisreportedLengthStream(excess ? [1, 2, 3] : [1], 2);
        if (excess)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.WriteAsync("key", input, 2, null, TestContext.Current.CancellationToken).AsTask());
        }
        else
        {
            var error = await Assert.ThrowsAsync<HttpCacheContentStoreException>(() =>
                store.WriteAsync("key", input, 2, null, TestContext.Current.CancellationToken).AsTask());
            Assert.IsType<EndOfStreamException>(error.InnerException);
            Assert.NotEmpty(_logger.Errors);
        }
        using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 9, 8 }, await ReadAll(reader!));
        Assert.Empty(Directory.GetFiles(NamespaceDirectory, "*.tmp"));
    }

    [Fact]
    public async Task PreCancellationAffectsAllOperations()
    {
        await using var store = CreateStore();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync("key", input, 1, null, canceled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync("key", canceled.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveAsync("key", canceled.Token).AsTask());
        Assert.Empty(Directory.GetFiles(NamespaceDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailuresRemoveTempsAndDoNotHideProgrammingErrors(bool operational)
    {
        await using var store = CreateStore();
        Exception failure = operational ? new IOException("Input failed") : new InvalidOperationException("Programming error");
        using var input = new FailingStream(failure);
        if (operational)
        {
            var error = await Assert.ThrowsAsync<HttpCacheContentStoreException>(() =>
                store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
            Assert.Same(failure, error.InnerException);
            Assert.Single(_logger.Errors);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
            Assert.Same(failure, error);
            Assert.Empty(_logger.Errors);
        }

        Assert.True(input.CanRead);
        Assert.Null(await store.OpenReadAsync("key", TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(NamespaceDirectory));
    }

    [Fact]
    public async Task CancellationRemovesStagingAndPreservesPreviousBody()
    {
        await using var store = CreateStore();
        await Put(store, "key", [7]);
        using var cancellation = new CancellationTokenSource();
        using var input = new PausedStream([1, 2, 3]);
        var write = store.WriteAsync("key", input, 3, null, cancellation.Token).AsTask();
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(NamespaceDirectory, "*.tmp"));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.True(input.CanRead);
        Assert.Empty(Directory.GetFiles(NamespaceDirectory, "*.tmp"));
        using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 7 }, await ReadAll(reader!));
    }

    [Fact]
    public async Task AtomicReplacementKeepsActiveReaderAndCoordinatesCleanup()
    {
        await using var store = CreateStore(bytes: 100);
        await Put(store, "key", [9, 8, 7]);
        using var original = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        using var input = new PausedStream([1, 2, 3, 4]);
        var write = store.WriteAsync("key", input, 4, null, TestContext.Current.CancellationToken).AsTask();
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var cleanup = store.CleanupAsync(TestContext.Current.CancellationToken);
        var opening = store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask();
        Assert.False(cleanup.IsCompleted);
        Assert.False(opening.IsCompleted);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(BodyPath("key")));
        input.Resume.TrySetResult();
        await write;
        await cleanup;

        using var replacement = await opening;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await ReadAll(replacement!));
        Assert.Equal(new byte[] { 9, 8, 7 }, await ReadAll(original!));
        Assert.Empty(Directory.GetFiles(NamespaceDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentWritersAndReadersOnlyObserveCompleteBodies()
    {
        await using var store = CreateStore();
        await Put(store, "key", new byte[131_077]);
        var operations = Enumerable.Range(0, 32).Select(async index =>
        {
            if (index % 2 == 0)
            {
                await Put(store, "key", Enumerable.Repeat((byte)index, 131_077).ToArray());
            }
            else
            {
                using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
                var bytes = await ReadAll(reader!);
                Assert.Equal(131_077, bytes.Length);
                Assert.All(bytes, value => Assert.Equal(bytes[0], value));
            }
        });
        await Task.WhenAll(operations);
    }

    [Fact]
    public async Task UnsetLimitsNeverAutomaticallyDeleteBodies()
    {
        await using var store = CreateStore();
        await Put(store, "key", [1]);
        _time.Advance(TimeSpan.FromDays(365));
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.NotNull(reader);
        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public async Task AgeCleanupUsesTimeProviderAndOpenReaderSurvives()
    {
        await using var store = CreateStore(age: TimeSpan.FromMinutes(2));
        await Put(store, "old", [1, 2]);
        using var reader = await store.OpenReadAsync("old", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Put(store, "new", [3, 4]);
        _time.Advance(TimeSpan.FromMinutes(1));
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        Assert.Null(await store.OpenReadAsync("old", TestContext.Current.CancellationToken));
        using var recent = await store.OpenReadAsync("new", TestContext.Current.CancellationToken);
        Assert.NotNull(recent);
        Assert.Equal(new byte[] { 1, 2 }, await ReadAll(reader!));
    }

    [Fact]
    public async Task QuotaEvictsOldestFirstAndIsSoft()
    {
        await using var store = CreateStore(bytes: 5);
        await Put(store, "oldest", [1, 1, 1]);
        using var oldReader = await store.OpenReadAsync("oldest", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Put(store, "middle", [2, 2, 2]);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Put(store, "newest", [3, 3, 3]);
        Assert.Equal(3, Directory.GetFiles(NamespaceDirectory, "*.body").Length);
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        Assert.Null(await store.OpenReadAsync("oldest", TestContext.Current.CancellationToken));
        Assert.Null(await store.OpenReadAsync("middle", TestContext.Current.CancellationToken));
        using var newest = await store.OpenReadAsync("newest", TestContext.Current.CancellationToken);
        Assert.NotNull(newest);
        Assert.Equal(new byte[] { 1, 1, 1 }, await ReadAll(oldReader!));
    }

    [Fact]
    public async Task ReplacementResetsRetentionAgeWithoutChangingExistingReader()
    {
        await using var store = CreateStore(age: TimeSpan.FromMinutes(2));
        await Put(store, "key", [1]);
        using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Put(store, "key", [2]);
        _time.Advance(TimeSpan.FromMinutes(1));
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        using var replacement = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 2 }, await ReadAll(replacement!));
        Assert.Equal(new byte[] { 1 }, await ReadAll(reader!));
        _time.Advance(TimeSpan.FromMinutes(1));
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        Assert.Null(await store.OpenReadAsync("key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovedKeyCanBeRewrittenWhileOldReaderIsAlive()
    {
        await using var store = CreateStore();
        await Put(store, "key", [1]);
        using var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        await store.RemoveAsync("key", TestContext.Current.CancellationToken);
        await Put(store, "key", [2]);
        using var replacement = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 2 }, await ReadAll(replacement!));
        Assert.Equal(new byte[] { 1 }, await ReadAll(reader!));
    }

    [Fact]
    public async Task PeriodicCleanupRunsAndDisposalStopsTimer()
    {
        var store = CreateStore(age: TimeSpan.FromMinutes(1));
        await Put(store, "key", [1]);
        _time.Advance(TimeSpan.FromMinutes(5));
        await EventuallyAsync(() => !File.Exists(BodyPath("key")));
        await Put(store, "after-cleanup", [2]);
        await store.DisposeAsync();
        _time.Advance(TimeSpan.FromDays(1));
        Assert.True(File.Exists(BodyPath("after-cleanup")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.RemoveAsync("key", TestContext.Current.CancellationToken).AsTask());
        using var input = new MemoryStream([3]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
        store.Dispose();
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsActiveWriteButDoesNotDisposeReaders()
    {
        var store = CreateStore();
        await Put(store, "existing", [8]);
        using var reader = await store.OpenReadAsync("existing", TestContext.Current.CancellationToken);
        using var input = new PausedStream([1, 2, 3]);
        var write = store.WriteAsync("key", input, 3, null, TestContext.Current.CancellationToken).AsTask();
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await store.DisposeAsync();
        Assert.True(write.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Empty(Directory.GetFiles(NamespaceDirectory, "*.tmp"));
        Assert.Equal(new byte[] { 8 }, await ReadAll(reader!));
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task StoreDisposalDoesNotCloseReadersAndRemovedReaderRemainsValid()
    {
        var store = CreateStore();
        await Put(store, "key", [4, 5]);
        var reader = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        await store.RemoveAsync("key", TestContext.Current.CancellationToken);
        await store.DisposeAsync();
        Assert.Equal(new byte[] { 4, 5 }, await ReadAll(reader!));
        await reader!.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
    }

    [Fact]
    public async Task MissingRootIsAnErrorNotAMissingBody()
    {
        await using var store = CreateStore();
        Directory.Delete(NamespaceDirectory);
        await Assert.ThrowsAnyAsync<IOException>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<IOException>(() => store.RemoveAsync("key", TestContext.Current.CancellationToken).AsTask());
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<HttpCacheContentStoreException>(() => store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(3, _logger.Errors.Count);
    }

    [Fact]
    public async Task DirectoryAtBodyPathIsAnErrorAndIsNeverRecursivelyRemoved()
    {
        await using var store = CreateStore();
        Directory.CreateDirectory(BodyPath("key"));
        var marker = Path.Combine(BodyPath("key"), "leave-me");
        File.WriteAllText(marker, "private");
        await Assert.ThrowsAnyAsync<IOException>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<IOException>(() => store.RemoveAsync("key", TestContext.Current.CancellationToken).AsTask());
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<HttpCacheContentStoreException>(() => store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task SharingFailureIsLoggedAndDoesNotBecomeMissing()
    {
        await using var store = CreateStore();
        await Put(store, "key", [1]);
        using var blocker = new FileStream(BodyPath("key"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAnyAsync<IOException>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask());
        Assert.NotEmpty(_logger.Errors);
    }

    [Fact]
    public async Task CleanupFailureIsLoggedAndRetriedOnNextTick()
    {
        await using var store = CreateStore(age: TimeSpan.FromSeconds(1));
        var saved = NamespaceDirectory + "-saved";
        Directory.Move(NamespaceDirectory, saved);
        _time.Advance(TimeSpan.FromMinutes(5));
        await EventuallyAsync(() => !_logger.Errors.IsEmpty);
        Directory.Move(saved, NamespaceDirectory);
        await Put(store, "key", [1]);
        _time.Advance(TimeSpan.FromMinutes(5));
        await EventuallyAsync(() => !File.Exists(BodyPath("key")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\Windows\\system.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("a:b\0c")]
    [InlineData("")]
    [InlineData("🌈/../../../private")]
    public async Task KeysNeverBecomePaths(string key)
    {
        await using var store = CreateStore();
        await Put(store, key, [1]);
        Assert.Equal(BodyPath(key), Assert.Single(Directory.GetFiles(NamespaceDirectory)));
        using var read = await store.OpenReadAsync(key, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1 }, await ReadAll(read!));
        await store.RemoveAsync(key, TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(NamespaceDirectory));
    }

    [Fact]
    public async Task StartupCleansOnlyPreciselyOwnedAbandonedTemps()
    {
        Directory.CreateDirectory(NamespaceDirectory);
        var owned = Path.Combine(NamespaceDirectory, $"hhc-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(owned, "abandoned");
        var unrelated = new[]
        {
            Path.Combine(NamespaceDirectory, "unrelated.tmp"),
            Path.Combine(NamespaceDirectory, "hhc-short.tmp"),
            Path.Combine(NamespaceDirectory, $"hhc-{new string('z', 32)}.tmp"),
            Path.Combine(NamespaceDirectory, $"hhc-{new string('f', 64)}.body.backup"),
            Path.Combine(_directory, $"hhc-{Guid.NewGuid():N}.tmp"),
        };
        foreach (var path in unrelated)
        {
            File.WriteAllText(path, "preserve");
        }

        var subdirectory = Path.Combine(NamespaceDirectory, "nested");
        Directory.CreateDirectory(subdirectory);
        var nested = Path.Combine(subdirectory, $"hhc-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(nested, "preserve");
        await using var store = CreateStore(age: TimeSpan.FromSeconds(1), bytes: 1);
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(owned));
        Assert.All(unrelated.Append(nested), path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task ExistingEntrySymlinkIsRejectedWithoutTouchingTarget()
    {
        await using var store = CreateStore(age: TimeSpan.FromSeconds(1), bytes: 1);
        var target = Path.Combine(_directory, "private-data");
        File.WriteAllText(target, "do-not-touch");
        CreateFileLinkOrSkip(BodyPath("key"), target);
        await Assert.ThrowsAnyAsync<IOException>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<IOException>(() => store.RemoveAsync("key", TestContext.Current.CancellationToken).AsTask());
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<HttpCacheContentStoreException>(() => store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
        await store.CleanupAsync(TestContext.Current.CancellationToken);
        Assert.Equal("do-not-touch", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(BodyPath("key")).LinkTarget);
    }

    [Fact]
    public async Task AbandonedTempSymlinkIsLoggedAndNeverDeletesTarget()
    {
        Directory.CreateDirectory(NamespaceDirectory);
        var target = Path.Combine(_directory, "private-data");
        File.WriteAllText(target, "do-not-touch");
        var link = Path.Combine(NamespaceDirectory, $"hhc-{Guid.NewGuid():N}.tmp");
        CreateFileLinkOrSkip(link, target);
        await using var store = CreateStore();
        Assert.Equal("do-not-touch", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Single(_logger.Errors);
    }

    [Fact]
    public void SymlinkedRootOrAncestorIsRejected()
    {
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, "target");
        var link = Path.Combine(_directory, "link");
        Directory.CreateDirectory(target);
        CreateDirectoryLinkOrSkip(link, target);
        Assert.ThrowsAny<IOException>(() => new FileSystemContentStore(
            Options.Create(new FileSystemContentStoreOptions { RootDirectory = Path.Combine(link, "new-child") }), _time, _logger));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task RootReplacedWithLinkIsRejectedOnLaterOperations()
    {
        await using var store = CreateStore();
        Directory.Delete(NamespaceDirectory);
        var target = Path.Combine(_directory, "target");
        Directory.CreateDirectory(target);
        CreateDirectoryLinkOrSkip(NamespaceDirectory, target);
        await Assert.ThrowsAnyAsync<IOException>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<IOException>(() => store.RemoveAsync("key", TestContext.Current.CancellationToken).AsTask());
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<HttpCacheContentStoreException>(() => store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public async Task RegistrationUsesAbstractionsAndInjectedClock()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddHttpHybridCacheFileSystemContentStore(options =>
        {
            options.RootDirectory = _directory;
            options.MaximumAge = TimeSpan.FromMinutes(1);
        });
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ILargeHttpCacheContentStore>();
        Assert.IsType<FileSystemContentStore>(store);
        Assert.Same(store, provider.GetRequiredService<ILargeHttpCacheContentStore>());
        using var input = new MemoryStream([1]);
        await store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(5));
        await EventuallyAsync(() => !File.Exists(BodyPath("key")));
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        void Reject(FileSystemContentStoreOptions options) =>
            Assert.ThrowsAny<ArgumentException>(() => new FileSystemContentStore(Options.Create(options), _time, _logger));

        Reject(new());
        Reject(new() { RootDirectory = "relative" });
        Reject(new() { RootDirectory = _directory, MaximumAge = TimeSpan.Zero });
        Reject(new() { RootDirectory = _directory, MaximumTotalBytes = 0 });
        Reject(new() { RootDirectory = _directory, CleanupInterval = TimeSpan.Zero });
        Reject(new() { RootDirectory = _directory, CleanupInterval = TimeSpan.MaxValue });
    }

    private string BodyPath(string key) => Path.Combine(NamespaceDirectory,
        $"hhc-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.body");

    private static async Task Put(FileSystemContentStore store, string key, byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        await store.WriteAsync(key, input, bytes.LongLength, null, TestContext.Current.CancellationToken);
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void CreateDirectoryLinkOrSkip(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating directory symlinks requires OS permissions.");
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Skip("Creating directory symlinks requires Windows developer mode or elevation.");
        }
    }

    private static void CreateFileLinkOrSkip(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (UnauthorizedAccessException)
        {
            Assert.Skip("Creating file symlinks requires OS permissions.");
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Skip("Creating file symlinks requires Windows developer mode or elevation.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class PausedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int LargestRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class CapabilityStream(bool canRead, bool canSeek) : MemoryStream
    {
        public override bool CanRead => canRead;
        public override bool CanSeek => canSeek;
    }

    private sealed class MisreportedLengthStream(byte[] bytes, long length) : MemoryStream(bytes)
    {
        public override long Length => length;
    }

    private sealed class FailingStream(Exception failure) : MemoryStream(new byte[1])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
    }

    private sealed class RecordingLogger : ILogger<FileSystemContentStore>
    {
        public ConcurrentQueue<Exception> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Errors.Enqueue(exception);
            }
        }
    }
}
