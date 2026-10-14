// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;

namespace DamianH.Http.HybridCacheHandler;

public class IntegrationTests
{
    private const string TestUrl = "https://example.com/resource";
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Integration_with_IDistributedCache()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Response 1"),
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromHours(1)
        };
        var handler = new TestHandler(mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(handler);
        using var client = fixture.CreateClient();

        // First request - cache miss
        var response1 = await client.GetAsync(TestUrl, _ct);
        var content1 = await response1.Content.ReadAsStringAsync(_ct);
        content1.ShouldBe("Response 1");
        handler.RequestCount.ShouldBe(1);

        // Second request - cache hit
        var response2 = await client.GetAsync(TestUrl, _ct);
        var content2 = await response2.Content.ReadAsStringAsync(_ct);
        content2.ShouldBe("Response 1");
        handler.RequestCount.ShouldBe(1); // No additional backend call
    }

    [Fact]
    public async Task Integration_with_HybridCache_directly()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Direct response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var handler = new TestHandler(mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(handler);
        using var client = fixture.CreateClient();

        var response = await client.GetAsync(TestUrl, _ct);
        var content = await response.Content.ReadAsStringAsync(_ct);

        content.ShouldBe("Direct response");
        response.IsSuccessStatusCode.ShouldBeTrue();
    }

    [Fact]
    public async Task Configure_max_cacheable_content_size()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("This content is way too large to be cached")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var handler = new TestHandler(mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(handler, options =>
        {
            options.MaxCacheableContentSize = 40; // Very small limit (first response is 42 bytes)
        });
        using var client = fixture.CreateClient();

        var response1 = await client.GetAsync(TestUrl, _ct);
        (await response1.Content.ReadAsStringAsync(_ct)).ShouldBe("This content is way too large to be cached");
        handler.RequestCount.ShouldBe(1);

        // Second request should NOT be cached due to size limit
        var newResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("New response")
        };
        newResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        handler.SetResponse(newResponse);

        var response2 = await client.GetAsync(TestUrl, _ct);
        (await response2.Content.ReadAsStringAsync(_ct)).ShouldBe("New response");
        handler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Configure_vary_headers()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Vary response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        mockResponse.Headers.Vary.Add("X-Custom-Header");
        var handler = new TestHandler(mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(handler, options =>
        {
            options.VaryHeaders = ["X-Custom-Header"];
        });
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, TestUrl);
        request1.Headers.Add("X-Custom-Header", "value1");
        var response1 = await client.SendAsync(request1, _ct);
        (await response1.Content.ReadAsStringAsync(_ct)).ShouldBe("Vary response");
        handler.RequestCount.ShouldBe(1);

        // Request with same header value should hit cache
        var request2 = new HttpRequestMessage(HttpMethod.Get, TestUrl);
        request2.Headers.Add("X-Custom-Header", "value1");
        var response2 = await client.SendAsync(request2, _ct);
        (await response2.Content.ReadAsStringAsync(_ct)).ShouldBe("Vary response");
        handler.RequestCount.ShouldBe(1);
    }

    private class TestHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private HttpResponseMessage _response = response;
        public int RequestCount { get; private set; }

        public void SetResponse(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            Ct cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_response);
        }
    }
}

