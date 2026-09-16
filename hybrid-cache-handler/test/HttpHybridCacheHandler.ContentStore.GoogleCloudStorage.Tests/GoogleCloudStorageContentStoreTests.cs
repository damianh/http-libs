using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google;
using Google.Apis.Download;
using Google.Apis.Storage.v1.Data;
using Google.Apis.Upload;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.DependencyInjection;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace DamianH.HttpHybridCacheHandler;

public class GoogleCloudStorageContentStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024 + 13)]
    public async Task RoundTripPreservesBytesAndCallerOwnership(int size)
    {
        var client = new FakeStorageClient();
        var store = Create(client);
        var bytes = new byte[size];
        new Random(123).NextBytes(bytes);
        using var source = new MemoryStream(bytes);

        await store.WriteAsync("key", source, size, ["advisory"], CancellationToken.None);

        source.CanRead.ShouldBeTrue();
        source.Position.ShouldBe(size);
        client.LastUpload!.ContentEncoding.ShouldBeNull();
        client.LastUpload.ContentType.ShouldBe("application/octet-stream");
        client.LastUploadOptions!.ChunkSize.ShouldBe(UploadObjectOptions.MinimumChunkSize);
        client.LastUploadOptions.UploadValidationMode.ShouldBe(UploadValidationMode.DeleteAndThrow);
        await using var read = (await store.OpenReadAsync("key", CancellationToken.None))!;
        read.CanSeek.ShouldBeFalse();
        (await ReadAll(read)).ShouldBe(bytes);
        client.LastDownloadOptions!.Generation.ShouldBe(123);
        client.LastDownloadOptions.DownloadValidationMode.ShouldBe(DownloadValidationMode.Always);
        client.LastDownloadOptions.ChunkSize.ShouldBe(256 * 1024);
        client.BucketProbes.ShouldBe(0);
    }

    [Fact]
    public async Task IndependentReadersAndConcurrentIdenticalWrites()
    {
        var client = new FakeStorageClient();
        var store = Create(client);
        var bytes = new byte[128 * 1024];
        Random.Shared.NextBytes(bytes);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var source = new MemoryStream(bytes);
            await store.WriteAsync("shared", source, bytes.Length, null, CancellationToken.None);
        }));
        await using var first = (await store.OpenReadAsync("shared", CancellationToken.None))!;
        await using var second = (await store.OpenReadAsync("shared", CancellationToken.None))!;
        first.ShouldNotBeSameAs(second);
        first.ReadByte().ShouldBe(bytes[0]);
        await first.DisposeAsync();
        (await ReadAll(second)).ShouldBe(bytes);
    }

    [Fact]
    public async Task MissingObjectIsNullAndRemoveIsIdempotent()
    {
        var client = new FakeStorageClient();
        var store = Create(client);
        (await store.OpenReadAsync("missing", CancellationToken.None)).ShouldBeNull();
        await store.RemoveAsync("missing", CancellationToken.None);
        await store.RemoveAsync("missing", CancellationToken.None);
        client.BucketProbes.ShouldBe(3);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task MissingObjectWithInaccessibleBucketIsNotHidden(HttpStatusCode bucketFailure)
    {
        var client = new FakeStorageClient { BucketError = ApiError(bucketFailure) };
        var store = Create(client);
        var readError = await Should.ThrowAsync<GoogleApiException>(() =>
            store.OpenReadAsync("missing", CancellationToken.None).AsTask());
        var deleteError = await Should.ThrowAsync<GoogleApiException>(() =>
            store.RemoveAsync("missing", CancellationToken.None).AsTask());
        readError.HttpStatusCode.ShouldBe(bucketFailure);
        deleteError.HttpStatusCode.ShouldBe(bucketFailure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonMissingFailuresPropagateWithoutBucketProbe(HttpStatusCode status)
    {
        var client = new FakeStorageClient { ObjectError = ApiError(status), DeleteError = ApiError(status) };
        var store = Create(client);
        (await Should.ThrowAsync<GoogleApiException>(() =>
            store.OpenReadAsync("key", CancellationToken.None).AsTask())).HttpStatusCode.ShouldBe(status);
        (await Should.ThrowAsync<GoogleApiException>(() =>
            store.RemoveAsync("key", CancellationToken.None).AsTask())).HttpStatusCode.ShouldBe(status);
        client.BucketProbes.ShouldBe(0);
    }

    [Fact]
    public async Task InitialTransportFailurePropagates()
    {
        var expected = new HttpRequestException("offline");
        var store = Create(new FakeStorageClient { ObjectError = expected });
        (await Should.ThrowAsync<HttpRequestException>(() =>
            store.OpenReadAsync("key", CancellationToken.None).AsTask())).ShouldBeSameAs(expected);
    }

    [Fact]
    public async Task KeysAreOpaqueStableAndRemoveTargetsOneObject()
    {
        var client = new FakeStorageClient();
        var store = Create(client, "my-cache");
        const string key = "../../https://private.example/secret?q=value";
        using var one = new MemoryStream([1]);
        using var two = new MemoryStream([2]);
        await store.WriteAsync(key, one, 1, null, CancellationToken.None);
        var expected = "my-cache/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        client.LastUpload!.Name.ShouldBe(expected);
        await store.WriteAsync("other", two, 1, null, CancellationToken.None);
        await store.RemoveAsync(key, CancellationToken.None);
        client.LastDeleted.ShouldBe(expected);
        client.Objects.Count.ShouldBe(1);
        await using var other = (await store.OpenReadAsync("other", CancellationToken.None))!;
        (await ReadAll(other)).ShouldBe(new byte[] { 2 });
    }

    [Fact]
    public async Task UploadUsesRemainingFixedLengthWithoutDisposingSource()
    {
        var client = new FakeStorageClient { DisposeUploadSource = true };
        var store = Create(client);
        using var source = new MemoryStream([0, 0, 1, 2, 3]);
        source.Position = 2;
        await store.WriteAsync("key", source, 3, null, CancellationToken.None);
        source.CanRead.ShouldBeTrue();
        client.UploadSourceLength.ShouldBe(3);
        client.UploadSourceInitialPosition.ShouldBe(0);
        client.Objects.Single().Value.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task InvalidLengthFailsBeforeUpload(long length)
    {
        var client = new FakeStorageClient();
        using var source = new MemoryStream([1, 2, 3]);
        await Should.ThrowAsync<ArgumentException>(() =>
            Create(client).WriteAsync("key", source, length, null, CancellationToken.None).AsTask());
        client.LastUpload.ShouldBeNull();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task TruncatedSeekableSourceFailsWithoutPublishing()
    {
        var client = new FakeStorageClient();
        using var source = new TruncatedStream();
        await Should.ThrowAsync<EndOfStreamException>(() =>
            Create(client).WriteAsync("key", source, source.Length, null, CancellationToken.None).AsTask());
        client.Objects.ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task FailedUploadPreservesExistingObjectAndCallerStream()
    {
        var client = new FakeStorageClient();
        var store = Create(client);
        using var first = new MemoryStream([1]);
        await store.WriteAsync("key", first, 1, null, CancellationToken.None);
        client.UploadError = ApiError(HttpStatusCode.Forbidden);
        using var replacement = new MemoryStream([2]);
        var error = await Should.ThrowAsync<HttpCacheContentStoreException>(() =>
            store.WriteAsync("key", replacement, 1, null, CancellationToken.None).AsTask());
        error.InnerException.ShouldBeSameAs(client.UploadError);
        replacement.CanRead.ShouldBeTrue();
        client.Objects.Single().Value.ShouldBe(new byte[] { 1 });
    }

    [Fact]
    public async Task UploadValidationFailureKeepsOriginalDetails()
    {
        var expected = new UploadValidationException("AAAAAA==", new StorageObject(),
            new AggregateException(ApiError(HttpStatusCode.Forbidden)));
        var client = new FakeStorageClient { UploadError = expected };
        using var source = new MemoryStream([1]);
        var error = await Should.ThrowAsync<HttpCacheContentStoreException>(() =>
            Create(client).WriteAsync("key", source, 1, null, CancellationToken.None).AsTask());
        error.InnerException.ShouldBeSameAs(expected);
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationDuringUploadDoesNotPublishOrDisposeSource()
    {
        var started = NewSignal();
        var client = new FakeStorageClient
        {
            BeforeUpload = async ct =>
            {
                started.SetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }
        };
        using var cancellation = new CancellationTokenSource();
        using var source = new MemoryStream([1]);
        var write = Create(client).WriteAsync("key", source, 1, null, cancellation.Token).AsTask();
        await started.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => write.WaitAsync(Timeout));
        client.Objects.ShouldBeEmpty();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationBeforeOperationsDoesNotCallProvider()
    {
        var client = new FakeStorageClient();
        var store = Create(client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var source = new MemoryStream([1]);
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.WriteAsync("key", source, 1, null, cancelled.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.OpenReadAsync("key", cancelled.Token).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(() =>
            store.RemoveAsync("key", cancelled.Token).AsTask());
        client.LastUpload.ShouldBeNull();
        client.MetadataCalls.ShouldBe(0);
        client.LastDeleted.ShouldBeNull();
        source.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task DelayedProducerFailurePropagatesThroughReturnedStream()
    {
        var release = NewSignal();
        var expected = new IOException("delayed failure");
        var client = DownloadClient(async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            throw expected;
        });
        await using var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        release.SetResult();
        var error = await Should.ThrowAsync<IOException>(() => ReadAll(stream));
        error.ShouldBeSameAs(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MidstreamFailureAndGenerationDisappearanceAreReadErrors(bool notFound)
    {
        var release = NewSignal();
        Exception expected = notFound ? ApiError(HttpStatusCode.NotFound) : new IOException("bad checksum");
        var client = DownloadClient(async (destination, ct) =>
        {
            await destination.WriteAsync(new byte[] { 7 }, ct);
            await release.Task.WaitAsync(ct);
            throw expected;
        });
        await using var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        var buffer = new byte[1];
        (await stream.ReadAsync(buffer)).ShouldBe(1);
        buffer[0].ShouldBe((byte)7);
        release.SetResult();
        var error = await Record.ExceptionAsync(async () => { _ = await stream.ReadAsync(buffer); });
        error.ShouldBeSameAs(expected);
        client.BucketProbes.ShouldBe(0);
    }

    [Fact]
    public async Task MissingGenerationFailsBeforeProducerStarts()
    {
        var client = DownloadClient((_, _) => Task.CompletedTask);
        client.Generation = null;
        await Should.ThrowAsync<InvalidDataException>(() =>
            Create(client).OpenReadAsync("key", CancellationToken.None).AsTask());
        client.DownloadCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyDisposalCancelsAndJoinsProducer(bool synchronous)
    {
        var started = NewSignal();
        var exited = NewSignal();
        var client = DownloadClient(async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }
            finally
            {
                exited.SetResult();
            }
        });
        var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        await started.Task.WaitAsync(Timeout);
        if (synchronous)
        {
            stream.Dispose();
        }
        else
        {
            await stream.DisposeAsync();
        }
        exited.Task.IsCompleted.ShouldBeTrue();
        await stream.DisposeAsync();
        stream.CanRead.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(() => ReadAll(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenOrReadCancellationStopsActiveProducer(bool cancelOpen)
    {
        var started = NewSignal();
        var exited = NewSignal();
        using var cancellation = new CancellationTokenSource();
        var client = DownloadClient(async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }
            finally
            {
                exited.SetResult();
            }
        });
        await using var stream = (await Create(client).OpenReadAsync("key",
            cancelOpen ? cancellation.Token : CancellationToken.None))!;
        await started.Task.WaitAsync(Timeout);
        var read = stream.ReadAsync(new byte[1], cancelOpen ? CancellationToken.None : cancellation.Token).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => read.WaitAsync(Timeout));
        await exited.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task PipeBackpressureBlocksProducerAndDisposalReleasesIt()
    {
        var blocked = NewSignal();
        var exited = NewSignal();
        var completedWrites = 0;
        var client = DownloadClient(async (destination, ct) =>
        {
            try
            {
                var chunk = new byte[16 * 1024];
                for (var i = 0; i < 4; i++)
                {
                    var write = destination.WriteAsync(chunk, ct);
                    if (i == 3)
                    {
                        write.IsCompleted.ShouldBeFalse();
                        blocked.SetResult();
                    }
                    await write;
                    Interlocked.Increment(ref completedWrites);
                }
            }
            finally
            {
                exited.SetResult();
            }
        });
        await using var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        await blocked.Task.WaitAsync(Timeout);
        Volatile.Read(ref completedWrites).ShouldBe(3);
        await stream.DisposeAsync();
        exited.Task.IsCompleted.ShouldBeTrue();
        Volatile.Read(ref completedWrites).ShouldBe(3);
    }

    [Fact]
    public async Task HugeSingleWriteIsBackpressuredUntilConsumerReads()
    {
        var blocked = NewSignal();
        var finished = NewSignal();
        var bytes = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        var client = DownloadClient(async (destination, ct) =>
        {
            var write = destination.WriteAsync(bytes, ct);
            write.IsCompleted.ShouldBeFalse();
            blocked.SetResult();
            await write;
            finished.SetResult();
        });
        await using var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        await blocked.Task.WaitAsync(Timeout);
        finished.Task.IsCompleted.ShouldBeFalse();
        (await ReadAll(stream)).ShouldBe(bytes);
        await finished.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task SynchronousProducerWritesAndSpanReadsWork()
    {
        var client = DownloadClient((destination, _) =>
        {
            destination.Write(new byte[] { 1, 2, 3 }.AsSpan());
            destination.Flush();
            return Task.CompletedTask;
        });
        using var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        Span<byte> result = stackalloc byte[3];
        stream.Read(result).ShouldBe(3);
        result.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        stream.ReadByte().ShouldBe(-1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrayWritesAndReadsPreserveOffsetsAndBackpressure(bool synchronous)
    {
        var bytes = new byte[256 * 1024 + 2];
        new Random(42).NextBytes(bytes);
        var client = DownloadClient(async (destination, ct) =>
        {
            if (synchronous)
            {
                destination.Write(bytes, 1, bytes.Length - 2);
            }
            else
            {
                await destination.WriteAsync(bytes, 1, bytes.Length - 2, ct);
            }
        });
        using var stream = (await Create(client).OpenReadAsync("key", CancellationToken.None))!;
        using var output = new MemoryStream();
        var buffer = new byte[4098];
        buffer[0] = buffer[^1] = 99;
        int count;
        while ((count = synchronous
            ? stream.Read(buffer, 1, buffer.Length - 2)
            : await stream.ReadAsync(buffer, 1, buffer.Length - 2, TestContext.Current.CancellationToken)) != 0)
        {
            output.Write(buffer, 1, count);
        }
        buffer[0].ShouldBe((byte)99);
        buffer[^1].ShouldBe((byte)99);
        output.ToArray().ShouldBe(bytes[1..^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrayUploadReadsSupportRewindingAndKeepSourceOpen(bool synchronous)
    {
        var client = new FakeStorageClient
        {
            Upload = async (source, ct) =>
            {
                source.Length.ShouldBe(3);
                var buffer = new byte[] { 99, 99, 99, 99, 99 };
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    source.Seek(0, SeekOrigin.Begin).ShouldBe(0);
                    var count = synchronous
                        ? source.Read(buffer, 1, 3)
                        : await source.ReadAsync(buffer, 1, 3, ct);
                    count.ShouldBe(3);
                    buffer.ShouldBe(new byte[] { 99, 1, 2, 3, 99 });
                    (await source.ReadAsync(buffer, 1, 3, ct)).ShouldBe(0);
                }
                return buffer[1..^1];
            }
        };
        using var input = new MemoryStream([0, 0, 1, 2, 3]) { Position = 2 };
        await Create(client).WriteAsync("key", input, 3, null, TestContext.Current.CancellationToken);
        input.CanRead.ShouldBeTrue();
        client.Objects.Single().Value.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void DependencyInjectionUsesRegisteredClientAndValidatedOptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton<StorageClient>(new FakeStorageClient());
        services.AddHttpHybridCacheGoogleCloudStorageContentStore(options =>
        {
            options.BucketName = "cache-bucket";
            options.Prefix = "test/";
        });
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILargeHttpCacheContentStore>().ShouldBeOfType<GoogleCloudStorageContentStore>();
        Should.Throw<ArgumentException>(() => new GoogleCloudStorageContentStore(new FakeStorageClient(), new()));
        Should.Throw<ArgumentOutOfRangeException>(() => new GoogleCloudStorageContentStore(new FakeStorageClient(),
            new() { BucketName = "bucket", DownloadBufferSize = 0 }));
    }

    private static GoogleCloudStorageContentStore Create(FakeStorageClient client, string prefix = "http-cache/") =>
        new(client, new() { BucketName = "cache-bucket", Prefix = prefix });

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static GoogleApiException ApiError(HttpStatusCode status) =>
        new("storage", status.ToString()) { HttpStatusCode = status };

    private static FakeStorageClient DownloadClient(Func<Stream, CancellationToken, Task> download) =>
        new() { AssumeObjectExists = true, Download = download };

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes).WaitAsync(Timeout);
        return bytes.ToArray();
    }

    private sealed class TruncatedStream : MemoryStream
    {
        public override long Length => 3;
    }

    private sealed class FakeStorageClient : StorageClient
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public StorageObject? LastUpload { get; private set; }
        public UploadObjectOptions? LastUploadOptions { get; private set; }
        public DownloadObjectOptions? LastDownloadOptions { get; private set; }
        public string? LastDeleted { get; private set; }
        public Exception? ObjectError { get; set; }
        public Exception? BucketError { get; set; }
        public Exception? DeleteError { get; set; }
        public Exception? UploadError { get; set; }
        public bool AssumeObjectExists { get; init; }
        public bool DisposeUploadSource { get; init; }
        public long? Generation { get; set; } = 123;
        public long UploadSourceLength { get; private set; }
        public long UploadSourceInitialPosition { get; private set; }
        public int BucketProbes { get; private set; }
        public int MetadataCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public Func<Stream, CancellationToken, Task>? Download { get; init; }
        public Func<Stream, CancellationToken, Task<byte[]>>? Upload { get; init; }
        public Func<CancellationToken, Task>? BeforeUpload { get; init; }

        public override Task<StorageObject> GetObjectAsync(string bucket, string objectName,
            GetObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            MetadataCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (ObjectError is not null) throw ObjectError;
            if (!AssumeObjectExists && !Objects.ContainsKey(objectName)) throw ApiError(HttpStatusCode.NotFound);
            return Task.FromResult(new StorageObject { Bucket = bucket, Name = objectName, Generation = Generation });
        }

        public override Task<Bucket> GetBucketAsync(string bucket, GetBucketOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BucketProbes++;
            cancellationToken.ThrowIfCancellationRequested();
            if (BucketError is not null) throw BucketError;
            return Task.FromResult(new Bucket { Name = bucket });
        }

        public override async Task<StorageObject> UploadObjectAsync(StorageObject destination, Stream source,
            UploadObjectOptions? options = null, CancellationToken cancellationToken = default,
            IProgress<IUploadProgress>? progress = null)
        {
            LastUpload = destination;
            LastUploadOptions = options;
            UploadSourceLength = source.Length;
            UploadSourceInitialPosition = source.Position;
            if (BeforeUpload is not null) await BeforeUpload(cancellationToken);
            using var bytes = new MemoryStream();
            if (Upload is not null)
            {
                bytes.Write(await Upload(source, cancellationToken));
            }
            else
            {
                await source.CopyToAsync(bytes, cancellationToken);
            }
            if (DisposeUploadSource) source.Dispose();
            if (UploadError is not null) throw UploadError;
            Objects[destination.Name] = bytes.ToArray();
            return destination;
        }

        public override async Task<StorageObject> DownloadObjectAsync(string bucket, string objectName,
            Stream destination, DownloadObjectOptions? options = null, CancellationToken cancellationToken = default,
            IProgress<IDownloadProgress>? progress = null)
        {
            DownloadCalls++;
            LastDownloadOptions = options;
            if (Download is not null)
            {
                await Download(destination, cancellationToken);
            }
            else
            {
                await destination.WriteAsync(Objects[objectName], cancellationToken);
            }
            return new StorageObject { Bucket = bucket, Name = objectName, Generation = Generation };
        }

        public override Task DeleteObjectAsync(string bucket, string objectName,
            DeleteObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastDeleted = objectName;
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteError is not null) throw DeleteError;
            if (!Objects.TryRemove(objectName, out _)) throw ApiError(HttpStatusCode.NotFound);
            return Task.CompletedTask;
        }
    }
}
