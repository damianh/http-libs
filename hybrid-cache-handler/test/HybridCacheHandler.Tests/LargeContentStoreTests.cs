// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace DamianH.Http.HybridCacheHandler;

public class LargeContentStoreTests
{
    private const string TestUrl = "https://api.example.com/large-content";
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Large_content_uses_external_store_when_registered()
    {
        var largeStore = new TestLargeContentStore();
        var content = new string('x', 4096);

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
        mockResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options =>
            {
                options.LargeContentThreshold = 1024;
                options.CompressionThreshold = 0;
                options.MaxCacheableContentSize = 1024 * 1024;
            },
            largeContentStore: largeStore);

        using var client = fixture.CreateClient();

        var firstResponse = await client.GetAsync(TestUrl, _ct);
        var secondResponse = await client.GetAsync(TestUrl, _ct);
        var secondContent = await secondResponse.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(1);
        largeStore.WriteCount.ShouldBe(1);
        largeStore.ReadCount.ShouldBeGreaterThanOrEqualTo(1);
        secondContent.Length.ShouldBe(content.Length);
    }

    [Fact]
    public async Task Large_compressible_content_uses_external_store_based_on_original_size()
    {
        var largeStore = new TestLargeContentStore();
        var content = new string('x', 4096);

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
        mockResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options =>
            {
                options.LargeContentThreshold = 1024;
                options.CompressionThreshold = 1;
                options.MaxCacheableContentSize = 1024 * 1024;
            },
            largeContentStore: largeStore);

        using var client = fixture.CreateClient();

        var firstResponse = await client.GetAsync(TestUrl, _ct);
        var secondResponse = await client.GetAsync(TestUrl, _ct);
        var secondContent = await secondResponse.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(1);
        largeStore.WriteCount.ShouldBe(1);
        largeStore.ReadCount.ShouldBeGreaterThanOrEqualTo(1);
        secondContent.ShouldBe(content);
    }

    [Fact]
    public async Task Missing_external_content_falls_back_to_origin()
    {
        var largeStore = new TestLargeContentStore();
        var content = new string('x', 4096);

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
        mockResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };

        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options =>
            {
                options.LargeContentThreshold = 1024;
                options.CompressionThreshold = 0;
                options.MaxCacheableContentSize = 1024 * 1024;
            },
            largeContentStore: largeStore);

        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        largeStore.Clear();
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    private sealed class TestLargeContentStore : ILargeHttpCacheContentStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }
        public int ReadCount { get; private set; }

        public async ValueTask WriteAsync(
            string contentKey,
            Stream content,
            long contentLength,
            IEnumerable<string>? tags,
            Ct ct)
        {
            WriteCount++;
            var bytes = new byte[checked((int)contentLength)];
            await content.ReadExactlyAsync(bytes, ct);
            _entries[contentKey] = bytes;
        }

        public ValueTask<Stream?> OpenReadAsync(string contentKey, Ct ct)
        {
            ReadCount++;
            if (_entries.TryGetValue(contentKey, out var content))
            {
                return ValueTask.FromResult<Stream?>(new MemoryStream(content, writable: false));
            }

            return ValueTask.FromResult<Stream?>(null);
        }

        public ValueTask RemoveAsync(string contentKey, Ct ct)
        {
            _entries.TryRemove(contentKey, out _);
            return ValueTask.CompletedTask;
        }

        public void Clear() => _entries.Clear();
    }
}
