// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.Http.HybridCacheHandler;

public class ResponseDirectivesTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Response_with_no_store_not_cached()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "no-store" } }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request - should not be cached
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2); // Both requests hit origin
    }

    [Fact]
    public async Task Existing_cache_entry_removed_if_response_has_no_store()
    {
        var responseCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            responseCount++;
            if (responseCount == 1)
            {
                // First response is cacheable with ETag for validation
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("first"),
                    Headers =
                    {
                        { "Cache-Control", "max-age=1" },
                        { "ETag", "\"v1\"" }
                    }
                };
            }
            else
            {
                // Subsequent responses have no-store (invalidate cache)
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("second"),
                    Headers = { { "Cache-Control", "no-store" } }
                };
            }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request - cache it
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Advance time to make cache stale (requires revalidation)
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - cache is stale, revalidates and gets no-store response (cache should be invalidated)
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(2);

        // Third request - cache was invalidated, should fetch fresh
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task Response_with_no_cache_stored_but_requires_validation()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "no-cache" },
                { "ETag", "\"123\"" }
            }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request - should trigger validation
        await client.GetAsync("https://example.com/resource", _ct);

        // For now, no-cache responses aren't cached (will implement in Phase 5)
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Subsequent_request_triggers_conditional_request_for_no_cache()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "no-cache" },
                { "ETag", "\"123\"" }
            }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Second request - should send conditional request
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(2);
        mockHandler.LastRequest.ShouldNotBeNull();
        mockHandler.LastRequest.Headers.IfNoneMatch.ShouldContain(etag => etag.Tag == "\"123\"");
    }

    [Fact]
    public async Task Response_fresh_for_max_age_seconds()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request within max-age - should be cached
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Response_stale_after_max_age_seconds()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=1" } } // 1 second
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Wait for response to become stale
        fixture.AdvanceTime(TimeSpan.FromMilliseconds(1100)); // Wait 1.1 seconds

        // Second request - should fetch fresh response
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Response_with_max_age_zero_is_stored_for_only_if_cached_requests()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=0" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "only-if-cached");
        var response = await client.SendAsync(request, _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBe("response");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Private_responses_are_cached_in_client_cache()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "private, max-age=3600" } }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request - should be cached
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Public_responses_are_cached()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "public, max-age=3600" } }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request - should be cached
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Authenticated_requests_respect_private_directive()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "private, max-age=3600" } }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // Request with Authorization header
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request1.Headers.Add("Authorization", "Bearer token");
        await client.SendAsync(request1, _ct);

        // Second request with Authorization
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request2.Headers.Add("Authorization", "Bearer token");
        await client.SendAsync(request2, _ct);

        // Private responses with auth should still be cached in client cache
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Stale_response_with_must_revalidate_forces_validation()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=1, must-revalidate" },
                { "ETag", "\"123\"" }
            }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Wait for response to become stale
        fixture.AdvanceTime(TimeSpan.FromMilliseconds(1100));

        // Second request - must revalidate
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Cannot_serve_stale_response_even_if_max_stale_requested()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=1, must-revalidate" },
                { "ETag", "\"123\"" }
            }
        });
        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Wait for response to become stale
        fixture.AdvanceTime(TimeSpan.FromMilliseconds(1100));

        // Second request with max-stale - must still revalidate
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "max-stale=3600");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }
}
