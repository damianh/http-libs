// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace DamianH.Http.HybridCacheHandler;

public class CompressionTests
{
    private const string TestUrl = "https://example.com/resource";
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Large_compressible_content_is_compressed()
    {
        // Content larger than default 1KB threshold
        var largeContent = new string('x', 2048);
        var responseContent = Encoding.UTF8.GetBytes(largeContent);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent),
            Headers =
            {
                CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - should cache and compress
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsStringAsync(_ct);
        content1.ShouldBe(largeContent);

        // Second request - should hit cache (compressed)
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content2 = await response2.Content.ReadAsStringAsync(_ct);
        content2.ShouldBe(largeContent);

        // Verify only one backend call
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Small_content_is_not_compressed()
    {
        // Content smaller than default 1KB threshold
        var smallContent = "small content";
        var responseContent = Encoding.UTF8.GetBytes(smallContent);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent),
            Headers =
            {
                CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - should cache without compression
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsStringAsync(_ct);
        content1.ShouldBe(smallContent);

        // Second request - should hit cache
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content2 = await response2.Content.ReadAsStringAsync(_ct);
        content2.ShouldBe(smallContent);
    }

    [Fact]
    public async Task Non_compressible_content_is_not_compressed()
    {
        // Large binary content (image)
        var binaryContent = new byte[2048];
        Random.Shared.NextBytes(binaryContent);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(binaryContent),
            Headers =
            {
                CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - should cache without compression (not compressible)
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsByteArrayAsync(_ct);
        content1.ShouldBe(binaryContent);

        // Second request - should hit cache
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content2 = await response2.Content.ReadAsByteArrayAsync(_ct);
        content2.ShouldBe(binaryContent);
    }

    [Fact]
    public async Task Compression_can_be_disabled()
    {
        var largeContent = new string('x', 2048);
        var responseContent = Encoding.UTF8.GetBytes(largeContent);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.CompressionThreshold = long.MinValue); // Disable compression
        using var client = fixture.CreateClient();

        // First request - should cache without compression
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content1 = await response1.Content.ReadAsStringAsync(_ct);
        content1.ShouldBe(largeContent);

        // Second request - should hit cache
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content2 = await response2.Content.ReadAsStringAsync(_ct);
        content2.ShouldBe(largeContent);
    }

    [Fact]
    public async Task Custom_compression_threshold_is_respected()
    {
        // Content smaller than custom threshold
        var content = new string('x', 2048);
        var responseContent = Encoding.UTF8.GetBytes(content);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent),
            Headers =
            {
                CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.CompressionThreshold = 5000); // Set high compression threshold
        using var client = fixture.CreateClient();

        // Should cache without compression (below threshold)
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseContent2 = await response1.Content.ReadAsStringAsync(_ct);
        responseContent2.ShouldBe(content);
    }

    [Fact]
    public async Task Custom_compressible_types_are_respected()
    {
        var content = new string('x', 2048);
        var responseContent = Encoding.UTF8.GetBytes(content);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent),
            Headers =
            {
                CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        // text/plain should not be compressed with custom list
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.CompressibleContentTypes = ["application/json"]); // Only compress application/json
        using var client = fixture.CreateClient();

        // Should cache without compression (not in custom list)
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        var responseContent2 = await response1.Content.ReadAsStringAsync(_ct);
        responseContent2.ShouldBe(content);
    }

    [Fact]
    public async Task Json_content_is_compressed_by_default()
    {
        var jsonContent = new string('x', 2048);
        var responseContent = Encoding.UTF8.GetBytes($"{{\"data\":\"{jsonContent}\"}}");

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseContent),
            Headers =
            {
                CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - should cache and compress
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Second request - should hit cache
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Verify only one backend call
        mockHandler.RequestCount.ShouldBe(1);
    }
}
