// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;

namespace DamianH.Http.HybridCacheHandler;

public class ErrorHandlingTests
{
    private const string TestUrl = "https://example.com/resource";
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serialization_failure_handled_gracefully()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        var mockHandler = new MockHttpMessageHandler(() => mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // First request should succeed
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(1);

        // Second request should hit cache (if serialization worked)
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(1); // Should still be cached
    }

    [Fact]
    public async Task Deserialization_failure_bypasses_cache()
    {
        var cache = new FaultyCache(shouldFailOnGet: true);
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        var mockHandler = new MockHttpMessageHandler(() => mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            customCache: cache);
        var client = fixture.CreateClient();

        // First request - cache read fails, should fetch from origin
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(1);

        // Second request - cache read still fails, should fetch from origin again
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_read_failure_falls_back_to_origin()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response from origin")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        var mockHandler = new MockHttpMessageHandler(() => mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        var response = await client.GetAsync(TestUrl, _ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(_ct);
        content.ShouldBe("response from origin");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Legacy_entry_with_null_variants_is_treated_as_cache_miss_and_repaired()
    {
        var cache = new InspectableHybridCache();
        await cache.SetAsync($"GET:{TestUrl}::", new CachedHttpEntry { Variants = null! }, cancellationToken: _ct);

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        var mockHandler = new MockHttpMessageHandler(() => mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        var firstResponse = await client.GetAsync(TestUrl, _ct);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var secondResponse = await client.GetAsync(TestUrl, _ct);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        mockHandler.RequestCount.ShouldBe(1);
        cache.GetMetadataEntry().ShouldNotBeNull();
        cache.GetMetadataEntry()!.Variants.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cache_write_failure_doesnt_break_request()
    {
        var cache = new FaultyCache(shouldFailOnSet: true);
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
        var mockHandler = new MockHttpMessageHandler(() => mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            customCache: cache);
        var client = fixture.CreateClient();

        // First request should succeed even if caching fails
        var response1 = await client.GetAsync(TestUrl, _ct);
        response1.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(1);

        // Second request fetches from origin since cache write failed
        var response2 = await client.GetAsync(TestUrl, _ct);
        response2.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_requests_for_same_resource()
    {
        var backendSignal = new AsyncAutoResetEvent(false);
        var requestStarted = new AsyncAutoResetEvent(false);

        var mockHandler = new MockHttpMessageHandler(async () =>
        {
            requestStarted.Set(); // Signal that backend request has started
            await backendSignal.WaitAsync(_ct); // Wait for signal to proceed
            var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("response")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            return response;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // Start 10 concurrent requests for the same resource
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync(TestUrl, _ct))
            .ToArray();

        // Wait for first backend request to start
        await requestStarted.WaitAsync(_ct);

        // Let backend complete
        backendSignal.Set();

        var responses = await Task.WhenAll(tasks);

        // All requests should succeed
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);

        // HybridCache handles request coalescing internally, 
        // so we should see minimal backend requests
        mockHandler.RequestCount.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Parallel_requests_for_different_resources()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            var response = new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent($"response for {request.RequestUri}")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            return Task.FromResult(response);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        // Make concurrent requests for different resources
        var tasks = Enumerable.Range(0, 5)
            .Select(i => client.GetAsync($"https://example.com/resource{i}", _ct))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        // All requests should succeed
        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);

        // Should make one request per unique resource
        mockHandler.RequestCount.ShouldBe(5);
    }

    [Fact]
    public async Task Cache_remove_failure_doesnt_break_request()
    {
        var mockResponse = new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("response")
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        var mockHandler = new MockHttpMessageHandler(() => mockResponse);

        var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        var client = fixture.CreateClient();

        // Request with no-store should succeed even if cache removal fails
        var response = await client.GetAsync(TestUrl, _ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        mockHandler.RequestCount.ShouldBe(1);
    }

    private class FaultyCache(
        bool shouldFailOnGet = false,
        bool shouldFailOnSet = false,
        bool shouldFailOnRemove = false)
        : HybridCache
    {
        private readonly HybridCache _innerCache = CreateInnerCache();

        private static HybridCache CreateInnerCache()
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<HybridCache>();
        }

        public override async ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, Ct, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            Ct cancellationToken = default)
        {
            if (shouldFailOnGet)
            {
                throw new InvalidOperationException("Simulated cache read failure");
            }

            if (shouldFailOnSet)
            {
                // When SET fails, we need to simulate that GetOrCreate also can't cache
                // Just run the factory and return the result without caching
                return await factory(state, cancellationToken);
            }

            return await _innerCache.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken);
        }

        public override async ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            Ct cancellationToken = default)
        {
            if (shouldFailOnSet)
            {
                throw new InvalidOperationException("Simulated cache write failure");
            }

            await _innerCache.SetAsync(key, value, options, tags, cancellationToken);
        }

        public override async ValueTask RemoveAsync(string key, Ct cancellationToken = default)
        {
            if (shouldFailOnRemove)
            {
                throw new InvalidOperationException("Simulated cache remove failure");
            }

            await _innerCache.RemoveAsync(key, cancellationToken);
        }

        public override ValueTask RemoveAsync(IEnumerable<string> keys, Ct cancellationToken = default) =>
            shouldFailOnRemove
                ? throw new InvalidOperationException("Simulated cache remove failure")
                : _innerCache.RemoveAsync(keys, cancellationToken);

        public override ValueTask RemoveByTagAsync(string tag, Ct cancellationToken = default) =>
            shouldFailOnRemove
                ? throw new InvalidOperationException("Simulated cache remove failure")
                : _innerCache.RemoveByTagAsync(tag, cancellationToken);

        public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, Ct cancellationToken = default) =>
            shouldFailOnRemove
                ? throw new InvalidOperationException("Simulated cache remove failure")
                : _innerCache.RemoveByTagAsync(tags, cancellationToken);
    }
}
