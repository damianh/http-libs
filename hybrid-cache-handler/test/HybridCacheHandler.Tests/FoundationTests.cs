// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;

namespace DamianH.Http.HybridCacheHandler;

public class FoundationTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;


    [Fact]
    public async Task Handler_passes_through_to_inner_handler_when_no_cache()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("test response")
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var response = await client.GetAsync("https://example.com/test", _ct);
        var content = await response.Content.ReadAsStringAsync(_ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        content.ShouldBe("test response");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Second_identical_GET_request_returns_cached_response()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("cached content")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - should go to origin
        var response1 = await client.GetAsync("https://example.com/resource", _ct);
        var content1 = await response1.Content.ReadAsStringAsync(_ct);

        // Second request - should come from cache
        var response2 = await client.GetAsync("https://example.com/resource", _ct);
        var content2 = await response2.Content.ReadAsStringAsync(_ct);

        content1.ShouldBe("cached content");
        content2.ShouldBe("cached content");
        mockHandler.RequestCount.ShouldBe(1); // Only one request to origin
    }

    [Fact]
    public async Task Cache_key_includes_method_and_URI()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1); // Cached
    }

    [Fact]
    public async Task Response_body_matches_original()
    {
        const string OriginalContent = "original response body";
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(OriginalContent)
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var response1 = await client.GetAsync("https://example.com/test", _ct);
        var content1 = await response1.Content.ReadAsStringAsync(_ct);

        var response2 = await client.GetAsync("https://example.com/test", _ct);
        var content2 = await response2.Content.ReadAsStringAsync(_ct);

        content1.ShouldBe(OriginalContent);
        content2.ShouldBe(OriginalContent);
    }

    [Fact]
    public async Task Different_URIs_result_in_different_cache_entries()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource1", _ct);
        await client.GetAsync("https://example.com/resource2", _ct);

        mockHandler.RequestCount.ShouldBe(2); // Different URIs, no cache hit
    }

    [Fact]
    public async Task Different_methods_do_not_share_cache()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler = new MockHttpMessageHandler(mockResponse);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/resource"), _ct);

        mockHandler.RequestCount.ShouldBe(2); // GET and HEAD don't share cache
    }
}
