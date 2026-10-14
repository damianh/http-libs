// Copyright Damian Hickey

using BenchmarkDotNet.Attributes;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benchmarks;

/// <summary>
/// Cache-hit benchmarks targeting Large Object Heap (LOH) behavior around the
/// default 85,000-byte object threshold, with and without stored-content compression. All entries
/// are primed and verified in GlobalSetup; measured methods are pure hits.
/// </summary>
[MemoryDiagnoser]
public class LohBenchmarks
{
    private const string TestUrl = "https://example.com/api/data";

    private ServiceProvider _serviceProvider = null!;
    private HttpClient _compressedClient = null!;
    private HttpClient _uncompressedClient = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        _serviceProvider = services.BuildServiceProvider();
        var hybridCache = _serviceProvider.GetRequiredService<HybridCache>();

        var compressedFake = new SizedFakeHandler();
        _compressedClient = new HttpClient(new HttpHybridCacheHandler(
            compressedFake,
            hybridCache,
            TimeProvider.System,
            new HttpHybridCacheHandlerOptions
            {
                CompressionThreshold = 1024, // Compress >=1KiB
                MaxCacheableContentSize = 10 * 1024 * 1024,
            },
            NullLogger<HttpHybridCacheHandler>.Instance));

        var uncompressedFake = new SizedFakeHandler();
        _uncompressedClient = new HttpClient(new HttpHybridCacheHandler(
            uncompressedFake,
            hybridCache,
            TimeProvider.System,
            new HttpHybridCacheHandlerOptions
            {
                CompressionThreshold = long.MaxValue, // Disable compression
                MaxCacheableContentSize = 10 * 1024 * 1024,
            },
            NullLogger<HttpHybridCacheHandler>.Instance));

        // Prime all hit entries, then verify hits are real.
        foreach (var (client, size, key) in Entries())
        {
            await (await client.GetAsync(Url(size, key), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();
        }

        compressedFake.ResetCounter();
        uncompressedFake.ResetCounter();
        foreach (var (client, size, key) in Entries())
        {
            await (await client.GetAsync(Url(size, key), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();
        }

        if (compressedFake.RequestCount != 0 || uncompressedFake.RequestCount != 0)
        {
            throw new InvalidOperationException("Priming failed: subsequent requests were not served from cache.");
        }

        IEnumerable<(HttpClient Client, int Size, string Key)> Entries() =>
        [
            (_uncompressedClient, 80 * 1024, "uncomp"),
            (_uncompressedClient, 85 * 1024, "uncomp"),
            (_uncompressedClient, 100 * 1024, "uncomp"),
            (_compressedClient, 100 * 1024, "comp"),
            (_compressedClient, 500 * 1024, "comp"),
            (_compressedClient, 1024 * 1024, "comp"),
        ];
    }

    private static string Url(int size, string key) => $"{TestUrl}?size={size}&key={key}-{size}";

    [Benchmark(Description = "Hit Below LOH: 80KiB - Uncompressed")]
    public async Task BelowLoh_80KB_Uncompressed()
        => await (await _uncompressedClient.GetAsync(Url(80 * 1024, "uncomp"), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Above LOH: 85KiB - Uncompressed")]
    public async Task AtLoh_85KB_Uncompressed()
        => await (await _uncompressedClient.GetAsync(Url(85 * 1024, "uncomp"), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Above LOH: 100KB - Uncompressed")]
    public async Task AboveLoh_100KB_Uncompressed()
        => await (await _uncompressedClient.GetAsync(Url(100 * 1024, "uncomp"), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Above LOH: 100KB - With Compression")]
    public async Task AboveLoh_100KB_WithCompression()
        => await (await _compressedClient.GetAsync(Url(100 * 1024, "comp"), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Large: 500KB - With Compression")]
    public async Task Large_500KB_WithCompression()
        => await (await _compressedClient.GetAsync(Url(500 * 1024, "comp"), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [Benchmark(Description = "Hit Very Large: 1MB - With Compression")]
    public async Task VeryLarge_1MB_WithCompression()
        => await (await _compressedClient.GetAsync(Url(1024 * 1024, "comp"), HttpCompletionOption.ResponseHeadersRead)).DrainAsync();

    [GlobalCleanup]
    public void Cleanup()
    {
        _compressedClient.Dispose();
        _uncompressedClient.Dispose();
        _serviceProvider.Dispose();
    }
}
