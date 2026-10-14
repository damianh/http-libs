// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DamianH.FileDistributedCache;

/// <summary>
/// End-to-end tests demonstrating that <see cref="FileDistributedCache"/> works as the L2
/// backend for <see cref="HttpHybridCacheHandler"/>.
/// </summary>
public sealed class IntegrationWithHttpHybridCacheHandlerTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _fakeTime = new();

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task CachedResponse_SurvivedL1Eviction_ServedFromFileL2()
    {
        var ct = TestContext.Current.CancellationToken;
        const string ResponseBody = "persistent response from origin";

        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody),
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };

        var mockHandler = new CountingHttpMessageHandler(mockResponse);

        await using var sp = BuildServiceProvider(mockHandler);
        var client = CreateClient(sp);

        // First request — populates both L1 (memory) and L2 (file).
        var response1 = await client.GetAsync("https://example.com/resource", ct);
        var body1 = await response1.Content.ReadAsStringAsync(ct);
        body1.ShouldBe(ResponseBody);
        mockHandler.RequestCount.ShouldBe(1);

        // Simulate L1 eviction by compacting the memory cache to zero.
        EvictL1(sp);

        // Second request — L1 is empty, so HybridCache falls back to L2 (the file cache).
        var response2 = await client.GetAsync("https://example.com/resource", ct);
        var body2 = await response2.Content.ReadAsStringAsync(ct);
        body2.ShouldBe(ResponseBody);

        // Origin should NOT have been called again — L2 served the response.
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task FileCache_PersistsEntryAcrossServiceProviderRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        const string ResponseBody = "response that survives restart";

        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseBody),
        };
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };

        var mockHandler = new CountingHttpMessageHandler(mockResponse);

        // --- First "process" ---
        await using (var sp1 = BuildServiceProvider(mockHandler))
        {
            var client1 = CreateClient(sp1);
            var r = await client1.GetAsync("https://example.com/stable", ct);
            (await r.Content.ReadAsStringAsync(ct)).ShouldBe(ResponseBody);
        }

        mockHandler.RequestCount.ShouldBe(1);

        // --- Second "process" (new ServiceProvider, same cache directory) ---
        await using (var sp2 = BuildServiceProvider(mockHandler))
        {
            var client2 = CreateClient(sp2);

            // L1 is empty (fresh process). HybridCache reads from L2 file cache.
            var r2 = await client2.GetAsync("https://example.com/stable", ct);
            (await r2.Content.ReadAsStringAsync(ct)).ShouldBe(ResponseBody);
        }

        // Still only 1 origin request — the file cache served the second "process".
        mockHandler.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredFileEntry_NotServedAfterTtlElapsed()
    {
        var ct = TestContext.Current.CancellationToken;

        var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("short-lived response"),
        };
        // Cache for 10 seconds only.
        mockResponse.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(10) };

        var mockHandler = new CountingHttpMessageHandler(mockResponse);

        await using var sp = BuildServiceProvider(mockHandler);
        var client = CreateClient(sp);

        // First request — caches the entry.
        await client.GetAsync("https://example.com/short", ct);
        mockHandler.RequestCount.ShouldBe(1);

        // Advance time past TTL and evict L1.
        _fakeTime.Advance(TimeSpan.FromSeconds(11));
        EvictL1(sp);

        // Second request — both L1 and L2 entries are expired; origin is called again.
        var response2 = await client.GetAsync("https://example.com/short", ct);
        (await response2.Content.ReadAsStringAsync(ct)).ShouldBe("short-lived response");
        mockHandler.RequestCount.ShouldBe(2);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildServiceProvider(HttpMessageHandler innerHandler) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(_fakeTime)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddFileDistributedCache(o =>
            {
                o.CacheDirectory = _cacheDir;
                o.EvictionInterval = TimeSpan.FromDays(1); // don't evict during test
            })
            .AddHttpHybridCacheHandler(_ => { })
            .AddHttpClient("CachingClient")
            .ConfigurePrimaryHttpMessageHandler(_ => innerHandler)
            .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>())
            .Services
            .BuildServiceProvider();

    private static HttpClient CreateClient(ServiceProvider sp) =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("CachingClient");

    private static void EvictL1(ServiceProvider sp)
    {
        // Forcing a full compaction causes MemoryCache to remove expired entries immediately.
        var memCache = sp.GetService<IMemoryCache>() as MemoryCache;
        memCache?.Compact(1.0);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _template;
        private int _count;

        public CountingHttpMessageHandler(HttpResponseMessage template) => _template = template;

        public int RequestCount => _count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Ct ct)
        {
            Interlocked.Increment(ref _count);

            var bytes = await _template.Content.ReadAsByteArrayAsync(ct);
            var cloned = new HttpResponseMessage(_template.StatusCode)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = request,
            };

            foreach (var h in _template.Headers)
            {
                cloned.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            foreach (var h in _template.Content.Headers)
            {
                cloned.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            return cloned;
        }
    }
}
