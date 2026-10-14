// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;

namespace DamianH.Http.HybridCacheHandler;

public class OptionalConformanceTests
{
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connection_listed_hop_by_hop_headers_are_not_replayed_from_cache()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached")
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
        response.Headers.TryAddWithoutValidation("Connection", "X-Test-Hop");
        response.Headers.TryAddWithoutValidation("X-Test-Hop", "secret");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/connection", _ct);
        var cachedResponse = await client.GetAsync("https://example.com/connection", _ct);

        mockHandler.RequestCount.ShouldBe(1);
        cachedResponse.Headers.Contains("Connection").ShouldBeFalse();
        cachedResponse.Headers.Contains("X-Test-Hop").ShouldBeFalse();
    }

    [Fact]
    public async Task Qualified_no_cache_headers_are_omitted_without_forcing_revalidation()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600, no-cache=\"Set-Cookie\"");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var originResponse = await client.GetAsync("https://example.com/qualified-no-cache", _ct);
        var cachedResponse = await client.GetAsync("https://example.com/qualified-no-cache", _ct);

        mockHandler.RequestCount.ShouldBe(1);
        originResponse.Headers.Contains("Set-Cookie").ShouldBeTrue();
        cachedResponse.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Multiple_qualified_no_cache_directives_omit_all_listed_headers()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600, no-cache=\"Set-Cookie\", no-cache=\"X-Trace-Id\"");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc");
        response.Headers.TryAddWithoutValidation("X-Trace-Id", "trace-123");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var originResponse = await client.GetAsync("https://example.com/multi-qualified-no-cache", _ct);
        var cachedResponse = await client.GetAsync("https://example.com/multi-qualified-no-cache", _ct);

        mockHandler.RequestCount.ShouldBe(1);
        originResponse.Headers.Contains("Set-Cookie").ShouldBeTrue();
        originResponse.Headers.Contains("X-Trace-Id").ShouldBeTrue();
        cachedResponse.Headers.Contains("Set-Cookie").ShouldBeFalse();
        cachedResponse.Headers.Contains("X-Trace-Id").ShouldBeFalse();
    }

    [Fact]
    public async Task Shared_cache_allows_authorization_with_must_revalidate()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("authorized")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "must-revalidate, max-age=60");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/auth");
        request1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/auth");
        request2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        await client.SendAsync(request2, _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Shared_cache_reuses_authorized_must_revalidate_response_for_followup_without_authorization()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("authorized")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "must-revalidate, max-age=3600");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/auth-followup");
        request1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        request1.Headers.TryAddWithoutValidation("Pragma", "foo");
        request1.Headers.TryAddWithoutValidation("Cache-Control", "nothing-to-see-here");
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/auth-followup");
        request2.Headers.TryAddWithoutValidation("Pragma", "foo");
        request2.Headers.TryAddWithoutValidation("Cache-Control", "nothing-to-see-here");
        await client.SendAsync(request2, _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Must_understand_can_override_no_store_for_understood_status()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cacheable")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store, must-understand, max-age=120");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/must-understand", _ct);
        await client.GetAsync("https://example.com/must-understand", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cdn_cache_control_can_override_cache_control_no_store()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cdn")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", "max-age=60");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/cdn", _ct);
        await client.GetAsync("https://example.com/cdn", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("max-age =100")]
    [InlineData("max-age= 100")]
    public async Task Shared_cache_ignores_targeted_max_age_with_space_around_equals(string targetedCacheControl)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cdn")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", targetedCacheControl);

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/cdn-invalid-targeted-max-age", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetAsync("https://example.com/cdn-invalid-targeted-max-age", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Shared_cache_ignores_targeted_max_age_when_out_of_range()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cdn")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", $"max-age={long.MaxValue}");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/cdn-out-of-range-targeted-max-age", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.GetAsync("https://example.com/cdn-out-of-range-targeted-max-age", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Shared_cache_with_targeted_cache_control_still_honors_qualified_no_cache_from_cache_control()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600, no-cache=\"Set-Cookie\"");
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", "max-age=3600");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        var originResponse = await client.GetAsync("https://example.com/cdn-qualified-no-cache", _ct);
        var cachedResponse = await client.GetAsync("https://example.com/cdn-qualified-no-cache", _ct);

        mockHandler.RequestCount.ShouldBe(1);
        originResponse.Headers.Contains("Set-Cookie").ShouldBeTrue();
        cachedResponse.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    [Fact]
    public async Task Shared_cache_handles_null_targeted_cache_control_header_names()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cdn")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", "max-age=60");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options =>
            {
                options.Mode = CacheMode.Shared;
                options.TargetedCacheControlHeaderNames = null!;
            });
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/cdn-null-targeted-headers", _ct);
        await client.GetAsync("https://example.com/cdn-null-targeted-headers", _ct);

        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Shared_cache_ignores_invalid_targeted_cache_control_header_names()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cdn")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", "max-age=60");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options =>
            {
                options.Mode = CacheMode.Shared;
                options.TargetedCacheControlHeaderNames = [null!, " ", "\t", "CDN-Cache-Control"];
            });
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/cdn-targeted-header-filtering", _ct);
        await client.GetAsync("https://example.com/cdn-targeted-header-filtering", _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Shared_cache_recognizes_cdn_cache_control_max_age()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cdn")
        };
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", "max-age=3600");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/cdn-max-age");
        request1.Headers.TryAddWithoutValidation("Pragma", "foo");
        request1.Headers.TryAddWithoutValidation("Cache-Control", "nothing-to-see-here");
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/cdn-max-age");
        request2.Headers.TryAddWithoutValidation("Pragma", "foo");
        request2.Headers.TryAddWithoutValidation("Cache-Control", "nothing-to-see-here");
        await client.SendAsync(request2, _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Shared_cache_recognizes_cdn_cache_control_public_for_authorized_requests()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("authorized")
        };
        response.Content.Headers.Expires = now.AddMinutes(1);
        response.Headers.TryAddWithoutValidation("CDN-Cache-Control", "public");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/cdn-public-auth");
        request1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/cdn-public-auth");
        request2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        await client.SendAsync(request2, _ct);

        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Shared_cache_retains_proxy_revalidate_when_cdn_cache_control_present()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var initial = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("cached")
                };
                initial.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, proxy-revalidate");
                initial.Headers.TryAddWithoutValidation("CDN-Cache-Control", "max-age=1");
                return initial;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("origin-down")
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.Mode = CacheMode.Shared);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/cdn-proxy-revalidate", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var response = await client.GetAsync("https://example.com/cdn-proxy-revalidate", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        body.ShouldBe("origin-down");
    }

    [Fact]
    public async Task Head_response_can_refresh_cached_get_freshness()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                };
                head.Headers.TryAddWithoutValidation("Cache-Control", "max-age=120");
                head.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(head);
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v1")
            };
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
            get.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(get);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-refresh", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-refresh"), _ct);

        var response = await client.GetAsync("https://example.com/head-refresh", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        requestCount.ShouldBe(2); // Initial GET + HEAD only (final GET served from cache)
        body.ShouldBe("v1");
    }

    [Fact]
    public async Task Head_response_with_no_store_invalidates_cached_get()
    {
        var getResponses = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                };
                head.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
                head.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(head);
            }

            getResponses++;
            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"v{getResponses}")
            };
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            get.Headers.ETag = new EntityTagHeaderValue($"\"v{getResponses}\"");
            return Task.FromResult(get);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-no-store", _ct);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-no-store"), _ct);

        var response = await client.GetAsync("https://example.com/head-no-store", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(3);
        body.ShouldBe("v2");
    }

    [Fact]
    public async Task Head_refresh_treats_equivalent_weak_and_strong_etags_as_non_conflicting()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty)
                };
                head.Headers.TryAddWithoutValidation("Cache-Control", "max-age=120");
                head.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
                return Task.FromResult(head);
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v1")
            };
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
            get.Headers.TryAddWithoutValidation("ETag", "w/ \"v1\"");
            return Task.FromResult(get);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-weak-strong-equivalent", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-weak-strong-equivalent"), _ct);

        var response = await client.GetAsync("https://example.com/head-weak-strong-equivalent", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        requestCount.ShouldBe(2); // Initial GET + HEAD only (final GET served from cache)
        body.ShouldBe("v1");
    }

    [Fact]
    public async Task Head_refresh_with_null_content_does_not_invalidate_cached_get()
    {
        var sequenceHandler = new SequenceMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = null
                };
                head.Headers.TryAddWithoutValidation("Cache-Control", "max-age=120");
                head.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return head;
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v1")
            };
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
            get.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return get;
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(sequenceHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-null-content", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-null-content"), _ct);

        var response = await client.GetAsync("https://example.com/head-null-content", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        sequenceHandler.RequestCount.ShouldBe(2);
        body.ShouldBe("v1");
    }

    [Fact]
    public async Task Head_response_uses_cached_get_content_length_when_missing_from_headers()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Content.Headers.ContentLength = null;
                head.Headers.TryAddWithoutValidation("Cache-Control", "max-age=120");
                head.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(head);
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v1")
            };
            get.Content.Headers.ContentLength = null;
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
            get.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(get);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-content-length", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-content-length"), _ct);

        requestCount.ShouldBe(2);
        headResponse.Content.Headers.ContentLength.ShouldBe(2);
    }

    [Fact]
    public async Task Head_refresh_does_not_replay_cached_age_or_date_when_origin_omits_them()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero);
        var initialDate = now.AddMinutes(-1);
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (req.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                head.Headers.TryAddWithoutValidation("Cache-Control", "max-age=120");
                head.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(head);
            }

            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("v1")
            };
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
            get.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            get.Headers.Date = initialDate;
            get.Headers.TryAddWithoutValidation("Age", "40");
            return Task.FromResult(get);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        fixture.SetUtcNow(now);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-age-date", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));
        var headResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-age-date"), _ct);

        requestCount.ShouldBe(2);
        headResponse.Headers.Contains("Age").ShouldBeFalse();
        headResponse.Headers.Contains("Date").ShouldBeFalse();
    }

    [Fact]
    public async Task Head_error_can_invalidate_cached_get()
    {
        var getResponses = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone)
                {
                    Content = new StringContent(string.Empty)
                });
            }

            getResponses++;
            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"v{getResponses}")
            };
            get.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            get.Headers.ETag = new EntityTagHeaderValue($"\"v{getResponses}\"");
            return Task.FromResult(get);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/head-invalidate", _ct);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://example.com/head-invalidate"), _ct);
        var response = await client.GetAsync("https://example.com/head-invalidate", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(3);
        body.ShouldBe("v2");
    }

    [Fact]
    public async Task Stale_response_can_be_served_on_5xx_without_stale_if_error()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(() =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var initial = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("cached")
                };
                initial.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return initial;
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("origin-down")
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/stale-5xx", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var response = await client.GetAsync("https://example.com/stale-5xx", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBe("cached");
    }

    [Fact]
    public async Task Stale_response_can_be_served_on_transport_error()
    {
        var requestCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var initial = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("cached")
                };
                initial.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1");
                initial.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                return Task.FromResult(initial);
            }

            throw new HttpRequestException("connection closed");
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/stale-close", _ct);
        fixture.AdvanceTime(TimeSpan.FromSeconds(2));

        var response = await client.GetAsync("https://example.com/stale-close", _ct);
        var body = await response.Content.ReadAsStringAsync(_ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBe("cached");
    }

    [Fact]
    public async Task Fresh_complete_response_can_satisfy_single_range_request()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("abcdef")
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");

        var mockHandler = new MockHttpMessageHandler(response);
        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/range-full", _ct);

        var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-full");
        rangeRequest.Headers.Range = new RangeHeaderValue(1, 3);
        var rangeResponse = await client.SendAsync(rangeRequest, _ct);
        var body = await rangeResponse.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(1);
        rangeResponse.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        body.ShouldBe("bcd");
        rangeResponse.Content.Headers.ContentRange.ShouldNotBeNull();
        rangeResponse.Content.Headers.ContentRange.From.ShouldBe(1);
        rangeResponse.Content.Headers.ContentRange.To.ShouldBe(3);
    }

    [Fact]
    public async Task Partial_response_is_cached_per_range_request()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StringContent("ab")
            };
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 1, 6);
            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-partial");
        request1.Headers.Range = new RangeHeaderValue(0, 1);
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-partial");
        request2.Headers.Range = new RangeHeaderValue(0, 1);
        var response2 = await client.SendAsync(request2, _ct);

        mockHandler.RequestCount.ShouldBe(1);
        response2.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
    }

    [Fact]
    public async Task Open_ended_range_does_not_serve_truncated_cached_partial_payload()
    {
        var originCalls = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            originCalls++;
            if (originCalls == 1)
            {
                var truncatedPartial = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StringContent("ab")
                };
                truncatedPartial.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
                truncatedPartial.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 4, 5);
                truncatedPartial.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(truncatedPartial);
            }

            return Task.FromResult(CreatePartialResponse("abcde", 0, 4, 5));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var initialRangeRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-open-ended");
        initialRangeRequest.Headers.Range = new RangeHeaderValue(0, 4);
        await client.SendAsync(initialRangeRequest, _ct);

        var openEndedRangeRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-open-ended");
        openEndedRangeRequest.Headers.Range = new RangeHeaderValue(0, null);
        var openEndedResponse = await client.SendAsync(openEndedRangeRequest, _ct);
        var openEndedBody = await openEndedResponse.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(2);
        openEndedResponse.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        openEndedBody.ShouldBe("abcde");
        openEndedResponse.Content.Headers.ContentRange.ShouldNotBeNull();
        openEndedResponse.Content.Headers.ContentRange.From.ShouldBe(0);
        openEndedResponse.Content.Headers.ContentRange.To.ShouldBe(4);
        openEndedResponse.Content.Headers.ContentRange.Length.ShouldBe(5);
    }

    [Fact]
    public async Task Suffix_range_does_not_use_cached_partial_without_total_length()
    {
        var originCalls = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            originCalls++;
            if (originCalls == 1)
            {
                var partialUnknownLength = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StringContent("abc")
                };
                partialUnknownLength.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
                partialUnknownLength.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 100-102/*");
                partialUnknownLength.Headers.AcceptRanges.Add("bytes");
                return Task.FromResult(partialUnknownLength);
            }

            return Task.FromResult(CreatePartialResponse("z", 199, 199, 200));
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var initialRangeRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-suffix");
        initialRangeRequest.Headers.Range = new RangeHeaderValue(100, 102);
        await client.SendAsync(initialRangeRequest, _ct);

        var suffixRangeRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-suffix");
        suffixRangeRequest.Headers.TryAddWithoutValidation("Range", "bytes=-1");
        var suffixResponse = await client.SendAsync(suffixRangeRequest, _ct);
        var suffixBody = await suffixResponse.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(2);
        suffixResponse.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        suffixBody.ShouldBe("z");
    }

    [Fact]
    public async Task Full_get_replaces_cached_partial_response_when_origin_returns_cacheable_full_response()
    {
        var requestRanges = new List<string?>();
        var originCalls = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            originCalls++;
            var range = req.Headers.Range?.Ranges.SingleOrDefault();
            requestRanges.Add(range == null
                ? null
                : $"bytes={range.From}-{(range.To.HasValue ? range.To.Value.ToString() : string.Empty)}");

            if (originCalls == 1)
            {
                return Task.FromResult(CreatePartialResponse("ab", 0, 1, 6));
            }

            if (range?.From == 2 && !range.To.HasValue)
            {
                return Task.FromResult(CreatePartialResponse("cdef", 2, 5, 6));
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("abcdef")
            };
            full.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
            return Task.FromResult(full);
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        await client.GetAsync("https://example.com/full-after-partial", _ct);
        var firstFullResponse = await client.GetAsync("https://example.com/full-after-partial", _ct);
        var secondFullResponse = await client.GetAsync("https://example.com/full-after-partial", _ct);
        var firstBody = await firstFullResponse.Content.ReadAsStringAsync(_ct);
        var secondBody = await secondFullResponse.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(2);
        requestRanges.Count.ShouldBe(2);
        requestRanges[0].ShouldBeNull();
        requestRanges[1].ShouldBeNull();
        firstFullResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondFullResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        firstBody.ShouldBe("abcdef");
        secondBody.ShouldBe("abcdef");
    }

    [Fact]
    public async Task Different_partial_ranges_use_separate_cache_entries()
    {
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var range = req.Headers.Range?.Ranges.SingleOrDefault();

            if (range?.From == 0 && range.To == 1)
            {
                return Task.FromResult(CreatePartialResponse("ab", 0, 1, 6));
            }

            if (range?.From == 2 && range.To == 3)
            {
                return Task.FromResult(CreatePartialResponse("cd", 2, 3, 6));
            }

            throw new InvalidOperationException("Unexpected range request.");
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(mockHandler);
        using var client = fixture.CreateClient();

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-fragments");
        request1.Headers.Range = new RangeHeaderValue(0, 1);
        await client.SendAsync(request1, _ct);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-fragments");
        request2.Headers.Range = new RangeHeaderValue(2, 3);
        var response2 = await client.SendAsync(request2, _ct);
        var body2 = await response2.Content.ReadAsStringAsync(_ct);

        var request3 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/range-fragments");
        request3.Headers.Range = new RangeHeaderValue(0, 1);
        var response3 = await client.SendAsync(request3, _ct);
        var body3 = await response3.Content.ReadAsStringAsync(_ct);

        mockHandler.RequestCount.ShouldBe(2);
        response2.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response3.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        body2.ShouldBe("cd");
        body3.ShouldBe("ab");
    }

    private static HttpResponseMessage CreatePartialResponse(string body, long from, long to, long totalLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StringContent(body)
        };
        response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=3600");
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, totalLength);
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    private sealed class SequenceMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Ct cancellationToken)
        {
            RequestCount++;
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

}
