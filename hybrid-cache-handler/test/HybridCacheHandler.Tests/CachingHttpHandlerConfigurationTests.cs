// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DamianH.Http.HybridCacheHandler;

public class CachingHttpHandlerConfigurationTests
{
    private const string TestUrl = "https://example.com/resource";
    private readonly Ct _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Configure_fallback_cache_duration()
    {
        var fallbackCacheDuration = TimeSpan.FromMinutes(10);

        var mockHandler = new MockHttpMessageHandler(async _ =>
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("response"),
                Headers = { { "Cache-Control", "public" } }
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.FallbackCacheDuration = fallbackCacheDuration);
        using var client = fixture.CreateClient();

        // First request - cache miss
        await client.GetAsync(TestUrl, _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Advance time but stay within default duration
        fixture.AdvanceTime(TimeSpan.FromMinutes(5));
        await client.GetAsync(TestUrl, _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Advance past default duration
        fixture.AdvanceTime(TimeSpan.FromMinutes(6));
        await client.GetAsync(TestUrl, _ct);
        mockHandler.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task Respect_max_entry_size_limit()
    {
        const long MaxSize = 100L;

        var mockHandler = new MockHttpMessageHandler(async request =>
        {
            await Task.Yield();
            var content = request.RequestUri!.PathAndQuery.Contains("large")
                ? new string('x', 200)
                : "small";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                Headers = { { "Cache-Control", "public, max-age=3600" } }
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = MaxSize);
        using var client = fixture.CreateClient();

        // Small response - should be cached
        await client.GetAsync($"{TestUrl}/small", _ct);
        mockHandler.RequestCount.ShouldBe(1);

        await client.GetAsync($"{TestUrl}/small", _ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Large response - should NOT be cached
        await client.GetAsync($"{TestUrl}/large", _ct);
        mockHandler.RequestCount.ShouldBe(2);

        await client.GetAsync($"{TestUrl}/large", _ct);
        mockHandler.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task Allow_unlimited_entry_size_when_configured()
    {
        var mockHandler = new MockHttpMessageHandler(async _ =>
        {
            await Task.Yield();
            var largeContent = new string('x', 10_000_000); // 10MB

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(largeContent),
                Headers = { { "Cache-Control", "public, max-age=3600" } }
            };
        });

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options => options.MaxCacheableContentSize = long.MaxValue); // Unlimited
        using var client = fixture.CreateClient();

        // Large response - should be cached when unlimited
        await client.GetAsync(TestUrl, _ct);
        mockHandler.RequestCount.ShouldBe(1);

        await client.GetAsync(TestUrl, _ct);
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public void Targeted_cache_control_header_names_do_not_mutate_defaults()
    {
        var options = new HttpHybridCacheHandlerOptions();

        options.TargetedCacheControlHeaderNames[0] = "X-Test-Cache-Control";

        HttpHybridCacheHandlerOptions.DefaultTargetedCacheControlHeaderNames[0].ShouldBe("CDN-Cache-Control");
    }

    [Fact]
    public void Cap_max_cacheable_content_size_to_hybrid_cache_limit()
    {
        using var services = new ServiceCollection()
            .AddHttpHybridCacheHandler(options => options.MaxCacheableContentSize = long.MaxValue)
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<HttpHybridCacheHandlerOptions>>().Value;
        options.MaxCacheableContentSize.ShouldBe(int.MaxValue);
    }

    [Fact]
    public void Propagate_mode_from_configure_delegate()
    {
        using var services = new ServiceCollection()
            .AddHttpHybridCacheHandler(options => options.Mode = CacheMode.Shared)
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<HttpHybridCacheHandlerOptions>>().Value;
        options.Mode.ShouldBe(CacheMode.Shared);
    }

    [Fact]
    public void Propagate_targeted_cache_control_header_names_from_configure_delegate()
    {
        var expected = new[] { "X-Shared-Cache-Control", "Surrogate-Control" };

        using var services = new ServiceCollection()
            .AddHttpHybridCacheHandler(options => options.TargetedCacheControlHeaderNames = expected)
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<HttpHybridCacheHandlerOptions>>().Value;
        options.TargetedCacheControlHeaderNames.ShouldBe(expected);
    }

    [Fact]
    public async Task Configure_delegate_is_applied_once()
    {
        var configureInvocationCount = 0;
        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("response"),
            Headers = { { "Cache-Control", "public, max-age=3600" } }
        };
        var mockHandler = new MockHttpMessageHandler(mockResponse);

        await using var fixture = new HttpHybridCacheHandlerFixture(
            mockHandler,
            options =>
            {
                configureInvocationCount++;
                options.MaxCacheableContentSize = 1024 * 1024;
            });
        using var client = fixture.CreateClient();

        await client.GetAsync(TestUrl, _ct);

        configureInvocationCount.ShouldBe(1);
    }
}
