// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace DamianH.Http.HybridCacheHandler;

public class ClientConditionalTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_cached_ETag_match_returns_304()
    {
        var lastModified = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached", response =>
        {
            response.Headers.ETag = new EntityTagHeaderValue("\"abcdef\"");
            response.Content.Headers.LastModified = lastModified;
            response.Content.Headers.Expires = expires;
            response.Content.Headers.ContentLocation = new Uri("https://example.com/resource");
        }));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"abcdef\"");

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Content.Headers.Contains("Content-Length").ShouldBeFalse();
        response.Content.Headers.Contains("Content-Type").ShouldBeFalse();
        response.Content.Headers.Contains("Content-Encoding").ShouldBeFalse();
        response.Content.Headers.TryGetValues("Last-Modified", out var lastModifiedValues).ShouldBeTrue();
        lastModifiedValues.ShouldBe([lastModified.ToString("R", CultureInfo.InvariantCulture)]);
        response.Content.Headers.TryGetValues("Expires", out var expiresValues).ShouldBeTrue();
        expiresValues.ShouldBe([expires.ToString("R", CultureInfo.InvariantCulture)]);
        response.Content.Headers.TryGetValues("Content-Location", out var contentLocationValues).ShouldBeTrue();
        contentLocationValues.ShouldBe(["https://example.com/resource"]);
        response.Headers.TryGetValues("ETag", out var etagValues).ShouldBeTrue();
        etagValues.ShouldContain("\"abcdef\"");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task If_None_Match_takes_precedence_over_If_Modified_Since()
    {
        var lastModified = DateTimeOffset.UtcNow.AddMinutes(-5);
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached", response =>
        {
            response.Headers.ETag = new EntityTagHeaderValue("\"abcdef\"");
            response.Content.Headers.LastModified = lastModified;
        }));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"abcdef\"");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified.AddMinutes(-10).ToString("R", CultureInfo.InvariantCulture));

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_cached_unquoted_ETag_matches_quoted_If_None_Match()
    {
        var originResponse = CreateCacheableResponse("cached");
        originResponse.Headers.TryAddWithoutValidation("ETag", "abcdef");
        var mockHandler = new MockHttpMessageHandler(originResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"abcdef\"");

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_cached_ETag_matches_list_item_after_even_backslashes()
    {
        var originResponse = CreateCacheableResponse("cached");
        originResponse.Headers.TryAddWithoutValidation("ETag", "\"def\"");
        var mockHandler = new MockHttpMessageHandler(originResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"abc\\\\\", \"def\"");

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_cached_response_without_Last_Modified_or_Date_ignores_If_Modified_Since()
    {
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached"));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("R", CultureInfo.InvariantCulture));

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(_ct)).ShouldBe("cached");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_cached_response_without_Last_Modified_and_Date_before_If_Modified_Since_returns_200()
    {
        var responseDate = DateTimeOffset.UtcNow;
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached", response =>
        {
            response.Headers.Date = responseDate;
        }));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", responseDate.AddMinutes(10).ToString("R", CultureInfo.InvariantCulture));
        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(_ct)).ShouldBe("cached");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_cached_response_without_Last_Modified_and_Date_after_If_Modified_Since_returns_304()
    {
        var responseDate = DateTimeOffset.UtcNow;
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached", response =>
        {
            response.Headers.Date = responseDate;
        }));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", responseDate.AddMinutes(-10).ToString("R", CultureInfo.InvariantCulture));
        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_cached_response_with_non_http_date_If_Modified_Since_ignores_header()
    {
        var lastModified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached", response =>
        {
            response.Content.Headers.LastModified = lastModified;
        }));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", "2024-01-02T03:04:05Z");

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(_ct)).ShouldBe("cached");
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task RFC850_If_Modified_Since_is_honored()
    {
        var lastModified = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var mockHandler = new MockHttpMessageHandler(CreateCacheableResponse("cached", response =>
        {
            response.Content.Headers.LastModified = lastModified;
        }));

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified.ToString("dddd, dd-MMM-yy HH':'mm':'ss 'GMT'", CultureInfo.InvariantCulture));

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Bare_If_None_Match_is_quoted_when_forwarded()
    {
        HttpRequestMessage? lastRequest = null;
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            lastRequest = request;
            return Task.FromResult(CreateCacheableResponse("origin"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        request.Headers.TryAddWithoutValidation("If-None-Match", "abcdef");

        _ = await client.SendAsync(request, _ct);

        lastRequest.ShouldNotBeNull();
        lastRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["\"abcdef\""]);
    }

    [Fact]
    public async Task Client_If_None_Match_is_preserved_when_cache_entry_has_no_validator()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(CreateCacheableResponse("cached"));
            }

            validationRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = null
            });
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"abcdef\"");

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["\"abcdef\""]);
    }

    [Fact]
    public async Task Client_If_Modified_Since_is_preserved_when_cache_entry_has_no_validator()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var ifModifiedSince = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("R", CultureInfo.InvariantCulture);
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(CreateCacheableResponse("cached"));
            }

            validationRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = null
            });
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", ifModifiedSince);

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-Modified-Since", out var ifModifiedSinceValues).ShouldBeTrue();
        ifModifiedSinceValues.ShouldBe([ifModifiedSince]);
    }

    [Fact]
    public async Task Weak_ETag_is_preserved_in_validation_request()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var originResponse = CreateCacheableResponse("cached", response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "W/\"abcdef\"");
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
        });

        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(originResponse);
            }

            validationRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new StringContent(string.Empty)
            });
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        _ = await client.GetAsync("https://example.com/resource", _ct);

        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["W/\"abcdef\""]);
    }

    [Fact]
    public async Task Raw_ETag_value_is_stored_in_cached_metadata()
    {
        const string RawEtag = "W/ \"abc\\\\def\"";
        var cache = new RecordingHybridCache();
        var originResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached")
        };
        originResponse.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        originResponse.Headers.TryAddWithoutValidation("ETag", RawEtag);
        var mockHandler = new SingleResponseMessageHandler(originResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler, customCache: cache);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);

        cache.StoredMetadata.ShouldHaveSingleItem().ETag.ShouldBe(RawEtag);
    }

    [Fact]
    public async Task Unquoted_stored_ETag_is_quoted_in_validation_request()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var originResponse = CreateCacheableResponse("cached", response =>
        {
            response.Headers.TryAddWithoutValidation("ETag", "abcdef");
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
        });

        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(originResponse);
            }

            validationRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new StringContent(string.Empty)
            });
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        _ = await client.GetAsync("https://example.com/resource", _ct);

        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["\"abcdef\""]);
    }

    [Fact]
    public async Task Client_If_Modified_Since_returns_304_after_origin_304_revalidation()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var lastModified = DateTimeOffset.UtcNow.AddDays(-1);
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(CreateCacheableResponse("cached", response =>
                {
                    response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                    response.Content.Headers.LastModified = lastModified;
                }));
            }

            validationRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = new StringContent(string.Empty)
            });
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified.ToString("R", CultureInfo.InvariantCulture));

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-Modified-Since", out var ifModifiedSinceValues).ShouldBeTrue();
        ifModifiedSinceValues.Count().ShouldBe(1);
        ifModifiedSinceValues.Single().ShouldBe(lastModified.ToString("R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Stale_client_conditional_revalidation_with_null_304_content_returns_304()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(CreateCacheableResponse("cached", response =>
                {
                    response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) };
                }));
            }

            validationRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Content = null
            });
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/resource", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", "\"client-token\"");

        var response = await client.SendAsync(conditionalRequest, _ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["\"client-token\""]);
        requestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Fresh_Vary_mismatch_revalidates_with_stored_ETag()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(CreateCacheableResponse("variant-1", response =>
                {
                    response.Headers.ETag = new EntityTagHeaderValue("\"abcdef\"");
                    response.Headers.TryAddWithoutValidation("Vary", "Abc");
                }));
            }

            validationRequest = request;
            return Task.FromResult(CreateCacheableResponse("variant-2"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        firstRequest.Headers.TryAddWithoutValidation("Abc", "123");
        _ = await client.SendAsync(firstRequest, _ct);

        var secondRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        secondRequest.Headers.TryAddWithoutValidation("Abc", "456");
        _ = await client.SendAsync(secondRequest, _ct);

        requestCount.ShouldBe(2);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("Abc", out var abcValues).ShouldBeTrue();
        abcValues.ShouldBe(["456"]);
        validationRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["\"abcdef\""]);
    }

    [Fact]
    public async Task Fresh_Vary_mismatch_with_200_response_updates_cached_variant()
    {
        HttpRequestMessage? validationRequest = null;
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(CreateCacheableResponse("variant-1", response =>
                {
                    response.Headers.ETag = new EntityTagHeaderValue("\"abcdef\"");
                    response.Headers.TryAddWithoutValidation("Vary", "Abc");
                }));
            }

            if (requestCount == 2)
            {
                validationRequest = request;
                return Task.FromResult(CreateCacheableResponse("variant-2", response =>
                {
                    response.Headers.ETag = new EntityTagHeaderValue("\"ghijkl\"");
                    response.Headers.TryAddWithoutValidation("Vary", "Abc");
                }));
            }

            return Task.FromResult(CreateCacheableResponse("variant-3"));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        firstRequest.Headers.TryAddWithoutValidation("Abc", "123");
        _ = await client.SendAsync(firstRequest, _ct);

        var secondRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        secondRequest.Headers.TryAddWithoutValidation("Abc", "456");
        var secondResponse = await client.SendAsync(secondRequest, _ct);

        var thirdRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/resource");
        thirdRequest.Headers.TryAddWithoutValidation("Abc", "456");
        var thirdResponse = await client.SendAsync(thirdRequest, _ct);

        requestCount.ShouldBe(2);
        validationRequest.ShouldNotBeNull();
        validationRequest.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues).ShouldBeTrue();
        ifNoneMatchValues.ShouldBe(["\"abcdef\""]);
        (await secondResponse.Content.ReadAsStringAsync(_ct)).ShouldBe("variant-2");
        (await thirdResponse.Content.ReadAsStringAsync(_ct)).ShouldBe("variant-2");
    }

    private static HttpResponseMessage CreateCacheableResponse(string body, Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };

        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        configure?.Invoke(response);
        return response;
    }

    private sealed class RecordingHybridCache : HybridCache, IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly HybridCache _inner;

        public List<CachedHttpMetadata> StoredMetadata { get; } = [];

        public RecordingHybridCache()
        {
            _serviceProvider = CreateServiceProvider();
            _inner = _serviceProvider.GetRequiredService<HybridCache>();
        }

        private static ServiceProvider CreateServiceProvider()
        {
            var services = new ServiceCollection();
            services.AddHybridCache();
            return services.BuildServiceProvider();
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
            if (value is CachedHttpMetadata metadata)
            {
                StoredMetadata.Add(metadata);
            }
            else if (value is CachedHttpEntry entry)
            {
                StoredMetadata.AddRange(entry.Variants);
            }

            await _inner.SetAsync(key, value, options, tags, cancellationToken);
        }

        public override ValueTask RemoveAsync(string key, Ct cancellationToken = default) =>
            _inner.RemoveAsync(key, cancellationToken);

        public override ValueTask RemoveAsync(IEnumerable<string> keys, Ct cancellationToken = default) =>
            _inner.RemoveAsync(keys, cancellationToken);

        public override ValueTask RemoveByTagAsync(string tag, Ct cancellationToken = default) =>
            _inner.RemoveByTagAsync(tag, cancellationToken);

        public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, Ct cancellationToken = default) =>
            _inner.RemoveByTagAsync(tags, cancellationToken);

        public void Dispose()
        {
            _serviceProvider.Dispose();
        }
    }

    private sealed class SingleResponseMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Ct ct)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
