using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using DamianH.Http.HybridCacheHandler;
using DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob;
using Microsoft.Extensions.DependencyInjection;

namespace HttpHybridCacheHandler.ContentStore.AzureBlob.Tests;

public sealed class AzureBlobContentStoreTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(173)]
    [InlineData(10 * 1024 * 1024 + 19)]
    public async Task RoundtripUsesBoundedBlocksAndIndependentOwnedReads(int length)
    {
        using var fixture = new Fixture();
        var bytes = new byte[length];
        new Random(17).NextBytes(bytes);
        using var input = new TrackingStream(bytes);
        await fixture.Store.WriteAsync("key", input, length, ["ignored"], default);

        Assert.False(input.Disposed);
        Assert.InRange(input.MaximumRead, 0, 4 * 1024 * 1024);
        Assert.All(fixture.Transport.StageSizes, size => Assert.InRange(size, 1, 4 * 1024 * 1024));
        Assert.Equal(1, fixture.Transport.Commits);
        Assert.Equal("application/octet-stream", fixture.Transport.StoredContentType);
        Assert.Null(fixture.Transport.StoredContentEncoding);
        Assert.False(fixture.Transport.HasTags);

        var first = await fixture.Store.OpenReadAsync("key", default);
        var second = await fixture.Store.OpenReadAsync("key", default);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        await first.DisposeAsync();
        Assert.True(fixture.Transport.ReadStreams[0].Disposed);
        Assert.False(fixture.Transport.ReadStreams[1].Disposed);
        using var destination = new MemoryStream();
        await second.CopyToAsync(destination);
        Assert.Equal(bytes, destination.ToArray());
        await second.DisposeAsync();
        Assert.True(fixture.Transport.ReadStreams[1].Disposed);
    }

    [Fact]
    public async Task KeyMappingIsOpaqueStableAndNamespaced()
    {
        using var fixture = new Fixture("application/cache/");
        const string key = "../../private?token=secret#fragment";
        using var input = new MemoryStream([1, 2, 3]);
        await fixture.Store.WriteAsync(key, input, 3, null, default);
        var expected = "/container/application/cache/" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        Assert.All(fixture.Transport.Paths, path => Assert.Equal(expected, path));
        await using var result = await fixture.Store.OpenReadAsync(key, default);
        Assert.NotNull(result);
        await fixture.Store.RemoveAsync(key, default);
        await fixture.Store.RemoveAsync(key, default);
        Assert.Null(await fixture.Store.OpenReadAsync(key, default));
        Assert.All(fixture.Transport.Paths, path => Assert.Equal(expected, path));
    }

    [Theory]
    [InlineData(404, "BlobNotFound", true)]
    [InlineData(404, "ContainerNotFound", false)]
    [InlineData(404, null, false)]
    [InlineData(403, "AuthorizationFailure", false)]
    [InlineData(429, "TooManyRequests", false)]
    [InlineData(500, "InternalError", false)]
    public async Task OnlyExactBlobNotFoundIsMissing(int status, string? errorCode, bool missing)
    {
        using var fixture = new Fixture();
        fixture.Transport.Error = (status, errorCode);
        if (missing)
        {
            Assert.Null(await fixture.Store.OpenReadAsync("key", default));
            await fixture.Store.RemoveAsync("key", default);
        }
        else
        {
            var read = await Assert.ThrowsAsync<RequestFailedException>(
                () => fixture.Store.OpenReadAsync("key", default).AsTask());
            Assert.Equal(status, read.Status);
            var remove = await Assert.ThrowsAsync<RequestFailedException>(
                () => fixture.Store.RemoveAsync("key", default).AsTask());
            Assert.Equal(status, remove.Status);
        }
    }

    [Fact]
    public async Task FailedStageOrCommitPreservesPreviousCompleteBody()
    {
        using var fixture = new Fixture();
        using var original = new MemoryStream([7, 8, 9]);
        await fixture.Store.WriteAsync("key", original, original.Length, null, default);
        foreach (var operation in new[] { "block", "blocklist" })
        {
            fixture.Transport.FailOperation = operation;
            using var replacement = new TrackingStream(new byte[5 * 1024 * 1024]);
            var exception = await Assert.ThrowsAsync<HttpCacheContentStoreException>(
                () => fixture.Store.WriteAsync("key", replacement, replacement.Length, null, default).AsTask());
            Assert.IsType<RequestFailedException>(exception.InnerException);
            Assert.False(replacement.Disposed);
            Assert.Equal(1, fixture.Transport.Commits);
            await using var current = await fixture.Store.OpenReadAsync("key", default);
            Assert.NotNull(current);
            Assert.Equal(7, current.ReadByte());
            Assert.Equal(3, current.Length);
        }
    }

    [Fact]
    public async Task TruncatedInputIsNeverCommitted()
    {
        using var fixture = new Fixture();
        using var input = new TruncatedStream();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            fixture.Store.WriteAsync("key", input, input.Length, null, default).AsTask());
        Assert.Equal(0, fixture.Transport.Commits);
        Assert.Null(await fixture.Store.OpenReadAsync("key", default));
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task UploadTransportFailureUsesAbstractionsFailureSeam()
    {
        using var fixture = new Fixture();
        fixture.Transport.UploadTransportFailure = new HttpRequestException("Transport unavailable.");
        using var input = new MemoryStream([1, 2, 3]);
        var exception = await Assert.ThrowsAsync<HttpCacheContentStoreException>(() =>
            fixture.Store.WriteAsync("key", input, 3, null, default).AsTask());
        Assert.IsAssignableFrom<IOException>(exception);
        Assert.NotNull(exception.InnerException);
        Assert.True(exception.InnerException is RequestFailedException or HttpRequestException);
        Assert.Contains("Transport unavailable.", exception.InnerException.ToString());
        Assert.Equal(0, fixture.Transport.Commits);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task CancellationAfterFirstBlockDoesNotPublish()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.AfterStage = cancellation.Cancel;
        using var input = new TrackingStream(new byte[5 * 1024 * 1024]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.WriteAsync("key", input, input.Length, null, cancellation.Token).AsTask());
        Assert.Equal(0, fixture.Transport.Commits);
        Assert.False(input.Disposed);
        Assert.Null(await fixture.Store.OpenReadAsync("key", default));
    }

    [Fact]
    public async Task PreCanceledOperationsDoNotUseTransport()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.WriteAsync("key", input, 1, null, cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.OpenReadAsync("key", cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.RemoveAsync("key", cancellation.Token).AsTask());
        Assert.Empty(fixture.Transport.Paths);
    }

    [Fact]
    public async Task LengthMismatchIsRejectedBeforeTransportAndPositionIsRespected()
    {
        using var fixture = new Fixture();
        using var input = new MemoryStream([0, 1, 2]);
        input.Position = 1;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.WriteAsync("key", input, 3, null, default).AsTask());
        Assert.Empty(fixture.Transport.Paths);
        await fixture.Store.WriteAsync("key", input, 2, null, default);
        await using var result = await fixture.Store.OpenReadAsync("key", default);
        Assert.NotNull(result);
        Assert.Equal(1, result.ReadByte());
        Assert.Equal(2, result.ReadByte());
        Assert.Equal(-1, result.ReadByte());
    }

    [Fact]
    public async Task GrowingInputCannotPublishBeyondDeclaredLength()
    {
        using var fixture = new Fixture();
        using var input = new GrowingStream();
        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Store.WriteAsync("key", input, 3, null, default).AsTask());
        Assert.Equal(0, fixture.Transport.Commits);
        Assert.Null(await fixture.Store.OpenReadAsync("key", default));
    }

    [Fact]
    public async Task InvalidLengthAndUnreadableInputAreRejectedBeforeRequests()
    {
        using var fixture = new Fixture();
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Store.WriteAsync("key", input, -1, null, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Store.WriteAsync("key", input, 209_715_200_001, null, default).AsTask());
        input.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Store.WriteAsync("key", input, 1, null, default).AsTask());
        Assert.Empty(fixture.Transport.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("bad\nprefix")]
    public void InvalidNamespaceIsRejected(string prefix)
    {
        using var fixture = new Fixture();
        Assert.ThrowsAny<ArgumentException>(() =>
            new AzureBlobContentStore(fixture.Container, new() { Namespace = prefix }));
    }

    [Fact]
    public async Task OpenedStreamPropagatesReadFailureAndCancellationAndRemainsDisposable()
    {
        using var fixture = new Fixture();
        using var input = new MemoryStream([1, 2, 3]);
        await fixture.Store.WriteAsync("key", input, 3, null, default);
        var stream = await fixture.Store.OpenReadAsync("key", default);
        Assert.NotNull(stream);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], canceled.Token).AsTask());
        fixture.Transport.ReadStreams[0].FailReads = true;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            stream.ReadAsync(new byte[1]).AsTask());
        await stream.DisposeAsync();
        Assert.True(fixture.Transport.ReadStreams[0].Disposed);
    }

    [Fact]
    public async Task StagedBytesAreNotVisibleBeforeCompleteCommit()
    {
        using var fixture = new Fixture();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.StageGate = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };
        using var input = new MemoryStream([1, 2, 3]);
        var write = fixture.Store.WriteAsync("key", input, 3, null, default).AsTask();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Null(await fixture.Store.OpenReadAsync("key", default));
        }
        finally
        {
            release.TrySetResult();
        }
        await write;
        await using var result = await fixture.Store.OpenReadAsync("key", default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ConcurrentWritesUseDisjointBlockIdsAndPublishWholeBodies()
    {
        using var fixture = new Fixture();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        fixture.Transport.StageGate = async () =>
        {
            if (Interlocked.Increment(ref count) == 2)
                reached.TrySetResult();
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        using var first = new MemoryStream([1, 1, 1]);
        using var second = new MemoryStream([2, 2, 2]);
        await Task.WhenAll(
            fixture.Store.WriteAsync("key", first, 3, null, default).AsTask(),
            fixture.Store.WriteAsync("key", second, 3, null, default).AsTask());
        Assert.Equal(2, fixture.Transport.Blocks.Count);
        await using var result = await fixture.Store.OpenReadAsync("key", default);
        Assert.NotNull(result);
        var value = result.ReadByte();
        Assert.Contains(value, new[] { 1, 2 });
        Assert.Equal(value, result.ReadByte());
        Assert.Equal(value, result.ReadByte());
        Assert.Equal(-1, result.ReadByte());
    }

    [Fact]
    public void RegistrationResolvesDirectContractWithoutHandler()
    {
        using var fixture = new Fixture();
        var services = new ServiceCollection();
        services.AddSingleton(fixture.Container);
        services.AddHttpHybridCacheAzureBlobContentStore(options => options.Namespace = "cache/test");
        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<ILargeHttpCacheContentStore>();
        Assert.IsType<AzureBlobContentStore>(first);
        Assert.Same(first, provider.GetRequiredService<ILargeHttpCacheContentStore>());
    }

    private sealed class Fixture : IDisposable
    {
        public FakeService Transport { get; } = new();
        private readonly HttpClient _http;
        public BlobContainerClient Container { get; }
        public AzureBlobContentStore Store { get; }

        public Fixture(string prefix = "http-cache")
        {
            _http = new HttpClient(Transport);
            var options = new BlobClientOptions
            {
                Transport = new HttpClientTransport(_http),
                Retry = { MaxRetries = 0 }
            };
            Container = new BlobContainerClient(new Uri("https://test.invalid/container"), options);
            Store = new AzureBlobContentStore(Container, new() { Namespace = prefix });
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class FakeService : HttpMessageHandler
    {
        public ConcurrentDictionary<string, byte[]> Blocks { get; } = new();
        private readonly ConcurrentDictionary<string, byte[]> _bodies = new();
        public ConcurrentBag<int> StageSizes { get; } = [];
        public ConcurrentBag<string> Paths { get; } = [];
        public List<TrackingStream> ReadStreams { get; } = [];
        public int Commits;
        public string? StoredContentType;
        public string? StoredContentEncoding;
        public bool HasTags;
        public (int Status, string? Code)? Error;
        public string? FailOperation;
        public HttpRequestException? UploadTransportFailure;
        public Action? AfterStage;
        public Func<Task>? StageGate;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            Paths.Add(path);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            query.TryGetValue("comp", out var operation);
            if (request.Method == HttpMethod.Put && UploadTransportFailure is not null)
                throw UploadTransportFailure;
            if (Error is { } error)
                return Failure(error.Status, error.Code);
            if (operation is not null && operation == FailOperation)
                return Failure(500, "InternalError");
            if (request.Method == HttpMethod.Put && operation == "block")
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Blocks[query["blockid"]] = bytes;
                StageSizes.Add(bytes.Length);
                if (StageGate is not null)
                    await StageGate();
                AfterStage?.Invoke();
                return new(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put && operation == "blocklist")
            {
                var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                _bodies[path] = xml.Root!.Elements().SelectMany(element => Blocks[element.Value]).ToArray();
                Interlocked.Increment(ref Commits);
                StoredContentType = Header(request, "x-ms-blob-content-type");
                StoredContentEncoding = Header(request, "x-ms-blob-content-encoding");
                HasTags = request.Headers.Contains("x-ms-tags");
                return new(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get && _bodies.TryGetValue(path, out var body))
            {
                var stream = new TrackingStream(body);
                ReadStreams.Add(stream);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                };
                response.Content.Headers.ContentLength = body.Length;
                response.Headers.TryAddWithoutValidation("ETag", "\"test-etag\"");
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                return response;
            }
            if (request.Method == HttpMethod.Delete && _bodies.TryRemove(path, out _))
                return new(HttpStatusCode.Accepted);
            return Failure(404, "BlobNotFound");
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

        private static HttpResponseMessage Failure(int status, string? code)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            if (code is not null)
                response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
            response.Content = new StringContent(
                $"<Error><Code>{code}</Code><Message>Failure</Message></Error>", Encoding.UTF8, "application/xml");
            return response;
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }
        public int MaximumRead { get; private set; }
        public bool FailReads { get; set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (FailReads)
                throw new InvalidDataException("Read failed after opening.");
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (FailReads)
                throw new InvalidDataException("Read failed after opening.");
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TruncatedStream() : MemoryStream(new byte[10])
    {
        public override long Length => 20;
    }

    private sealed class GrowingStream() : MemoryStream(new byte[4])
    {
        public override long Length => 3;
    }
}
