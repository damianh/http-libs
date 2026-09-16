using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

[Collection("Spool budget")]
public sealed class StreamingCompatibilityTests : IDisposable
{
    private const string Url = "https://compatibility.example.test/stream";
    private readonly string _directory = StorageDirectory.New() + "-\u00e9";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    public static bool IsUnixHost => Path.DirectorySeparatorChar == '/';

    private CacheFixture Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond, MemoryStore store,
        Action<HttpHybridCacheHandlerOptions>? configure = null) =>
        new(respond, options =>
        {
            options.LargeContentThreshold = 16;
            options.CompressionThreshold = 0;
            options.SpoolMemoryThreshold = 32;
            options.SpoolDirectory = _directory;
            configure?.Invoke(options);
        }, store);

    private static HttpResponseMessage Response(Stream stream, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.ContentLength = length;
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        return response;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_reads_publish_only_at_EOF_and_hash_exact_bytes(bool knownLength)
    {
        var bytes = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        using var source = new ChunkStream(bytes);
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(source, knownLength ? bytes.Length : null), store);
        using var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(0, source.Reads);
        using var stream = await response.Content.ReadAsStreamAsync();
        var first = new byte[8];
        Assert.Equal(8, await stream.ReadAsync(first, 0, first.Length, Ct));
        Assert.Equal(bytes.Take(8), first);
        Assert.Equal(0, store.Writes);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        using var remainder = new MemoryStream();
        await stream.CopyToAsync(remainder, 81920, Ct);
        Assert.Equal(bytes.Skip(8), remainder.ToArray());
        Assert.Equal(1, store.Writes);
        using var sha = SHA256.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        Assert.Equal("httpcache:content:raw:" + hash, store.LastKey);
        using var cached = await fixture.OnlyIfCached(Url);
        Assert.Equal(bytes, await cached.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, fixture.Origin.Calls);
        AssertNoSpools();
    }

    [Fact]
    public async Task Premature_EOF_never_publishes()
    {
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(new ChunkStream(new byte[127]), 128), store);
        using var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAnyAsync<IOException>(() => stream.CopyToAsync(Stream.Null, 81920, Ct));
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        Assert.Equal(0, store.Writes);
        AssertNoSpools();
    }

    [Fact]
    public async Task Early_disposal_does_not_drain_or_publish()
    {
        using var source = new ChunkStream(new byte[128]);
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(source), store);
        var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(64, await stream.ReadAsync(new byte[64], 0, 64, Ct));
        response.Dispose();
        Assert.Equal(1, source.Reads);
        Assert.True(source.Disposed);
        Assert.Equal(0, store.Writes);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        AssertNoSpools();
    }

    [Fact]
    public async Task Cancellation_during_read_releases_disk_and_never_publishes()
    {
        using var source = new ChunkStream(new byte[128], blockAfterFirst: true);
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(source), store);
        using var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(64, await stream.ReadAsync(new byte[64], 0, 64, Ct));
        using var cancellation = new CancellationTokenSource();
        var read = stream.ReadAsync(new byte[64], 0, 64, cancellation.Token);
        Assert.Same(source.Waiting.Task, await Task.WhenAny(source.Waiting.Task, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, store.Writes);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        AssertNoSpools();
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1024, 0)]
    [InlineData(64, 32)]
    public async Task Exhausted_disk_budget_bypasses_without_truncating(long bytes, int count)
    {
        var payload = new byte[128 * 1024];
        new Random(17).NextBytes(payload);
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(new ChunkStream(payload)), store,
            options => { options.MaxSpoolDiskBytes = bytes; options.MaxConcurrentDiskSpools = count; });
        using var response = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, store.Writes);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        AssertNoSpools();
    }

    [Fact]
    public async Task Unsafe_invalidation_prevents_inflight_fill_publication()
    {
        var store = new MemoryStore();
        using var fixture = Fixture(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Response(new ChunkStream(new byte[128])), store);
        using var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var stream = await response.Content.ReadAsStreamAsync();
        Assert.Equal(64, await stream.ReadAsync(new byte[64], 0, 64, Ct));
        using var posted = await fixture.Client.PostAsync(Url, new StringContent("change"), Ct);
        await stream.CopyToAsync(Stream.Null, 81920, Ct);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        AssertNoSpools();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_waits_for_upload_and_disposal_cancels_it(bool dispose)
    {
        var store = new MemoryStore { UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var fixture = Fixture(_ => Response(new ChunkStream(new byte[128])), store);
        using var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var stream = await response.Content.ReadAsStreamAsync();
        var copy = stream.CopyToAsync(Stream.Null, 81920, Ct);
        try
        {
            Assert.Same(store.UploadStarted.Task,
                await Task.WhenAny(store.UploadStarted.Task, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
            Assert.False(copy.IsCompleted);
            using var absent = await fixture.OnlyIfCached(Url);
            Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
            if (dispose)
            {
                response.Dispose();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copy);
                using var canceled = await fixture.OnlyIfCached(Url);
                Assert.Equal(HttpStatusCode.GatewayTimeout, canceled.StatusCode);
                Assert.Equal(0, store.Writes);
            }
            else
            {
                store.UploadGate.TrySetResult(true);
                await copy;
                using var cached = await fixture.OnlyIfCached(Url);
                Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
                Assert.Equal(1, store.Writes);
            }
            AssertNoSpools();
        }
        finally
        {
            store.UploadGate.TrySetResult(true);
        }
    }

    [Fact]
    public async Task Unknown_length_over_cache_limit_is_delivered_without_publication()
    {
        var payload = new byte[128 * 1024];
        new Random(17).NextBytes(payload);
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(new ChunkStream(payload)), store,
            options => options.MaxCacheableContentSize = 1024);
        using var response = await fixture.Client.GetAsync(Url, Ct);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, store.Writes);
        using var absent = await fixture.OnlyIfCached(Url);
        Assert.Equal(HttpStatusCode.GatewayTimeout, absent.StatusCode);
        AssertNoSpools();
    }

    [Fact]
    public async Task Unknown_length_content_serialization_uses_bounded_reads_and_publishes_at_EOF()
    {
        var payload = new byte[128 * 1024];
        new Random(17).NextBytes(payload);
        using var source = new ChunkStream(payload);
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(source), store);
        using var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal(0, source.Reads);
        using var destination = new MemoryStream();
        await response.Content.CopyToAsync(destination);
        Assert.Equal(payload, destination.ToArray());
        Assert.InRange(source.MaximumRequestedRead, 1, 64 * 1024);
        Assert.Equal(1, store.Writes);
        using var cached = await fixture.OnlyIfCached(Url);
        Assert.Equal(payload, await cached.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, fixture.Origin.Calls);
        AssertNoSpools();
    }

    [Fact(SkipUnless = nameof(IsUnixHost), Skip = "Unix directory-mode assertion runs on the Linux compatibility job.")]
    public async Task Active_spool_is_private_on_Unix()
    {
#if NET10_0_OR_GREATER
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This test requires Unix directory modes.");
        }
        var store = new MemoryStore();
        using var fixture = Fixture(_ => Response(new ChunkStream(new byte[128])), store);
        using (var response = await fixture.Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            Assert.Equal(64, await stream.ReadAsync(new byte[64], 0, 64, Ct));
            var directory = Assert.Single(Directory.GetDirectories(_directory, "httpcache-spool-*"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
        AssertNoSpools();
#else
        await Task.CompletedTask;
#endif
    }

    private void AssertNoSpools()
    {
        if (Directory.Exists(_directory))
        {
            Assert.Empty(Directory.EnumerateDirectories(_directory, "httpcache-spool-*"));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private sealed class MemoryStore : ILargeHttpCacheContentStore
    {
        private readonly Dictionary<string, byte[]> _values = new();
        public int Writes { get; private set; }
        public string? LastKey { get; private set; }
        public TaskCompletionSource<bool>? UploadGate { get; set; }
        public TaskCompletionSource<bool> UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(string contentKey, Stream content, long contentLength, IEnumerable<string>? tags, CancellationToken ct)
        {
            Assert.True(content.CanSeek);
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, 81920, ct);
            Assert.Equal(contentLength, copy.Length);
            UploadStarted.TrySetResult(true);
            if (UploadGate != null)
            {
                using var registration = ct.Register(() => UploadGate.TrySetCanceled());
                await UploadGate.Task;
            }
            ct.ThrowIfCancellationRequested();
            _values[contentKey] = copy.ToArray();
            LastKey = contentKey;
            Writes++;
        }

        public ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct) =>
            new(_values.TryGetValue(contentKey, out var bytes) ? new MemoryStream(bytes, false) : null);

        public ValueTask RemoveAsync(string contentKey, CancellationToken ct)
        {
            _values.Remove(contentKey);
            return default;
        }
    }

    private sealed class ChunkStream(byte[] bytes, bool blockAfterFirst = false) : Stream
    {
        private int _position;
        public int Reads { get; private set; }
        public int MaximumRequestedRead { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads++;
            MaximumRequestedRead = Math.Max(MaximumRequestedRead, count);
            if (blockAfterFirst && Reads > 1)
            {
                Waiting.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, ct);
            }
            var length = Math.Min(count, Math.Min(64, bytes.Length - _position));
            Array.Copy(bytes, _position, buffer, offset, length);
            _position += length;
            return length;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
