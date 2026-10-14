// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace DamianH.Http.HybridCacheHandler;

public class InvalidationTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unsafe_method_with_success_response_invalidates_target_uri()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(CreateCacheableResponse("value"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct); // MISS
        await client.GetAsync("https://example.com/resource", _ct); // HIT
        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct);
        await client.GetAsync("https://example.com/resource", _ct); // MISS after invalidation

        mockHandler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task Unsafe_method_invalidates_all_vary_variants_for_uri()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var response = CreateCacheableResponse("value");
            response.Headers.Vary.Add("Accept");
            return Task.FromResult(response);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.VaryHeaders = ["Accept"]);
        using var client = fixture.CreateClient();

        var jsonRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        jsonRequest.Headers.Add("Accept", "application/json");
        await client.SendAsync(jsonRequest, _ct); // MISS variant 1

        var xmlRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        xmlRequest.Headers.Add("Accept", "application/xml");
        await client.SendAsync(xmlRequest, _ct); // MISS variant 2

        var jsonHitRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        jsonHitRequest.Headers.Add("Accept", "application/json");
        await client.SendAsync(jsonHitRequest, _ct); // HIT

        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct);

        var jsonAfterInvalidation = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        jsonAfterInvalidation.Headers.Add("Accept", "application/json");
        await client.SendAsync(jsonAfterInvalidation, _ct); // MISS variant 1

        var xmlAfterInvalidation = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        xmlAfterInvalidation.Headers.Add("Accept", "application/xml");
        await client.SendAsync(xmlAfterInvalidation, _ct); // MISS variant 2

        mockHandler.RequestCount.ShouldBe(5);
    }

    [Fact]
    public async Task Same_origin_location_uri_is_invalidated()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.NoContent);
                response.Headers.Location = new Uri("/other", UriKind.Relative);
                return Task.FromResult(response);
            }

            return Task.FromResult(CreateCacheableResponse(request.RequestUri!.AbsolutePath));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/other", _ct); // MISS
        await client.GetAsync("https://example.com/other", _ct); // HIT
        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct);
        await client.GetAsync("https://example.com/other", _ct); // MISS after Location invalidation

        mockHandler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task Same_origin_content_location_uri_is_invalidated()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.NoContent)
                {
                    Content = new StringContent("done")
                };
                response.Content.Headers.ContentLocation = new Uri("/other", UriKind.Relative);
                return Task.FromResult(response);
            }

            return Task.FromResult(CreateCacheableResponse(request.RequestUri!.AbsolutePath));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/other", _ct); // MISS
        await client.GetAsync("https://example.com/other", _ct); // HIT
        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct);
        await client.GetAsync("https://example.com/other", _ct); // MISS after Content-Location invalidation

        mockHandler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task Cross_origin_location_is_not_invalidated()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.NoContent);
                response.Headers.Location = new Uri("https://other.example/other", UriKind.Absolute);
                return Task.FromResult(response);
            }

            return Task.FromResult(CreateCacheableResponse(request.RequestUri!.AbsolutePath));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/other", _ct); // MISS
        await client.GetAsync("https://example.com/other", _ct); // HIT
        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct);
        await client.GetAsync("https://example.com/other", _ct); // Still HIT

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Unsafe_method_with_error_response_does_not_invalidate()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(CreateCacheableResponse("value"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct); // MISS
        await client.GetAsync("https://example.com/resource", _ct); // HIT
        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct);
        await client.GetAsync("https://example.com/resource", _ct); // Still HIT

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Safe_method_does_not_invalidate()
    {
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Options)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(CreateCacheableResponse("value"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct); // MISS
        await client.GetAsync("https://example.com/resource", _ct); // HIT
        using var optionsRequest = new HttpRequestMessage(HttpMethod.Options, "https://example.com/resource");
        await client.SendAsync(optionsRequest, _ct);
        await client.GetAsync("https://example.com/resource", _ct); // Still HIT

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Cached_content_entries_are_tagged_with_request_uri_for_invalidation()
    {
        var cache = new RecordingHybridCache();
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(CreateCacheableResponse("value"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct); // cache content + metadata
        await client.PostAsync("https://example.com/resource", new StringContent("x"), _ct); // invalidate by tag

        cache.ContentEntryTags.ShouldContain(tags =>
            tags.Contains("httpcache:uri:https://example.com/resource"));
        cache.RemoveByTagCalls.ShouldContain("httpcache:uri:https://example.com/resource");
    }

    private static HttpResponseMessage CreateCacheableResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };
        response.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromHours(1)
        };
        return response;
    }

    private sealed class RecordingHybridCache : HybridCache
    {
        private readonly HybridCache _inner = CreateInnerCache();

        public List<string[]> ContentEntryTags { get; } = [];
        public List<string> RemoveByTagCalls { get; } = [];

        private static HybridCache CreateInnerCache()
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            return services.BuildServiceProvider().GetRequiredService<HybridCache>();
        }

        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, Ct, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            Ct cancellationToken = default) =>
            _inner.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken);

        public override async ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            Ct cancellationToken = default)
        {
            if (key.StartsWith("httpcache:content:", StringComparison.Ordinal))
            {
                ContentEntryTags.Add((tags ?? []).ToArray());
            }

            await _inner.SetAsync(key, value, options, tags, cancellationToken);
        }

        public override ValueTask RemoveAsync(string key, Ct cancellationToken = default) =>
            _inner.RemoveAsync(key, cancellationToken);

        public override ValueTask RemoveAsync(IEnumerable<string> keys, Ct cancellationToken = default) =>
            _inner.RemoveAsync(keys, cancellationToken);

        public override ValueTask RemoveByTagAsync(string tag, Ct cancellationToken = default)
        {
            RemoveByTagCalls.Add(tag);
            return _inner.RemoveByTagAsync(tag, cancellationToken);
        }

        public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, Ct cancellationToken = default)
        {
            RemoveByTagCalls.AddRange(tags);
            return _inner.RemoveByTagAsync(tags, cancellationToken);
        }
    }
}
