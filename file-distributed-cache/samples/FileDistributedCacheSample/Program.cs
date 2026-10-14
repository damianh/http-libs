// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using DamianH.Http.HybridCacheHandler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The cache directory persists across process restarts so that HTTP responses
// cached in one run are served from disk on subsequent runs without a network request.
var cacheDir = Path.Combine(AppContext.BaseDirectory, "http-cache");

Console.WriteLine("FileDistributedCache + HttpHybridCacheHandler Sample");
Console.WriteLine("=====================================================");
Console.WriteLine($"Cache directory: {cacheDir}");
Console.WriteLine();

var builder = Host.CreateApplicationBuilder(args);

// Register the file-based L2 distributed cache.
// HybridCache automatically uses it as its L2 backend.
builder.Services.AddFileDistributedCache(options =>
{
    options.CacheDirectory = cacheDir;
    options.DefaultAbsoluteExpiration = TimeSpan.FromHours(1);
});

builder.Services.AddHttpHybridCacheHandler(options =>
{
    options.FallbackCacheDuration = TimeSpan.FromMinutes(5);
});

builder.Services
    .AddHttpClient("CachedClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());

var host = builder.Build();

var httpClientFactory = host.Services.GetRequiredService<IHttpClientFactory>();
var client = httpClientFactory.CreateClient("CachedClient");

// Cache for 60 seconds at the HTTP level.
var url = "https://httpbin.org/cache/60";

Console.WriteLine("First run: request will be fetched from the network and stored in the file cache.");
Console.WriteLine("Subsequent runs (within 60 s): response is served from disk — no network request.");
Console.WriteLine();

Console.WriteLine($"GET {url}");
var sw = Stopwatch.StartNew();
var response = await client.GetAsync(url);
sw.Stop();
Console.WriteLine($"  Status : {response.StatusCode}");
Console.WriteLine($"  Elapsed: {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"  Age    : {response.Headers.Age?.TotalSeconds ?? 0} s");
Console.WriteLine();

Console.WriteLine($"GET {url} (second request — should hit L1 or L2 cache)");
sw.Restart();
var response2 = await client.GetAsync(url);
sw.Stop();
Console.WriteLine($"  Status : {response2.StatusCode}");
Console.WriteLine($"  Elapsed: {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"  Age    : {response2.Headers.Age?.TotalSeconds ?? 0} s");
Console.WriteLine();

Console.WriteLine("Cache files on disk:");
if (Directory.Exists(cacheDir))
{
    foreach (var file in Directory.GetFiles(cacheDir, "*.cache"))
    {
        var info = new FileInfo(file);
        Console.WriteLine($"  {Path.GetFileName(file)}  ({info.Length:N0} bytes)");
    }
}
else
{
    Console.WriteLine("  (none)");
}

Console.WriteLine();
Console.WriteLine("Restart the sample within 60 s to observe the second request served from the file cache.");
