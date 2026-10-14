// Copyright Damian Hickey

using System.Diagnostics;
using System.Net;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFusionCache()
    .WithDefaultEntryOptions(opt =>
    {
        opt.Duration = TimeSpan.FromMinutes(5);
    })
    .WithSystemTextJsonSerializer()
    .AsHybridCache();

builder.Services
    .AddHttpClient("CachedClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    })
    .AddHttpMessageHandler(sp => new HttpHybridCacheHandler(
        sp.GetRequiredService<HybridCache>(),
        TimeProvider.System,
        Options.Create(new HttpHybridCacheHandlerOptions
        {
            Mode = CacheMode.Private, // Private cache for client scenario (scaled-out/serverless)
            FallbackCacheDuration = TimeSpan.FromMinutes(5),
            MaxCacheableContentSize = 10 * 1024 * 1024,
            CompressionThreshold = 1024
        }),
        sp.GetRequiredService<ILogger<HttpHybridCacheHandler>>()
    ));

var host = builder.Build();

var httpClientFactory = host.Services.GetRequiredService<IHttpClientFactory>();
var client = httpClientFactory.CreateClient("CachedClient");

var url = "https://httpbin.org/cache/60"; // Cache for 60 seconds

Console.WriteLine($"Request 1: GET {url}");
var sw = Stopwatch.StartNew();
var response1 = await client.GetAsync(url);
sw.Stop();
Console.WriteLine($"Status: {response1.StatusCode}, Time: {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Cache-Control: {response1.Headers.CacheControl}\n");

Console.WriteLine($"Request 2: GET {url} (should be cached)");
sw.Restart();
var response2 = await client.GetAsync(url);
sw.Stop();
Console.WriteLine($"Status: {response2.StatusCode}, Time: {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Age header: {response2.Headers.Age?.TotalSeconds ?? 0}s\n");

Console.WriteLine($"Request 3: GET {url} (should still be cached)");
sw.Restart();
var response3 = await client.GetAsync(url);
sw.Stop();
Console.WriteLine($"Status: {response3.StatusCode}, Time: {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Age header: {response3.Headers.Age?.TotalSeconds ?? 0}s\n");
