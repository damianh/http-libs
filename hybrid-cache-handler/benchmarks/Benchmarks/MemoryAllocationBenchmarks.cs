// Copyright Damian Hickey

using BenchmarkDotNet.Attributes;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benchmarks;

/// <summary>
/// Memory allocation patterns across response sizes for cache miss (store),
/// cache hit, and concurrent cache hits. Hit entries are primed in
/// GlobalSetup and verified, so measured methods contain only the operation
/// under test. Responses are drained to Stream.Null (no string conversion).
/// </summary>
[MemoryDiagnoser]
public class MemoryAllocationBenchmarks
{
    private const string TestUrl = "https://example.com/api/data";

    private ServiceProvider _serviceProvider = null!;
    private HttpClient _cachedClient = null!;
    private SizedFakeHandler _fakeHandler = null!;
    private string _hitUrl = null!;
    private string _missUrl = null!;
    private int _cacheMissInvocations;

    [Params(1024, 10 * 1024, 50 * 1024, 100 * 1024, 500 * 1024, 1024 * 1024)]
    public int ResponseSize { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fakeHandler = new SizedFakeHandler();

        var services = new ServiceCollection();
        services.AddHybridCache();
        _serviceProvider = services.BuildServiceProvider();

        var cacheHandler = new HttpHybridCacheHandler(
            _fakeHandler,
            _serviceProvider.GetRequiredService<HybridCache>(),
            TimeProvider.System,
            new HttpHybridCacheHandlerOptions
            {
                CompressionThreshold = 1024,
                MaxCacheableContentSize = 2 * 1024 * 1024, // 2MB
            },
            NullLogger<HttpHybridCacheHandler>.Instance);

        _cachedClient = new HttpClient(cacheHandler);

        // Prime the hit entry and verify subsequent requests are served from cache.
        _hitUrl = $"{TestUrl}?size={ResponseSize}&key=hit";
        _missUrl = $"{TestUrl}?size={ResponseSize}&key=miss";
        await CacheHit();
        _fakeHandler.ResetCounter();
        await CacheHit();
        if (_fakeHandler.RequestCount != 0)
        {
            throw new InvalidOperationException("Priming failed: subsequent requests were not served from cache.");
        }
    }

    // Iteration setup makes BDN use one invocation per iteration for this target.
    [IterationSetup(Target = nameof(CacheMiss_InitialStore))]
    public void ResetCacheForMiss()
    {
        Cleanup();
        Setup().GetAwaiter().GetResult();
        _cacheMissInvocations = 0;
    }

    [IterationCleanup(Target = nameof(CacheMiss_InitialStore))]
    public void VerifyCacheMiss()
    {
        if (_cacheMissInvocations != 1)
        {
            throw new InvalidOperationException("Expected exactly one benchmark invocation per miss iteration.");
        }

        if (_fakeHandler.RequestCount != 1)
        {
            throw new InvalidOperationException("Expected exactly one origin request per miss iteration.");
        }

        VerifyStoredResponseAsync().GetAwaiter().GetResult();
    }

    private async Task VerifyStoredResponseAsync()
    {
        var response = await _cachedClient.GetAsync(_missUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
        if (_fakeHandler.RequestCount != 1)
        {
            throw new InvalidOperationException("The cache miss did not store a reusable response.");
        }
    }

    [Benchmark(Description = "Cache Miss - Initial Store")]
    public async Task CacheMiss_InitialStore()
    {
        _cacheMissInvocations++;
        var response = await _cachedClient.GetAsync(_missUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
    }

    [Benchmark(Description = "Cache Hit")]
    public async Task CacheHit()
    {
        var response = await _cachedClient.GetAsync(_hitUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
    }

    [Benchmark(Description = "Cache Hit - 5 Concurrent")]
    public async Task CacheHit_5Concurrent()
    {
        var tasks = new Task[5];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(GetAndDrainAsync);
        }

        await Task.WhenAll(tasks);

        async Task GetAndDrainAsync()
        {
            var response = await _cachedClient.GetAsync(_hitUrl, HttpCompletionOption.ResponseHeadersRead);
            await response.DrainAsync();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cachedClient.Dispose();
        _serviceProvider.Dispose();
    }
}
