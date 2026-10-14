// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;

namespace DamianH.Http.HybridCacheHandler;

public class NonCacheableTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task POST_requests_not_cached_by_default()
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

        // First POST request
        await client.PostAsync("https://example.com/resource", new StringContent("data"), _ct);

        // Second POST request
        await client.PostAsync("https://example.com/resource", new StringContent("data"), _ct);

        mockHandler.RequestCount.ShouldBe(2); // POST not cached
    }

    [Fact]
    public async Task PUT_DELETE_not_cached()
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

        // PUT
        await client.PutAsync("https://example.com/resource", new StringContent("data"), _ct);
        await client.PutAsync("https://example.com/resource", new StringContent("data"), _ct);

        // DELETE
        await client.DeleteAsync("https://example.com/resource", _ct);
        await client.DeleteAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(4); // None cached
    }

    [Fact]
    public async Task GET_cached()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1); // GET cached
    }

    [Fact]
    public async Task Pragma_no_cache_does_not_force_revalidation()
    {
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        mockResponse.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.IncludeDiagnosticHeaders = true);
        using var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request with Pragma: no-cache - should still serve cached response
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Pragma", "no-cache");
        var response2 = await client.SendAsync(request, _ct);

        // Should have made only the initial origin request
        mockHandler.RequestCount.ShouldBe(1);

        // Response body should be served from cache
        var body = await response2.Content.ReadAsStringAsync(_ct);
        body.ShouldBe("response");

        // Diagnostic should indicate fresh cache hit
        response2.Headers.GetValues("X-Cache-Diagnostic").First().ShouldBe("HIT-FRESH");
    }

    [Fact]
    public async Task Status_200_OK_cacheable()
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

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Status_203_204_206_cacheable_with_explicit_freshness()
    {
        var statusCodes = new[]
        {
            HttpStatusCode.NonAuthoritativeInformation, // 203
            HttpStatusCode.NoContent, // 204
            HttpStatusCode.PartialContent // 206
        };

        foreach (var statusCode in statusCodes)
        {
            var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent("response"),
                Headers = { { "Cache-Control", "max-age=3600" } }
            });
            await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
            using var client = fixture.CreateClient();

            await client.GetAsync($"https://example.com/{statusCode}", _ct);
            await client.GetAsync($"https://example.com/{statusCode}", _ct);

            mockHandler.RequestCount.ShouldBe(1, $"Status {statusCode} should be cacheable with explicit headers");
        }
    }

    [Fact]
    public async Task Status_300_301_308_404_410_cacheable()
    {
        var statusCodes = new[]
        {
            HttpStatusCode.MultipleChoices, // 300
            HttpStatusCode.MovedPermanently, // 301
            HttpStatusCode.PermanentRedirect, // 308
            HttpStatusCode.NotFound, // 404
            HttpStatusCode.Gone // 410
        };

        foreach (var statusCode in statusCodes)
        {
            var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent("response"),
                Headers = { { "Cache-Control", "max-age=3600" } }
            });
            await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
            using var client = fixture.CreateClient();

            await client.GetAsync($"https://example.com/{statusCode}", _ct);
            await client.GetAsync($"https://example.com/{statusCode}", _ct);

            mockHandler.RequestCount.ShouldBe(1, $"Status {statusCode} should be cacheable");
        }
    }

    [Fact]
    public async Task Other_status_codes_not_cached_without_explicit_headers()
    {
        // 500 Internal Server Error - not cacheable by default
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.InternalServerError,
            Content = new StringContent("error")
            // No Cache-Control header
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2); // Not cached without explicit headers
    }

    [Fact]
    public async Task Heuristic_freshness_not_applied_to_non_heuristic_status_without_public()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.Created, // 201
            Content = new StringContent("created")
            {
                Headers = { LastModified = now.AddDays(-1) }
            },
            Headers = { Date = now }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Heuristic_freshness_applied_to_nonstandard_status_when_public()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var response = new HttpResponseMessage((HttpStatusCode)599)
        {
            Content = new StringContent("unknown")
            {
                Headers = { LastModified = now.AddDays(-1) }
            },
            Headers = { Date = now }
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { Public = true };
        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Authorization_header_requires_Cache_Control_public()
    {
        // Without public directive
        var mockResponse1 = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse1.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        var mockHandler1 = new MockHttpMessageHandler(mockResponse1);
        await using var fixture1 = new HttpHybridCacheHandlerFixture(mockHandler1);
        using var client1 = fixture1.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request1.Headers.Add("Authorization", "Bearer token123");
        await client1.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request2.Headers.Add("Authorization", "Bearer token123");
        await client1.SendAsync(request2, _ct);

        mockHandler1.RequestCount.ShouldBe(2); // Not cached without public

        // With public directive
        var mockResponse2 = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse2.Headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromHours(1) };
        var mockHandler2 = new MockHttpMessageHandler(mockResponse2);
        await using var fixture2 = new HttpHybridCacheHandlerFixture(mockHandler2);
        using var client2 = fixture2.CreateClient();

        var request3 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource2");
        request3.Headers.Add("Authorization", "Bearer token123");
        await client2.SendAsync(request3, _ct);

        var request4 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource2");
        request4.Headers.Add("Authorization", "Bearer token123");
        await client2.SendAsync(request4, _ct);

        mockHandler2.RequestCount.ShouldBe(1); // Cached with public
    }
}
