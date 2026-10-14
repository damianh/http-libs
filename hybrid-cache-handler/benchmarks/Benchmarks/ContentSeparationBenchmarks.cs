// Copyright Damian Hickey

using BenchmarkDotNet.Attributes;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benchmarks;

/// <summary>
/// Benchmarks for the content/metadata separation architecture: the overhead
/// of two cache lookups (metadata + content) per hit, content deduplication
/// across Vary variants, and concurrent hit behavior. All entries are primed
/// and verified in GlobalSetup; measured methods are pure hits.
/// </summary>
[MemoryDiagnoser]
public class ContentSeparationBenchmarks
{
    private const string TestUrl = "https://example.com/api/data";

    private ServiceProvider _serviceProvider = null!;
    private HttpClient _cachedClient = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var fakeHandler = new SizedFakeHandler();

        var services = new ServiceCollection();
        services.AddHybridCache();
        _serviceProvider = services.BuildServiceProvider();

        var cacheHandler = new HttpHybridCacheHandler(
            fakeHandler,
            _serviceProvider.GetRequiredService<HybridCache>(),
            TimeProvider.System,
            new HttpHybridCacheHandlerOptions
            {
                CompressionThreshold = 1024,
                MaxCacheableContentSize = 10 * 1024 * 1024,
            },
            NullLogger<HttpHybridCacheHandler>.Instance);

        _cachedClient = new HttpClient(cacheHandler);

        // Prime size-keyed entries and both Vary variants, then verify hits are real.
        await PrimeAsync();
        fakeHandler.ResetCounter();
        await PrimeAsync();
        if (fakeHandler.RequestCount != 0)
        {
            throw new InvalidOperationException("Priming failed: subsequent requests were not served from cache.");
        }

        async Task PrimeAsync()
        {
            foreach (var size in (int[])[1024, 50 * 1024, 100 * 1024])
            {
                await (await _cachedClient.GetAsync($"{TestUrl}?size={size}&key=sep", HttpCompletionOption.ResponseHeadersRead)).DrainAsync();
            }

            await SendVaryRequestAsync("en-US");
            await SendVaryRequestAsync("fr-FR");
        }
    }

    [Benchmark(Description = "Hit Small (1KB) - Two Lookups")]
    public async Task SmallResponse_1KB()
        => await (await _cachedClient.GetAsync($"{TestUrl}?size=1024&key=sep", HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Medium (50KB) - Two Lookups")]
    public async Task MediumResponse_50KB()
        => await (await _cachedClient.GetAsync($"{TestUrl}?size=51200&key=sep", HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Large (100KB) - Two Lookups")]
    public async Task LargeResponse_100KB()
        => await (await _cachedClient.GetAsync($"{TestUrl}?size=102400&key=sep", HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Deduplicated Variant (50KB, Vary: Accept-Language)")]
    public async Task DeduplicatedVariant_50KB()
        => await SendVaryRequestAsync("fr-FR");

    [Benchmark(Description = "Concurrent Hits (50KB) - 10 Parallel")]
    public async Task ConcurrentCacheHits_10Parallel()
    {
        var tasks = new Task[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(GetAndDrainAsync);
        }

        await Task.WhenAll(tasks);

        async Task GetAndDrainAsync()
        {
            var response = await _cachedClient.GetAsync($"{TestUrl}?size=51200&key=sep", HttpCompletionOption.ResponseHeadersRead);
            await response.DrainAsync();
        }
    }

    private async Task SendVaryRequestAsync(string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{TestUrl}?size=51200&key=vary");
        request.Headers.Add("Accept-Language", language);
        await (await _cachedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)).DrainAsync();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cachedClient.Dispose();
        _serviceProvider.Dispose();
    }
}
