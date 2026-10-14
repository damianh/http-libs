// Copyright Damian Hickey

using BenchmarkDotNet.Attributes;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benchmarks;

/// <summary>
/// Handler overhead benchmarks for small responses. The baseline is a request
/// straight through the fake inner handler; the other benchmarks isolate the
/// cache handler's hit, miss, concurrency, and Vary-key costs.
/// Cache priming happens in GlobalSetup so measured methods contain only the
/// operation under test.
/// </summary>
[MemoryDiagnoser]
public class CachingBenchmarks
{
    private const string TestUrl = "https://example.com/api/data";
    private const string HitUrl = $"{TestUrl}?size=1024&key=hit";
    private const string MissUrl = $"{TestUrl}?size=1024&key=miss";

    private ServiceProvider _serviceProvider = null!;
    private HttpClient _cachedClient = null!;
    private HttpClient _uncachedClient = null!;
    private SizedFakeHandler _fakeHandler = null!;
    private int _cacheMissInvocations;

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
            new HttpHybridCacheHandlerOptions(),
            NullLogger<HttpHybridCacheHandler>.Instance);

        _cachedClient = new HttpClient(cacheHandler);
        _uncachedClient = new HttpClient(_fakeHandler, disposeHandler: false);

        // Prime the hit and vary-header entries, then verify hits are real.
        await CacheHit();
        await CacheHit_VaryHeader();
        _fakeHandler.ResetCounter();

        await CacheHit();
        await CacheHit_VaryHeader();
        if (_fakeHandler.RequestCount != 0)
        {
            throw new InvalidOperationException("Priming failed: subsequent requests were not served from cache.");
        }
    }

    [Benchmark(Baseline = true)]
    public async Task UncachedRequest()
    {
        var response = await _uncachedClient.GetAsync(HitUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
    }

    [Benchmark]
    public async Task CacheHit()
    {
        var response = await _cachedClient.GetAsync(HitUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
    }

    // Iteration setup makes BDN use one invocation per iteration for this target.
    [IterationSetup(Target = nameof(CacheMiss_Store))]
    public void ResetCacheForMiss()
    {
        Cleanup();
        Setup().GetAwaiter().GetResult();
        _cacheMissInvocations = 0;
    }

    [IterationCleanup(Target = nameof(CacheMiss_Store))]
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
        var response = await _cachedClient.GetAsync(MissUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
        if (_fakeHandler.RequestCount != 1)
        {
            throw new InvalidOperationException("The cache miss did not store a reusable response.");
        }
    }

    [Benchmark]
    public async Task CacheMiss_Store()
    {
        _cacheMissInvocations++;
        var response = await _cachedClient.GetAsync(MissUrl, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
    }

    [Benchmark]
    public async Task CacheHit_10Concurrent()
    {
        var tasks = new Task[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(GetAndDrainAsync);
        }

        await Task.WhenAll(tasks);

        async Task GetAndDrainAsync()
        {
            var response = await _cachedClient.GetAsync(HitUrl, HttpCompletionOption.ResponseHeadersRead);
            await response.DrainAsync();
        }
    }

    [Benchmark]
    public async Task CacheHit_VaryHeader()
    {
        using var request = CreateVaryRequest();
        var response = await _cachedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await response.DrainAsync();
    }

    private static HttpRequestMessage CreateVaryRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{TestUrl}?size=1024&key=vary");
        request.Headers.Add("Accept-Language", "en-US");
        return request;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cachedClient.Dispose();
        _uncachedClient.Dispose();
        _serviceProvider.Dispose();
    }
}
