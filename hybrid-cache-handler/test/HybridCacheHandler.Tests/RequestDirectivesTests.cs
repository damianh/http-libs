// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.Extensions.Caching.Hybrid;

namespace DamianH.Http.HybridCacheHandler;

public class RequestDirectivesTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_with_no_store_bypasses_cache_read()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request with no-store - should bypass cache
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "no-store");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(2); // Both requests hit origin
    }

    [Fact]
    public async Task Response_not_stored_when_request_has_no_store()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // Request with no-store
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request1.Headers.Add("Cache-Control", "no-store");
        await client.SendAsync(request1, _ct);

        // Second request without no-store - should not find cached entry
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2); // Both requests hit origin
    }

    [Fact]
    public async Task Request_with_no_cache_forces_validation()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "ETag", "\"123\"" }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request with no-cache - should force validation
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "no-cache");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(2); // Second request triggers validation
        mockHandler.LastRequest.ShouldNotBeNull();
        mockHandler.LastRequest.Headers.IfNoneMatch.ShouldContain(etag => etag.Tag == "\"123\"");
    }

    [Fact]
    public async Task No_cache_sends_conditional_request_even_if_fresh()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "ETag", "\"123\"" }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - cache fresh response
        await client.GetAsync("https://example.com/resource", _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Second request with no-cache on fresh response
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "no-cache");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(2); // Forces revalidation despite freshness
    }

    [Fact]
    public async Task Request_max_age_zero_forces_validation()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "ETag", "\"123\"" }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request with max-age=0 - should force validation
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "max-age=0");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Request_max_age_accepts_fresh_responses_within_age()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache with 1 hour freshness
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request with max-age=7200 (2 hours) - should accept cached
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "max-age=7200");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(1); // Cached response used
    }

    [Fact]
    public async Task Request_max_age_rejects_response_older_than_requested_limit()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers =
            {
                { "Cache-Control", "max-age=3600" },
                { "Age", "1800" }
            }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache with a response already 30 minutes old
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request asks for max-age=1 second; cached response should be rejected
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "max-age=1");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Request_max_stale_allows_serving_stale_response_within_limit()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=1" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Make response stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // max-stale should allow using stale cache entry
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "max-stale=1000");
        await client.SendAsync(request, _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Only_if_cached_returns_cached_response_if_available()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("cached response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Second request with only-if-cached
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "only-if-cached");
        var response = await client.SendAsync(request, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(_ct);
        content.ShouldBe("cached response");
        mockHandler.RequestCount.ShouldBe(1); // No request to origin
    }

    [Fact]
    public async Task Only_if_cached_with_missing_variant_body_removes_orphaned_variant()
    {
        var cache = new InspectableHybridCache();
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            var foo = request.Headers.TryGetValues("Foo", out var values)
                ? string.Join(string.Empty, values)
                : "missing";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"response_{foo}")
            };
            response.Headers.Add("Cache-Control", "max-age=3600");
            response.Headers.Add("Vary", "Foo");
            return Task.FromResult(response);
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        var shortRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        shortRequest.Headers.Add("Foo", "short");
        await client.SendAsync(shortRequest, _ct);

        var longRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        longRequest.Headers.Add("Foo", "long");
        await client.SendAsync(longRequest, _ct);

        cache.RemoveContentForVariant(v =>
            v.VaryHeaderValues != null
            && v.VaryHeaderValues.TryGetValue("Foo", out var value)
            && value == "short").ShouldBeTrue();

        var shortOnlyIfCached = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        shortOnlyIfCached.Headers.Add("Foo", "short");
        shortOnlyIfCached.Headers.Add("Cache-Control", "only-if-cached");
        var shortResponse = await client.SendAsync(shortOnlyIfCached, _ct);

        shortResponse.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        mockHandler.RequestCount.ShouldBe(2);

        var metadataEntry = cache.GetMetadataEntry();
        metadataEntry.ShouldNotBeNull();
        metadataEntry.Variants.Count.ShouldBe(1);
        metadataEntry.Variants[0].VaryHeaderValues?["Foo"].ShouldBe("long");

        var longOnlyIfCached = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        longOnlyIfCached.Headers.Add("Foo", "long");
        longOnlyIfCached.Headers.Add("Cache-Control", "only-if-cached");
        var longResponse = await client.SendAsync(longOnlyIfCached, _ct);

        longResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await longResponse.Content.ReadAsStringAsync(_ct)).ShouldBe("response_long");
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Only_if_cached_returns_504_when_matching_variant_is_past_effective_lifetime()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            var foo = request.Headers.TryGetValues("Foo", out var values)
                ? string.Join(string.Empty, values)
                : "missing";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"response_{foo}")
            };
            response.Headers.Add("Cache-Control", foo == "short" ? "max-age=1" : "max-age=3600");
            response.Headers.Add("Vary", "Foo");
            return Task.FromResult(response);
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var shortRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        shortRequest.Headers.Add("Foo", "short");
        await client.SendAsync(shortRequest, _ct);

        var longRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        longRequest.Headers.Add("Foo", "long");
        await client.SendAsync(longRequest, _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var staleOnlyIfCached = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        staleOnlyIfCached.Headers.Add("Foo", "short");
        staleOnlyIfCached.Headers.Add("Cache-Control", "only-if-cached");
        var staleResponse = await client.SendAsync(staleOnlyIfCached, _ct);

        staleResponse.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        mockHandler.RequestCount.ShouldBe(2);

        var freshOnlyIfCached = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        freshOnlyIfCached.Headers.Add("Foo", "long");
        freshOnlyIfCached.Headers.Add("Cache-Control", "only-if-cached");
        var freshResponse = await client.SendAsync(freshOnlyIfCached, _ct);

        freshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await freshResponse.Content.ReadAsStringAsync(_ct)).ShouldBe("response_long");
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Only_if_cached_falls_back_to_usable_Accept_Language_variant()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            var acceptLanguage = request.Headers.TryGetValues("Accept-Language", out var values)
                ? string.Join(string.Empty, values)
                : string.Empty;

            var isEnglishVariant = acceptLanguage == "en-US";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isEnglishVariant ? "response_en" : "response_fr")
            };
            response.Headers.Add("Cache-Control", isEnglishVariant ? "max-age=1" : "max-age=3600");
            response.Headers.Add("Vary", "Accept-Language");
            response.Content.Headers.Add("Content-Language", isEnglishVariant ? "en" : "fr");
            return Task.FromResult(response);
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var englishRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        englishRequest.Headers.Add("Accept-Language", "en-US");
        await client.SendAsync(englishRequest, _ct);

        var frenchRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        frenchRequest.Headers.Add("Accept-Language", "fr-FR");
        await client.SendAsync(frenchRequest, _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var onlyIfCachedRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        onlyIfCachedRequest.Headers.Add("Accept-Language", "en, fr;q=0.9");
        onlyIfCachedRequest.Headers.Add("Cache-Control", "only-if-cached");
        var onlyIfCachedResponse = await client.SendAsync(onlyIfCachedRequest, _ct);

        onlyIfCachedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await onlyIfCachedResponse.Content.ReadAsStringAsync(_ct)).ShouldBe("response_fr");
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Only_if_cached_returns_504_if_not_in_cache()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // Request with only-if-cached when cache is empty
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.Add("Cache-Control", "only-if-cached");
        var response = await client.SendAsync(request, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout); // 504
        mockHandler.RequestCount.ShouldBe(0); // No request to origin
    }

    [Fact]
    public async Task Head_only_if_cached_returns_cached_get_without_origin_request()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("cached response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request - populate GET cache
        await client.GetAsync("https://example.com/head-resource", _ct);

        // HEAD request with only-if-cached must not hit origin
        var request = new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-resource");
        request.Headers.Add("Cache-Control", "only-if-cached");
        var response = await client.SendAsync(request, _ct);
        var content = await response.Content.ReadAsStringAsync(_ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        content.ShouldBeEmpty();
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Head_only_if_cached_returns_504_if_not_in_cache()
    {
        var mockHandler = new MockHttpMessageHandler(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "max-age=3600" } }
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-resource");
        request.Headers.Add("Cache-Control", "only-if-cached");
        var response = await client.SendAsync(request, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        mockHandler.RequestCount.ShouldBe(0);
    }
}
