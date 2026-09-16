---
title: "File distributed cache configuration"
sidebarLabel: Configuration
description: "Configure local cache storage, expiration, eviction, and HybridCache integration."
order: 2
section: "File distributed cache"
topics:
  - caching
  - configuration
---

# File distributed cache configuration

Configure `FileDistributedCacheOptions` through `services.AddFileDistributedCache`.
See the [overview](/docs/file-distributed-cache/overview/) for installation and
direct `IDistributedCache` usage.

## Options and defaults

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `CacheDirectory` | `string` | `{TempPath}/DamianH.FileDistributedCache` | Directory where cache files are stored. |
| `MaxEntries` | `int?` | `null` (unlimited) | Soft limit on entry count; oldest entries by last access are evicted when exceeded. |
| `MaxTotalSize` | `long?` | `null` (unlimited) | Soft limit on total cached data size in bytes; oldest entries by last access are evicted when exceeded. |
| `EvictionInterval` | `TimeSpan` | 5 minutes | How frequently the background eviction scan runs. |
| `DefaultSlidingExpiration` | `TimeSpan?` | `null` | Default sliding expiration applied when an entry has no explicit sliding expiration. |
| `DefaultAbsoluteExpiration` | `TimeSpan?` | `null` | Default absolute expiration, relative to now, applied when an entry has no explicit expiration. |

Defaults are defined in
[`FileDistributedCacheOptions.cs`](https://github.com/damianh/http-libs/blob/main/file-distributed-cache/src/FileDistributedCache/FileDistributedCacheOptions.cs).

### Directory and process ownership

`CacheDirectory` defaults to
`Path.Combine(Path.GetTempPath(), "DamianH.FileDistributedCache")`. Temporary
storage may be cleaned independently of your application, so select a stable,
application-specific writable directory when persistence matters.

Only one process may use a cache directory at a time. Local filesystem storage
does not become a supported shared cache by pointing several processes at the
same directory. For shared multi-instance caching, use a distributed backend.

### Expiration and eviction

Per-entry `DistributedCacheEntryOptions` take precedence over the corresponding
defaults. An explicit sliding expiration also prevents the default absolute
expiration from being applied; it is not a global cap on every entry's lifetime.
With no per-entry expiration and both expiration defaults left at `null`, an
entry has no time-based expiration.

Reads update last-access timestamps to support sliding expiration. The
background scan removes expired entries and enforces configured entry-count and
size limits by evicting the oldest entries by last access. Limits are soft:
entries can exceed them between scans. Disposing the cache's service provider
also disposes the cache and stops its background eviction loop.

## Using with HttpHybridCacheHandler

FileDistributedCache is a drop-in L2 backend for .NET's `HybridCache`. Registering
the file cache provides the `IDistributedCache` / `IBufferDistributedCache`
services that `HybridCache` discovers for L2 storage.

Install [DamianH.HttpHybridCacheHandler](/docs/hybrid-cache-handler/overview/) as
well as FileDistributedCache in your application project:

```sh
dotnet add package DamianH.FileDistributedCache
dotnet add package DamianH.HttpHybridCacheHandler
```

The following example assumes a Generic Host application with
Microsoft.Extensions.Hosting and Microsoft.Extensions.Http available:

```csharp
using System.Net;
using DamianH.HttpHybridCacheHandler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFileDistributedCache(options =>
{
    options.CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyApp", "http-cache");
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

using var host = builder.Build();
using var client = host.Services.GetRequiredService<IHttpClientFactory>()
    .CreateClient("CachedClient");

using var response = await client.GetAsync("https://api.example.com/data");
```

Replace the example URL with your endpoint. A cacheable first response is fetched
from the network and stored in L1 (memory) and L2 (files). Subsequent requests can
use L1 or L2, including L2 after a process restart, while the cached response
remains usable under the handler's HTTP caching rules. Persistence alone does
not make every response cacheable or override HTTP expiration.

For a complete runnable example, see
[`FileDistributedCacheSample`](https://github.com/damianh/http-libs/tree/main/file-distributed-cache/samples/FileDistributedCacheSample)
and the [sample run instructions](/docs/file-distributed-cache/overview/#configuration-and-http-caching).
This backend is distinct from the handler's optional
[response content stores](/docs/hybrid-cache-handler/content-stores/overview/).
