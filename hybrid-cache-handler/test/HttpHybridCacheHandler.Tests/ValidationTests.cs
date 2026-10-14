// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Globalization;

namespace DamianH.Http.HybridCacheHandler;

public class ValidationTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cached_response_with_ETag_triggers_If_None_Match()
    {
        var requestCount = 0;
        HttpRequestMessage? lastRequest = null;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // First request - return response with ETag
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("original content")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"123abc\"");
                return Task.FromResult(response);
            }

            // Second request - capture for assertion
            lastRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request - populate cache
        await client.GetAsync("https://example.com/resource", _ct);

        // Make response stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - should trigger validation with If-None-Match
        _ = await client.GetAsync("https://example.com/resource", _ct);

        requestCount.ShouldBe(2);
        lastRequest.ShouldNotBeNull();
        lastRequest.Headers.IfNoneMatch.ShouldContain(etag => etag.Tag == "\"123abc\"");
    }

    [Fact]
    public async Task Response_304_updates_cache_metadata()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("content")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"123\"");
                return response;
            }

            // 304 with updated freshness
            var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"123\"");
            return notModifiedResponse;
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance past initial freshness
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - gets 304, updates metadata
        await client.GetAsync("https://example.com/resource", _ct);

        // Advance time within new freshness window
        fixture.AdvanceTime(TimeSpan.FromMinutes(30));

        // Third request - should use cache with updated metadata
        await client.GetAsync("https://example.com/resource", _ct);

        requestCount.ShouldBe(2); // Only 2 requests, third uses refreshed cache
    }

    [Fact]
    public async Task Response_304_updates_stored_response_and_content_headers()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("cached-body")
                };
                response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
                response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                response.Headers.TryAddWithoutValidation("Test-Header", "initial");
                response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
                return response;
            }

            var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new ByteArrayContent([])
            };
            notModifiedResponse.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            notModifiedResponse.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
            notModifiedResponse.Headers.TryAddWithoutValidation("Test-Header", "updated");
            notModifiedResponse.Content.Headers.TryAddWithoutValidation("Content-Encoding", "br");
            return notModifiedResponse;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/header-update", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var revalidatedResponse = await client.GetAsync("https://example.com/header-update", _ct);
        var cachedResponse = await client.GetAsync("https://example.com/header-update", _ct);
        var revalidatedBody = await revalidatedResponse.Content.ReadAsStringAsync(_ct);
        var cachedBody = await cachedResponse.Content.ReadAsStringAsync(_ct);

        requestCount.ShouldBe(2);
        revalidatedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        revalidatedBody.ShouldBe("cached-body");
        revalidatedResponse.Headers.TryGetValues("Test-Header", out var revalidatedHeaderValues).ShouldBeTrue();
        revalidatedHeaderValues.ShouldBe(["updated"]);
        revalidatedResponse.Content.Headers.ContentEncoding.ShouldBe(["br"]);

        cachedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        cachedBody.ShouldBe("cached-body");
        cachedResponse.Headers.TryGetValues("Test-Header", out var cachedHeaderValues).ShouldBeTrue();
        cachedHeaderValues.ShouldBe(["updated"]);
        cachedResponse.Content.Headers.ContentEncoding.ShouldBe(["br"]);
    }

    [Fact]
    public async Task Response_304_updates_stale_if_error_window()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("cached")
                };
                response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, stale-if-error=1");
                response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                return response;
            }

            if (requestCount == 2)
            {
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModifiedResponse.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, stale-if-error = 10");
                notModifiedResponse.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                return notModifiedResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("origin-down")
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/stale-window-update", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetAsync("https://example.com/stale-window-update", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(5));

        var response = await client.GetAsync("https://example.com/stale-window-update", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        requestCount.ShouldBe(3);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBe("cached");
    }

    [Fact]
    public async Task Response_304_uses_raw_weak_etag_for_next_validation()
    {
        var requestCount = 0;
        HttpRequestMessage? validationRequest = null;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("content")
                };
                response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
                response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                return Task.FromResult(response);
            }

            if (requestCount == 2)
            {
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModifiedResponse.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
                notModifiedResponse.Headers.TryAddWithoutValidation("ETag", "w/\"v2\"");
                return Task.FromResult(notModifiedResponse);
            }

            validationRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/etag-update", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetAsync("https://example.com/etag-update", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetAsync("https://example.com/etag-update", _ct);

        requestCount.ShouldBe(3);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-None-Match", out var values).ShouldBeTrue();
        values!.Single().ShouldStartWith("W/", Case.Insensitive);
        values.Single().ShouldContain("\"v2\"");
    }

    [Fact]
    public async Task Response_304_without_age_preserves_cached_age()
    {
        var requestCount = 0;
        var initialDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("content")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.Date = initialDate;
                response.Headers.TryAddWithoutValidation("Age", "30");
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return response;
            }

            var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new ByteArrayContent([])
            };
            notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) };
            notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return notModifiedResponse;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(initialDate);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var revalidatedResponse = await client.GetAsync("https://example.com/resource", _ct);

        requestCount.ShouldBe(2);
        revalidatedResponse.Headers.TryGetValues("Age", out var ageValues).ShouldBeTrue();
        int.Parse(ageValues!.Single()).ShouldBeGreaterThanOrEqualTo(30);
    }

    [Fact]
    public async Task Response_304_does_not_reintroduce_qualified_no_cache_headers()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("content")
                };
                response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, no-cache=\"Set-Cookie\"");
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return response;
            }

            if (requestCount == 2)
            {
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified)
                {
                    Content = new ByteArrayContent([])
                };
                notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                notModifiedResponse.Headers.TryAddWithoutValidation("Set-Cookie", "session=from-304");
                return notModifiedResponse;
            }

            var updatedNotModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new ByteArrayContent([])
            };
            updatedNotModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(2) };
            updatedNotModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return updatedNotModifiedResponse;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        var secondResponse = await client.GetAsync("https://example.com/resource", _ct);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        var thirdResponse = await client.GetAsync("https://example.com/resource", _ct);

        secondResponse.Headers.Contains("Set-Cookie").ShouldBeFalse();
        thirdResponse.Headers.Contains("Set-Cookie").ShouldBeFalse();
        requestCount.ShouldBe(3);
    }

    [Fact]
    public async Task Response_304_returns_cached_body()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("original body")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                return response;
            }
            else
            {
                // 304 has no body
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                return notModifiedResponse;
            }
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        var response1 = await client.GetAsync("https://example.com/resource", _ct);
        var body1 = await response1.Content.ReadAsStringAsync(_ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - gets 304 but returns cached body
        var response2 = await client.GetAsync("https://example.com/resource", _ct);
        var body2 = await response2.Content.ReadAsStringAsync(_ct);

        body1.ShouldBe("original body");
        body2.ShouldBe("original body"); // Body from cache, not empty 304
        response2.StatusCode.ShouldBe(HttpStatusCode.OK); // Presented as 200 to client
    }

    [Fact]
    public async Task Response_304_for_no_cache_request_removes_variant_when_cached_body_missing()
    {
        var cache = new InspectableHybridCache();
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("original body")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                return response;
            }

            var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
            return notModifiedResponse;
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        cache.RemoveContentForVariant(_ => true).ShouldBeTrue();

        var noCacheRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        noCacheRequest.Headers.Add("Cache-Control", "no-cache");
        var response = await client.SendAsync(noCacheRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        cache.GetMetadataEntry().ShouldBeNull();
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Response_304_for_stale_entry_removes_variant_when_cached_body_missing()
    {
        var cache = new InspectableHybridCache();
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("original body")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                return response;
            }

            var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
            return notModifiedResponse;
        });
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        cache.RemoveContentForVariant(_ => true).ShouldBeTrue();
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var response = await client.GetAsync("https://example.com/resource", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        cache.GetMetadataEntry().ShouldBeNull();
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Response_304_replace_trims_oversized_legacy_variant_entry()
    {
        var cache = new InspectableHybridCache();
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("original body")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                response.Headers.Add("Vary", "Foo");
                return response;
            }

            var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            notModifiedResponse.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
            return notModifiedResponse;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        var initialRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        initialRequest.Headers.Add("Foo", "0");
        await client.SendAsync(initialRequest, _ct);

        var metadataEntry = cache.GetMetadataEntry();
        metadataEntry.ShouldNotBeNull();
        SeedLegacyVariants(metadataEntry, 9);
        metadataEntry.Variants.Count.ShouldBe(10);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var revalidationRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        revalidationRequest.Headers.Add("Foo", "0");
        var response = await client.SendAsync(revalidationRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedEntry = cache.GetMetadataEntry();
        updatedEntry.ShouldNotBeNull();
        updatedEntry.Variants.Count.ShouldBe(8);
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Revalidation_no_store_remove_trims_oversized_legacy_variant_entry()
    {
        var cache = new InspectableHybridCache();
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("original body")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                response.Headers.Add("Vary", "Foo");
                return response;
            }

            var noStoreResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("uncached replacement")
            };
            noStoreResponse.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return noStoreResponse;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        var initialRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        initialRequest.Headers.Add("Foo", "0");
        await client.SendAsync(initialRequest, _ct);

        var metadataEntry = cache.GetMetadataEntry();
        metadataEntry.ShouldNotBeNull();
        SeedLegacyVariants(metadataEntry, 9);
        metadataEntry.Variants.Count.ShouldBe(10);

        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var revalidationRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        revalidationRequest.Headers.Add("Foo", "0");
        var response = await client.SendAsync(revalidationRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updatedEntry = cache.GetMetadataEntry();
        updatedEntry.ShouldNotBeNull();
        updatedEntry.Variants.Count.ShouldBe(8);
        updatedEntry.Variants.Exists(HasFooZero).ShouldBeFalse();
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Strong_vs_weak_ETag_comparison()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("content")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
        mockResponse.Headers.ETag = new EntityTagHeaderValue("\"weak-tag\"", true); // Weak ETag
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - should handle weak ETag validation
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2); // Validation attempted
    }

    [Fact]
    public async Task Cached_response_triggers_If_Modified_Since()
    {
        var requestCount = 0;
        HttpRequestMessage? lastRequest = null;
        var fixture = new HttpHybridCacheHandlerFixture();
        var lastModified = fixture.TimeProvider.GetUtcNow().AddDays(-1);
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("content")
                    {
                        Headers = { LastModified = lastModified }
                    }
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                return Task.FromResult(response);
            }
            else
            {
                lastRequest = req;
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                return Task.FromResult(notModifiedResponse);
            }
        });

        fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - should trigger If-Modified-Since
        await client.GetAsync("https://example.com/resource", _ct);

        requestCount.ShouldBe(2);
        lastRequest.ShouldNotBeNull();
        lastRequest.Headers.IfModifiedSince.ShouldBe(lastModified);
    }

    [Fact]
    public async Task Response_304_updates_cache_entry_date()
    {
        var requestCount = 0;
        var fixture = new HttpHybridCacheHandlerFixture();
        var lastModified = fixture.TimeProvider.GetUtcNow().AddDays(-1);
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("content")
                    {
                        Headers = { LastModified = lastModified }
                    }
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                return response;
            }
            else
            {
                var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified);
                notModifiedResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                return notModifiedResponse;
            }
        });

        fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Validation request
        await client.GetAsync("https://example.com/resource", _ct);

        // Should now be fresh for extended period
        fixture.AdvanceTime(TimeSpan.FromMinutes(30));
        await client.GetAsync("https://example.com/resource", _ct);

        requestCount.ShouldBe(2); // Third request uses refreshed cache
    }

    [Fact]
    public async Task Last_Modified_fallback_when_no_ETag()
    {
        var fixture = new HttpHybridCacheHandlerFixture();
        var lastModified = fixture.TimeProvider.GetUtcNow().AddDays(-1);
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("content")
            {
                Headers = { LastModified = lastModified }
            }
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
        // No ETag - should use Last-Modified
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - should attempt validation with Last-Modified
        await client.GetAsync("https://example.com/resource", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Response_200_replaces_cached_entry()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("old content")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"old\"");
                return response;
            }
            else
            {
                // Resource changed - return 200 with new content
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("new content")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"new\"");
                return response;
            }
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        var response1 = await client.GetAsync("https://example.com/resource", _ct);
        var body1 = await response1.Content.ReadAsStringAsync(_ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - resource changed, gets 200 with new content
        var response2 = await client.GetAsync("https://example.com/resource", _ct);
        var body2 = await response2.Content.ReadAsStringAsync(_ct);

        body1.ShouldBe("old content");
        body2.ShouldBe("new content");

        // Third request - should use new cached content
        var response3 = await client.GetAsync("https://example.com/resource", _ct);
        var body3 = await response3.Content.ReadAsStringAsync(_ct);

        body3.ShouldBe("new content");
        requestCount.ShouldBe(2); // Third uses updated cache
    }

    [Fact]
    public async Task Other_status_codes_handled_correctly()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var response = new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("content")
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                response.Headers.ETag = new EntityTagHeaderValue("\"abc\"");
                return response;
            }
            else
            {
                // Validation returns 404 - resource deleted
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("Not Found")
                };
            }
        });

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // First request
        await client.GetAsync("https://example.com/resource", _ct);

        // Make stale
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        // Second request - validation returns 404
        var response = await client.GetAsync("https://example.com/resource", _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        requestCount.ShouldBe(2);
    }

    private static void SeedLegacyVariants(CachedHttpEntry entry, int legacyVariantCount)
    {
        var currentVariant = entry.Variants[0];
        for (var i = 1; i <= legacyVariantCount; i++)
        {
            entry.Variants.Add(new CachedHttpMetadata
            {
                StatusCode = currentVariant.StatusCode,
                ContentKey = $"legacy-content-{i}",
                ContentLength = currentVariant.ContentLength,
                Headers = new Dictionary<string, string[]>(currentVariant.Headers, StringComparer.OrdinalIgnoreCase),
                ContentHeaders = new Dictionary<string, string[]>(currentVariant.ContentHeaders, StringComparer.OrdinalIgnoreCase),
                CachedAt = currentVariant.CachedAt - TimeSpan.FromMinutes(i),
                MaxAge = currentVariant.MaxAge,
                ETag = currentVariant.ETag,
                LastModified = currentVariant.LastModified,
                Expires = currentVariant.Expires,
                Date = currentVariant.Date,
                Age = currentVariant.Age,
                VaryHeaders = ["Foo"],
                VaryHeaderValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Foo"] = i.ToString(CultureInfo.InvariantCulture)
                },
                StaleWhileRevalidate = currentVariant.StaleWhileRevalidate,
                StaleIfError = currentVariant.StaleIfError,
                MustRevalidate = currentVariant.MustRevalidate,
                NoCache = currentVariant.NoCache,
                IsCompressed = currentVariant.IsCompressed
            });
        }
    }

    private static bool HasFooZero(CachedHttpMetadata variant) =>
        variant.VaryHeaderValues != null
        && variant.VaryHeaderValues.TryGetValue("Foo", out var foo)
        && string.Equals(foo, "0", StringComparison.Ordinal);

}
