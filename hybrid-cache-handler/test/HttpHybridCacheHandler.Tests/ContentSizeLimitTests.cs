// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using DamianH.FileDistributedCache;
using Microsoft.Extensions.DependencyInjection;

namespace DamianH.Http.HybridCacheHandler;

public class ContentSizeLimitTests
{
    private const string TestUrl = "https://example.com/resource";
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Response_within_size_limit_is_cached()
    {
        // 1 KB content
        var content = new string('x', 1024);
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = 10 * 1024); // 10 KB
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(1); // Second request was cached
    }

    [Fact]
    public async Task Response_exceeding_size_limit_by_ContentLength_is_not_cached()
    {
        // 20 KB content (exceeds 10 KB limit)
        var content = new string('x', 20 * 1024);
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = 10 * 1024); // 10 KB
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(2); // Not cached due to size
    }

    [Fact]
    public async Task Default_max_size_is_10MB()
    {
        // 5 MB content (within default 10 MB limit)
        var content = new byte[5 * 1024 * 1024];
        Array.Fill(content, (byte)'x');

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new ByteArrayContent(content)
        };
        mockResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(1); // Cached with default limit
    }

    [Fact]
    public async Task Response_exceeding_11MB_with_default_limit_is_not_cached()
    {
        // 11 MB content (exceeds default 10 MB limit)
        var content = new byte[11 * 1024 * 1024];
        Array.Fill(content, (byte)'x');

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new ByteArrayContent(content)
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(2); // Not cached due to size
    }

    [Fact]
    public async Task Custom_max_size_can_be_configured()
    {
        // 500 KB content
        var content = new byte[500 * 1024];
        Array.Fill(content, (byte)'x');

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new ByteArrayContent(content)
        };
        mockResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = 1024 * 1024); // 1 MB
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(1); // Cached within custom limit
    }

    [Fact]
    public async Task Response_without_ContentLength_header_is_checked_after_reading()
    {
        // 20 KB content without ContentLength header
        var content = new string('x', 20 * 1024);
        var response = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(content)
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };

        // Remove ContentLength to simulate chunked transfer
        response.Content.Headers.ContentLength = null;

        var mockHandler = new MockHttpMessageHandler(response);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = 10 * 1024); // 10 KB
        using var client = fixture.CreateClient();

        // First request should work but not cache (size check after reading)
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Second request should hit origin (not cached)
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Zero_size_limit_prevents_all_caching()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("small")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = 0);
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(2); // No caching with zero limit
    }

    [Fact]
    public async Task Response_between_1MiB_and_10MiB_is_cached_with_default_settings()
    {
        // 2 MiB — above HybridCache's old default MaximumPayloadBytes (1 MiB) but within
        // MaxCacheableContentSize (10 MiB). Without the fix this is silently never cached.
        var content = new byte[2 * 1024 * 1024];
        Array.Fill(content, (byte)'x');
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "HttpHybridCacheHandler.Tests",
            $"{nameof(Response_between_1MiB_and_10MiB_is_cached_with_default_settings)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDirectory);

        try
        {
            var firstResponse = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(content)
            };
            firstResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            firstResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            var firstHandler = new MockHttpMessageHandler(firstResponse);
            await using (var firstFixture = new HttpHybridCacheHandlerFixture(
                             firstHandler,
                             configureServices: services => services.AddFileDistributedCache(options => options.CacheDirectory = cacheDirectory)))
            {
                using var firstClient = firstFixture.CreateClient();
                await firstClient.GetAsync(TestUrl, _ct);
            }
            firstHandler.RequestCount.ShouldBe(1);

            var secondResponse = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(content)
            };
            secondResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            secondResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            var secondHandler = new MockHttpMessageHandler(secondResponse);
            await using (var secondFixture = new HttpHybridCacheHandlerFixture(
                             secondHandler,
                             configureServices: services => services.AddFileDistributedCache(options => options.CacheDirectory = cacheDirectory)))
            {
                using var secondClient = secondFixture.CreateClient();
                using var cachedResponse = await secondClient.GetAsync(TestUrl, _ct);
                var cachedContent = await cachedResponse.Content.ReadAsByteArrayAsync(_ct);
                cachedContent.Length.ShouldBe(content.Length);
            }
            secondHandler.RequestCount.ShouldBe(0); // Served from distributed cache in a new service provider
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Empty_response_is_cacheable()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = 1024);
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);
        await client.GetAsync(TestUrl, _ct);

        mockHandler.RequestCount.ShouldBe(1); // Empty response cached
    }
}
