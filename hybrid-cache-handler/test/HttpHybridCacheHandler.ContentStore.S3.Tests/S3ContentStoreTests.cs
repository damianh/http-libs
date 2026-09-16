using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DamianH.HttpHybridCacheHandler;

public class S3ContentStoreTests
{
    private const int MiB = 1024 * 1024;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16_383)]
    [InlineData(16_384)]
    [InlineData(16_385)]
    [InlineData(200_000)]
    [InlineData(5 * MiB + 19)]
    [InlineData(10 * MiB + 31)]
    public async Task Round_trip_exact_bytes_and_single_key_removal(int length)
    {
        using var client = new TestS3Client();
        var store = Create(client);
        var bytes = new byte[length];
        new Random(1234).NextBytes(bytes);
        using var input = new TrackingStream(bytes);
        await store.WriteAsync("../private?url=secret", input, bytes.Length, ["ignored"], TestContext.Current.CancellationToken);
        Assert.False(input.Disposed);
        Assert.InRange(input.LargestRead, 0, 4096);
        var expectedKey = "tests/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("../private?url=secret")));
        Assert.Equal(expectedKey, client.LastKey);
        Assert.Equal("bucket", client.LastBucket);
        Assert.False(client.GzipEncoding);
        await using (var read = await store.OpenReadAsync("../private?url=secret", TestContext.Current.CancellationToken))
        {
            Assert.NotNull(read);
            using var output = new MemoryStream();
            await read.CopyToAsync(output, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, output.ToArray());
            Assert.False(client.ReadStreams[^1].Disposed);
        }
        Assert.True(client.ReadStreams[^1].Disposed);
        if (length >= 16_384)
        {
            Assert.True(client.Completed);
            Assert.All(client.PartSizes.SkipLast(1), size => Assert.InRange(size, 5L * MiB, 5L * 1024 * MiB));
        }
        else
        {
            Assert.False(client.Completed);
        }
        await store.RemoveAsync("../private?url=secret", TestContext.Current.CancellationToken);
        await store.RemoveAsync("../private?url=secret", TestContext.Current.CancellationToken);
        Assert.Null(await store.OpenReadAsync("../private?url=secret", TestContext.Current.CancellationToken));
        Assert.Equal(2, client.Deletes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Starts_at_current_position_and_retries_each_part_without_crossing_boundaries(
        bool arrayReads, bool synchronousReads)
    {
        using var client = new TestS3Client
        {
            RewindParts = true,
            ArrayReads = arrayReads,
            SynchronousReads = synchronousReads
        };
        var bytes = new byte[5 * MiB + 99];
        new Random(7).NextBytes(bytes);
        using var input = new TrackingStream(bytes) { Position = 17 };
        var store = Create(client);
        await store.WriteAsync("key", input, bytes.Length - 17, null, TestContext.Current.CancellationToken);
        Assert.Equal(bytes[17..], Assert.Single(client.Objects).Value);
        Assert.False(input.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Array_reads_preserve_offsets_and_dispose_the_response(bool synchronous)
    {
        using var client = new TestS3Client { ArrayReads = true, SynchronousReads = synchronous };
        var store = Create(client);
        using var source = new MemoryStream([0, 1, 2, 3]) { Position = 1 };
        await store.WriteAsync("key", source, 3, null, TestContext.Current.CancellationToken);
        var response = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        using (response)
        {
            var buffer = new byte[] { 99, 99, 99, 99, 99 };
            var read = synchronous
                ? response.Read(buffer, 1, 3)
                : await response.ReadAsync(buffer, 1, 3, TestContext.Current.CancellationToken);
            Assert.Equal(3, read);
            Assert.Equal(new byte[] { 99, 1, 2, 3, 99 }, buffer);
            Assert.Equal(0, await response.ReadAsync(buffer, 1, 3, TestContext.Current.CancellationToken));
        }
        Assert.True(client.ReadStreams.Single().Disposed);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("NoSuchKey", 404, true)]
    [InlineData("NoSuchBucket", 404, false)]
    [InlineData("NotFound", 404, false)]
    [InlineData("AccessDenied", 403, false)]
    [InlineData("SlowDown", 503, false)]
    [InlineData("TooManyRequests", 429, false)]
    [InlineData("InternalError", 500, false)]
    public async Task Only_missing_key_is_a_miss_or_idempotent_remove(string code, int status, bool missing)
    {
        var error = new AmazonS3Exception("provider failure") { ErrorCode = code, StatusCode = (HttpStatusCode)status };
        using var client = new TestS3Client { ReadFailure = error, DeleteFailure = error };
        var store = Create(client);
        if (missing)
        {
            Assert.Null(await store.OpenReadAsync("key", TestContext.Current.CancellationToken));
            await store.RemoveAsync("key", TestContext.Current.CancellationToken);
        }
        else
        {
            Assert.Same(error, await Assert.ThrowsAsync<AmazonS3Exception>(() => store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask()));
            Assert.Same(error, await Assert.ThrowsAsync<AmazonS3Exception>(() => store.RemoveAsync("key", TestContext.Current.CancellationToken).AsTask()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_part_or_completion_aborts_and_preserves_previous_object(bool failCompletion)
    {
        using var client = new TestS3Client();
        var store = Create(client);
        using var original = new MemoryStream([1, 2, 3]);
        await store.WriteAsync("key", original, original.Length, null, TestContext.Current.CancellationToken);
        var error = new AmazonS3Exception("upload rejected") { ErrorCode = "AccessDenied" };
        client.UploadFailure = failCompletion ? null : error;
        client.CompleteFailure = failCompletion ? error : null;
        using var input = new TrackingStream(new byte[5 * MiB + 9]);
        var actual = await Assert.ThrowsAsync<HttpCacheContentStoreException>(() =>
            store.WriteAsync("key", input, input.Length, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Same(error, actual.InnerException);
        Assert.Equal(1, client.Aborts);
        Assert.Empty(client.Pending);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(client.Objects).Value);
        Assert.False(input.Disposed);
    }

    [Fact]
    public async Task Cancellation_aborts_with_independent_token_and_keeps_primary_failure()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new TestS3Client
        {
            AfterPart = cancellation.Cancel,
            AbortFailure = new AmazonS3Exception("abort failed")
        };
        using var input = new TrackingStream(new byte[5 * MiB + 9]);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(client).WriteAsync("key", input, input.Length, null, cancellation.Token).AsTask());
        Assert.Equal(1, client.Aborts);
        Assert.False(client.AbortTokenWasCancelled);
        Assert.Same(client.AbortFailure, error.Data["S3MultipartAbortFailure"]);
        Assert.Empty(client.Objects);
        Assert.False(input.Disposed);
    }

    [Fact]
    public async Task Precancelled_operations_do_not_contact_client()
    {
        using var client = new TestS3Client();
        var store = Create(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync("key", input, 1, null, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync("key", cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveAsync("key", cancellation.Token).AsTask());
        Assert.Null(client.LastKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_input_never_publishes_and_is_not_disposed(bool multipart)
    {
        using var client = new TestS3Client();
        using var input = new TrackingStream(new byte[multipart ? 20_000 : 100]) { EndAfter = 21 };
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            Create(client).WriteAsync("key", input, input.Length, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(client.Objects);
        Assert.Equal(multipart ? 1 : 0, client.Aborts);
        Assert.False(input.Disposed);
    }

    [Fact]
    public async Task Mismatched_length_and_unreadable_or_nonseekable_inputs_are_rejected()
    {
        using var client = new TestS3Client();
        var store = Create(client);
        using var input = new MemoryStream([1, 2]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("key", input, 1, null, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("key", input, 3, null, TestContext.Current.CancellationToken).AsTask());
        using var nonseekable = new TrackingStream([1]) { Seekable = false };
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("key", nonseekable, 1, null, TestContext.Current.CancellationToken).AsTask());
        input.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("key", input, 2, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Null(client.LastKey);
    }

    [Fact]
    public async Task Objects_above_AWS_limit_are_rejected_before_upload()
    {
        using var client = new TestS3Client();
        using var input = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Create(client).WriteAsync(
            "key", input, 5L * 1024 * MiB * 10_000 + 1, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Null(client.LastKey);
    }

    [Theory]
    [InlineData(5L * MiB * 10_000 + 1, 5L * MiB + 1)]
    [InlineData(5L * 1024 * MiB * 10_000, 5L * 1024 * MiB)]
    public async Task Large_object_planning_increases_part_size_without_buffering(long length, long expectedPartSize)
    {
        using var client = new TestS3Client { UploadFailure = new AmazonS3Exception("stop before reading huge input") };
        using var input = new LengthOnlyStream(length);
        await Assert.ThrowsAsync<HttpCacheContentStoreException>(() =>
            Create(client).WriteAsync("key", input, length, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(expectedPartSize, client.LastRequestedPartSize);
        Assert.Equal(1, client.Aborts);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_put_and_initiation_service_errors_preserve_failure_and_ownership(bool multipart)
    {
        var error = new AmazonS3Exception("bucket does not exist") { ErrorCode = "NoSuchBucket" };
        using var client = new TestS3Client { PutFailure = error, InitiationFailure = error };
        using var input = new TrackingStream(new byte[multipart ? 20_000 : 100]);
        var failure = await Assert.ThrowsAsync<HttpCacheContentStoreException>(() =>
            Create(client).WriteAsync("key", input, input.Length, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Same(error, failure.InnerException);
        Assert.False(input.Disposed);
        Assert.Empty(client.Objects);
        Assert.Equal(0, client.Aborts);
    }

    [Fact]
    public async Task Missing_multipart_checksum_aborts_before_publication()
    {
        using var client = new TestS3Client { OmitChecksum = true };
        using var input = new TrackingStream(new byte[20_000]);
        await Assert.ThrowsAsync<IOException>(() => Create(client).WriteAsync(
            "key", input, input.Length, null, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, client.Aborts);
        Assert.Empty(client.Objects);
        Assert.False(input.Disposed);
    }

    [Fact]
    public async Task Transport_upload_errors_use_stable_failure_seam_but_argument_and_cancellation_errors_do_not()
    {
        foreach (var error in new Exception[]
        {
            new HttpRequestException("connection failed"),
            new ArgumentException("invalid request"),
            new OperationCanceledException("cancelled")
        })
        {
            using var client = new TestS3Client { PutFailure = error };
            using var input = new TrackingStream([1, 2, 3]);
            var failure = await Record.ExceptionAsync(() => Create(client).WriteAsync(
                "key", input, input.Length, null, TestContext.Current.CancellationToken).AsTask());
            if (error is HttpRequestException)
                Assert.Same(error, Assert.IsType<HttpCacheContentStoreException>(failure).InnerException);
            else
                Assert.Same(error, failure);
            Assert.False(input.Disposed);
            Assert.Empty(client.Objects);
        }
    }

    [Fact]
    public async Task Independent_readers_own_their_response_and_DI_does_not_dispose_supplied_client()
    {
        using var client = new TestS3Client();
        var services = new ServiceCollection();
        services.AddSingleton<IAmazonS3>(client);
        services.AddHttpHybridCacheS3ContentStore(options => options.BucketName = "bucket");
        await using (var provider = services.BuildServiceProvider())
        {
            var store = provider.GetRequiredService<ILargeHttpCacheContentStore>();
            Assert.Same(provider.GetRequiredService<S3ContentStore>(), store);
            using var input = new MemoryStream([4, 5]);
            await store.WriteAsync("key", input, 2, null, TestContext.Current.CancellationToken);
            var first = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
            await using var second = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
            Assert.NotNull(first);
            Assert.NotNull(second);
            first.Dispose();
            first.Dispose();
            Assert.True(client.ReadStreams[0].Disposed);
            Assert.False(client.ReadStreams[1].Disposed);
            Assert.Equal(4, second.ReadByte());
            Assert.Throws<ObjectDisposedException>(() => first.ReadByte());
        }
        Assert.False(client.Disposed);
    }

    [Fact]
    public void Invalid_transfer_settings_and_overlong_prefix_fail_early()
    {
        using var client = new TestS3Client();
        foreach (var configure in new Action<S3ContentStoreOptions>[]
        {
            options => options.PartSize = 5 * MiB - 1,
            options => options.PartSize = 5L * 1024 * MiB + 1,
            options => options.TransferBufferSize = 0,
            options => options.MultipartThreshold = 0,
            options => options.MultipartThreshold = 5_000_000_001,
            options => options.AbortTimeout = TimeSpan.Zero,
            options => options.KeyPrefix = new string('é', 481),
            options => options.BucketName = ""
        })
        {
            var options = new S3ContentStoreOptions { BucketName = "bucket" };
            configure(options);
            Assert.ThrowsAny<ArgumentException>(() => new S3ContentStore(client, Options.Create(options)));
        }
    }

    private static S3ContentStore Create(TestS3Client client) => new(client, Options.Create(new S3ContentStoreOptions
    {
        BucketName = "bucket", KeyPrefix = "tests/", MultipartThreshold = 16_384, PartSize = 5 * MiB, TransferBufferSize = 4096
    }));

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public int LargestRead { get; private set; }
        public long EndAfter { get; init; } = long.MaxValue;
        public bool Seekable { get; init; } = true;
        public override bool CanSeek => Seekable && base.CanSeek;
        public override int Read(Span<byte> buffer)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return Position >= EndAfter ? 0 : base.Read(buffer[..(int)Math.Min(buffer.Length, EndAfter - Position)]);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class LengthOnlyStream(long length) : MemoryStream
    {
        public override long Length => length;
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("This planning test must not read the input.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This planning test must not read the input.");
    }

    private sealed class TestS3Client() : AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public List<byte[]> Pending { get; } = [];
        public List<long> PartSizes { get; } = [];
        public List<TrackingStream> ReadStreams { get; } = [];
        public string? LastKey { get; private set; }
        public string? LastBucket { get; private set; }
        public bool GzipEncoding { get; private set; }
        public bool Completed { get; private set; }
        public bool Disposed { get; private set; }
        public int Deletes { get; private set; }
        public int Aborts { get; private set; }
        public bool AbortTokenWasCancelled { get; private set; }
        public bool RewindParts { get; init; }
        public bool ArrayReads { get; init; }
        public bool SynchronousReads { get; init; }
        public bool OmitChecksum { get; init; }
        public long? LastRequestedPartSize { get; private set; }
        public AmazonS3Exception? ReadFailure { get; init; }
        public AmazonS3Exception? DeleteFailure { get; init; }
        public Exception? PutFailure { get; init; }
        public AmazonS3Exception? InitiationFailure { get; init; }
        public AmazonS3Exception? UploadFailure { get; set; }
        public AmazonS3Exception? CompleteFailure { get; set; }
        public AmazonS3Exception? AbortFailure { get; init; }
        public Action? AfterPart { get; init; }

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (PutFailure is not null) throw PutFailure;
            LastKey = request.Key;
            LastBucket = request.BucketName;
            GzipEncoding |= request.Headers.ContentEncoding == "gzip";
            Assert.False(request.AutoCloseStream);
            Assert.Equal(request.InputStream.Length, request.Headers.ContentLength);
            Objects[request.Key] = await ReadBytes(request.InputStream, cancellationToken);
            return new PutObjectResponse();
        }

        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            if (InitiationFailure is not null) throw InitiationFailure;
            Assert.Equal(ChecksumAlgorithm.SHA256, request.ChecksumAlgorithm);
            LastKey = request.Key;
            LastBucket = request.BucketName;
            GzipEncoding |= request.Headers.ContentEncoding == "gzip";
            Pending.Clear();
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload" });
        }

        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            LastRequestedPartSize = request.PartSize;
            if (UploadFailure is not null) throw UploadFailure;
            Assert.Equal(ChecksumAlgorithm.SHA256, request.ChecksumAlgorithm);
            Assert.Equal("upload", request.UploadId);
            Assert.Equal(Pending.Count + 1, request.PartNumber);
            Assert.Equal(request.PartSize, request.InputStream.Length);
            var bytes = await ReadBytes(request.InputStream, cancellationToken);
            if (RewindParts)
            {
                request.InputStream.Position = 0;
                Assert.Equal(bytes, await ReadBytes(request.InputStream, cancellationToken));
            }
            PartSizes.Add(request.PartSize!.Value);
            Pending.Add(bytes);
            AfterPart?.Invoke();
            return new UploadPartResponse
            {
                ETag = $"etag-{request.PartNumber}",
                ChecksumSHA256 = OmitChecksum ? null : Convert.ToBase64String(SHA256.HashData(bytes))
            };
        }

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            if (CompleteFailure is not null) throw CompleteFailure;
            Assert.Equal(Pending.Count, request.PartETags.Count);
            for (var index = 0; index < Pending.Count; index++)
                Assert.Equal(Convert.ToBase64String(SHA256.HashData(Pending[index])), request.PartETags[index].ChecksumSHA256);
            Objects[request.Key] = Pending.SelectMany(bytes => bytes).ToArray();
            Pending.Clear();
            Completed = true;
            return Task.FromResult(new CompleteMultipartUploadResponse());
        }

        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Aborts++;
            AbortTokenWasCancelled = cancellationToken.IsCancellationRequested;
            if (AbortFailure is not null) throw AbortFailure;
            Pending.Clear();
            return Task.FromResult(new AbortMultipartUploadResponse());
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (ReadFailure is not null) throw ReadFailure;
            if (!Objects.TryGetValue(request.Key, out var bytes))
                throw new AmazonS3Exception("missing") { ErrorCode = "NoSuchKey", StatusCode = HttpStatusCode.NotFound };
            var stream = new TrackingStream(bytes);
            ReadStreams.Add(stream);
            return Task.FromResult(new GetObjectResponse { ResponseStream = stream });
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            Deletes++;
            Objects.Remove(request.Key);
            return Task.FromResult(new DeleteObjectResponse());
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private async Task<byte[]> ReadBytes(Stream input, CancellationToken ct)
        {
            using var output = new MemoryStream();
            if (ArrayReads)
            {
                var buffer = new byte[128 * 1024 + 2];
                buffer[0] = buffer[^1] = 99;
                int count;
                while ((count = SynchronousReads
                    ? input.Read(buffer, 1, buffer.Length - 2)
                    : await input.ReadAsync(buffer, 1, buffer.Length - 2, ct)) != 0)
                {
                    output.Write(buffer, 1, count);
                }
                Assert.Equal(99, buffer[0]);
                Assert.Equal(99, buffer[^1]);
            }
            else
            {
                await input.CopyToAsync(output, 128 * 1024, ct);
            }
            return output.ToArray();
        }
    }
}
