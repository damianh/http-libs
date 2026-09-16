using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class DistributedCompatibilityTests
{
    private const string Url = "https://compatibility.example.test/distributed";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_HybridCache_serializes_metadata_and_body_to_L2_across_service_providers(bool compress)
    {
        using var backendServices = new ServiceCollection().AddDistributedMemoryCache().BuildServiceProvider();
        var backend = new ObservedDistributedCache(backendServices.GetRequiredService<IDistributedCache>());
        var payload = new string('x', 4096);
        Action<HttpHybridCacheHandlerOptions> options = o => o.CompressionThreshold = compress ? 16 : 0;
        Action<IServiceCollection> configureServices = services => services.AddSingleton<IDistributedCache>(backend);
        using (var writer = new CacheFixture(request =>
        {
            var language = Assert.Single(request.Headers.GetValues("Accept-Language"));
            var response = CacheFixture.Response(payload + language);
            response.Headers.ETag = new EntityTagHeaderValue("\"" + language + "\"");
            response.Headers.Vary.Add("Accept-Language");
            response.Headers.TryAddWithoutValidation("X-Values", new[] { "one,two", "three" });
            response.Content.Headers.ContentLanguage.Add(language);
            return response;
        }, options, configureServices: configureServices))
        {
            Assert.Equal("Microsoft.Extensions.Caching.Hybrid", writer.Cache.GetType().Assembly.GetName().Name);
            foreach (var language in new[] { "en", "fr" })
            {
                using var request = Request(language);
                using var response = await writer.Client.SendAsync(request, Ct);
                Assert.Equal(payload + language, await response.Content.ReadAsStringAsync());
            }
            Assert.Equal(2, writer.Origin.Calls);
            Assert.Contains(backend.WrittenKeys, key => key.Contains("GET:"));
            Assert.Contains(backend.WrittenKeys, key => key.Contains("httpcache:content:"));
        }

        using var reader = new CacheFixture(_ => throw new InvalidOperationException("The cold provider must load L2, not call the origin."),
            options, configureServices: configureServices);
        Assert.Equal("Microsoft.Extensions.Caching.Hybrid", reader.Cache.GetType().Assembly.GetName().Name);
        reader.Clock.Advance(TimeSpan.FromSeconds(10));
        foreach (var language in new[] { "fr", "en" })
        {
            using var request = Request(language);
            using var response = await reader.Client.SendAsync(request, Ct);
            Assert.Equal(payload + language, await response.Content.ReadAsStringAsync());
            Assert.Equal("\"" + language + "\"", response.Headers.ETag?.Tag);
            Assert.Equal(new[] { "one,two", "three" }, response.Headers.GetValues("X-Values"));
            Assert.Equal(language, Assert.Single(response.Content.Headers.ContentLanguage));
            Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.Age);
            Assert.Equal("HIT-FRESH", Assert.Single(response.Headers.GetValues("X-Cache-Diagnostic")));
            if (compress)
            {
                Assert.Equal("true", Assert.Single(response.Headers.GetValues("X-Cache-Compressed")));
            }
        }
        Assert.Equal(0, reader.Origin.Calls);
        Assert.Contains(backend.HitKeys, key => key.Contains("GET:"));
        Assert.Contains(backend.HitKeys, key => key.Contains("httpcache:content:"));
    }

    private static HttpRequestMessage Request(string language)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.Add("Accept-Language", language);
        return request;
    }

    private sealed class ObservedDistributedCache(IDistributedCache inner) : IDistributedCache
    {
        public ConcurrentQueue<string> WrittenKeys { get; } = new();
        public ConcurrentQueue<string> HitKeys { get; } = new();

        public byte[]? Get(string key)
        {
            var bytes = inner.Get(key);
            if (bytes != null) HitKeys.Enqueue(key);
            return bytes;
        }

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            var bytes = await inner.GetAsync(key, token);
            if (bytes != null) HitKeys.Enqueue(key);
            return bytes;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Assert.NotEmpty(value);
            inner.Set(key, value, options);
            WrittenKeys.Enqueue(key);
        }

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Assert.NotEmpty(value);
            await inner.SetAsync(key, value, options, token);
            WrittenKeys.Enqueue(key);
        }

        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    }
}
