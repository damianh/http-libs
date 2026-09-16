using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob;
using Google.Apis.Download;
using Google.Apis.Upload;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class CloudCompatibilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Key = "../../opaque?key=héllo";

    [Fact]
    public async Task Azure_precanceled_upload_never_needs_credentials_or_takes_stream_ownership()
    {
        var client = new BlobContainerClient(new Uri("https://unused.invalid/container"));
        var store = new AzureBlobContentStore(client);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(Key, content, 3, null, canceled.Token).AsTask());
        Assert.True(content.CanRead);
        Assert.Equal(0, content.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200_001)]
    public async Task S3_single_put_is_bounded_and_reads_are_independently_owned(int length)
    {
        using var client = new FakeS3();
        var store = new S3ContentStore(client, Options.Create(new S3ContentStoreOptions
        {
            BucketName = "test", KeyPrefix = "cache/", TransferBufferSize = 1024
        }));
        var bytes = Payload(length);
        using var input = new MemoryStream(bytes);
        await store.WriteAsync(Key, input, length, null, Ct);
        Assert.True(input.CanRead);
        Assert.Equal("cache/" + KeyHash(), client.Key);
        Assert.InRange(client.MaximumRead, 0, 1024);
        using var first = await store.OpenReadAsync(Key, Ct);
        using var second = await store.OpenReadAsync(Key, Ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        if (length != 0)
        {
            var buffer = Enumerable.Repeat((byte)255, 16).ToArray();
            Assert.Equal(7, await first.ReadAsync(buffer, 3, 7, Ct));
            Assert.Equal(bytes.Take(7), buffer.Skip(3).Take(7));
            Assert.All(buffer.Take(3).Concat(buffer.Skip(10)), value => Assert.Equal(255, value));
        }
        first.Dispose();
        Assert.False(client.ReadStreams[0].CanRead);
        Assert.True(client.ReadStreams[1].CanRead);
        using var copy = new MemoryStream();
        await second.CopyToAsync(copy, 81920, Ct);
        Assert.Equal(bytes, copy.ToArray());
        await store.RemoveAsync(Key, Ct);
        Assert.Null(await store.OpenReadAsync(Key, Ct));
    }

    [Fact]
    public async Task S3_provider_error_is_not_mistaken_for_a_miss()
    {
        using var client = new FakeS3 { FailRead = true };
        var store = new S3ContentStore(client, Options.Create(new S3ContentStoreOptions { BucketName = "test" }));
        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => store.OpenReadAsync(Key, Ct).AsTask());
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(300_001)]
    public async Task Google_streaming_roundtrip_keeps_input_open_and_pins_generation(int length)
    {
        var client = new FakeGoogle();
        var store = new GoogleCloudStorageContentStore(client, new GoogleCloudStorageContentStoreOptions
        {
            BucketName = "test", Prefix = "cache/", DownloadBufferSize = 1024
        });
        var bytes = Payload(length);
        using var input = new MemoryStream(bytes);
        await store.WriteAsync(Key, input, length, null, Ct);
        Assert.True(input.CanRead);
        Assert.Equal("cache/" + KeyHash(), client.Name);
        using var read = await store.OpenReadAsync(Key, Ct);
        Assert.NotNull(read);
        using var output = new MemoryStream();
        await read.CopyToAsync(output, 1024, Ct);
        Assert.Equal(bytes, output.ToArray());
        Assert.Equal(42, client.RequestedGeneration);
    }

    [Fact]
    public async Task Google_download_failure_propagates_through_read_stream()
    {
        var client = new FakeGoogle { FailDownload = true };
        var store = new GoogleCloudStorageContentStore(client, new GoogleCloudStorageContentStoreOptions { BucketName = "test" });
        using var read = await store.OpenReadAsync(Key, Ct);
        Assert.NotNull(read);
        await Assert.ThrowsAsync<IOException>(() => read.CopyToAsync(Stream.Null, 81920, Ct));
    }

    [Fact]
    public async Task Google_early_async_disposal_cancels_and_joins_the_producer()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeGoogle
        {
            Download = async (destination, token) =>
            {
                try
                {
                    await destination.WriteAsync(new byte[64], 0, 64, token);
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    stopped.TrySetResult(true);
                }
            }
        };
        var store = new GoogleCloudStorageContentStore(client,
            new GoogleCloudStorageContentStoreOptions { BucketName = "test", DownloadBufferSize = 1024 });
        using var read = await store.OpenReadAsync(Key, Ct);
        Assert.NotNull(read);
        Assert.Equal(8, await read.ReadAsync(new byte[16], 3, 8, Ct));
        var disposing = ((IAsyncDisposable)read).DisposeAsync().AsTask();
        Assert.Same(disposing, await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
        await disposing;
        Assert.True(stopped.Task.IsCompleted, "DisposeAsync must join the token-aware download producer.");
    }

    [Fact]
    public async Task Google_late_download_fault_is_observed_after_delivered_bytes()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bytes = Payload(64);
        var client = new FakeGoogle
        {
            Download = async (destination, token) =>
            {
                await destination.WriteAsync(bytes, 0, bytes.Length, token);
                await release.Task;
                throw new IOException("late download failure");
            }
        };
        var store = new GoogleCloudStorageContentStore(client,
            new GoogleCloudStorageContentStoreOptions { BucketName = "test", DownloadBufferSize = 1024 });
        using var read = await store.OpenReadAsync(Key, Ct);
        Assert.NotNull(read);
        try
        {
            var buffer = new byte[70];
            Assert.Equal(bytes.Length, await read.ReadAsync(buffer, 3, bytes.Length, Ct));
            Assert.Equal(bytes, buffer.Skip(3).Take(bytes.Length));
            release.TrySetResult(true);
            var exception = await Assert.ThrowsAsync<IOException>(() => read.ReadAsync(buffer, 0, buffer.Length, Ct));
            Assert.Equal("late download failure", exception.Message);
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        new Random(17).NextBytes(bytes);
        return bytes;
    }

    private static string KeyHash()
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Key))).Replace("-", "").ToLowerInvariant();
    }

    private sealed class FakeS3() : AmazonS3Client(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        private byte[]? _bytes;
        public string? Key { get; private set; }
        public int MaximumRead { get; private set; }
        public bool FailRead { get; set; }
        public List<MemoryStream> ReadStreams { get; } = [];

        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            Key = request.Key;
            Assert.False(request.AutoCloseStream);
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024 + 3];
            int count;
            while ((count = await request.InputStream.ReadAsync(buffer, 3, buffer.Length - 3, cancellationToken)) != 0)
            {
                MaximumRead = Math.Max(MaximumRead, count);
                bytes.Write(buffer, 3, count);
            }
            _bytes = bytes.ToArray();
            return new PutObjectResponse();
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            if (FailRead) throw new AmazonS3Exception("denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" };
            if (_bytes == null) throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" };
            var stream = new MemoryStream(_bytes);
            ReadStreams.Add(stream);
            return Task.FromResult(new GetObjectResponse { ResponseStream = stream });
        }

        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            _bytes = null;
            return Task.FromResult(new DeleteObjectResponse());
        }
    }

    private sealed class FakeGoogle : StorageClient
    {
        private byte[] _bytes = [];
        public string? Name { get; private set; }
        public long? RequestedGeneration { get; private set; }
        public bool FailDownload { get; set; }
        public Func<Stream, CancellationToken, Task>? Download { get; set; }

        public override async Task<StorageObject> UploadObjectAsync(StorageObject destination, Stream source,
            UploadObjectOptions? options = null, CancellationToken cancellationToken = default,
            IProgress<IUploadProgress>? progress = null)
        {
            Name = destination.Name;
            Assert.Equal(UploadObjectOptions.MinimumChunkSize, options!.ChunkSize);
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes, 81920, cancellationToken);
            _bytes = bytes.ToArray();
            source.Dispose();
            return destination;
        }

        public override Task<StorageObject> GetObjectAsync(string bucket, string objectName,
            GetObjectOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageObject { Bucket = bucket, Name = objectName, Generation = 42 });

        public override async Task<StorageObject> DownloadObjectAsync(string bucket, string objectName, Stream destination,
            DownloadObjectOptions? options = null, CancellationToken cancellationToken = default,
            IProgress<IDownloadProgress>? progress = null)
        {
            RequestedGeneration = options!.Generation;
            if (FailDownload) throw new IOException("download failed");
            if (Download != null)
            {
                await Download(destination, cancellationToken);
            }
            else
            {
                await destination.WriteAsync(_bytes, 0, _bytes.Length, cancellationToken);
            }
            return new StorageObject { Bucket = bucket, Name = objectName, Generation = 42 };
        }
    }
}
