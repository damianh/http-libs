// Copyright Damian Hickey

using System.Net;
using BenchmarkDotNet.Attributes;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benchmarks;

/// <summary>
/// Measures handler staging without allocating the origin body or buffering at the caller.
/// The sink deliberately never serves hits; provider SDK transfer costs are excluded.
/// </summary>
[MemoryDiagnoser]
public class StreamingFillBenchmarks
{
    private ServiceProvider _services = null!;
    private HttpClient _client = null!;

    [Params(1, 32, 128)]
    public int ResponseMiB { get; set; }

    [Params(false, true)]
    public bool Compress { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        _services = services.BuildServiceProvider();
        _client = new HttpClient(new HttpHybridCacheHandler(
            new GeneratedOrigin(ResponseMiB * 1024L * 1024),
            _services.GetRequiredService<HybridCache>(),
            TimeProvider.System,
            contentStore: null,
            new HttpHybridCacheHandlerOptions
            {
                MaxCacheableContentSize = 256 * 1024 * 1024,
                LargeContentThreshold = 1,
                CompressionThreshold = Compress ? 1 : 0
            },
            NullLogger<HttpHybridCacheHandler>.Instance,
            new SinkStore()));
    }

    [Benchmark]
    public async Task ExternalFillAndDrain()
    {
        using var response = await _client.GetAsync(
            "https://example.test/stream", HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _services.Dispose();
    }

    private sealed class SinkStore : ILargeHttpCacheContentStore
    {
        public async ValueTask WriteAsync(string contentKey, Stream content, long contentLength,
            IEnumerable<string>? tags, CancellationToken ct)
            => await content.CopyToAsync(Stream.Null, ct);

        public ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct)
            => ValueTask.FromResult<Stream?>(null);

        public ValueTask RemoveAsync(string contentKey, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class GeneratedOrigin(long length) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GeneratedStream(length))
            };
            response.Headers.CacheControl = new() { MaxAge = TimeSpan.FromHours(1) };
            response.Content.Headers.ContentLength = length;
            response.Content.Headers.ContentType = new("text/plain");
            return Task.FromResult(response);
        }
    }

    private sealed class GeneratedStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            buffer[..count].Fill((byte)'x');
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
