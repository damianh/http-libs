# DamianH.FileDistributedCache

[![NuGet](https://img.shields.io/nuget/v/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/)
[![Downloads](https://img.shields.io/nuget/dt/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/)

A local-file `IDistributedCache` / `IBufferDistributedCache` implementation for .NET,
providing persistent caching without external infrastructure.

**[Documentation](https://damianh.github.io/http-libs/docs/file-distributed-cache/overview/)** ·
[Configuration and HybridCache integration](https://damianh.github.io/http-libs/docs/file-distributed-cache/configuration/)

> [!IMPORTANT]
> This is a local-filesystem cache for a single process, not a shared distributed
> backend. Multiple processes sharing one cache directory are unsupported. Use
> Redis, SQL Server, or another shared backend for scaled-out applications.

## Installation

```sh
dotnet add package DamianH.FileDistributedCache
```

## Quick start

In an application using Microsoft.Extensions.DependencyInjection:

```csharp
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddFileDistributedCache(options =>
{
    options.CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyApp", "my-cache");
    options.DefaultAbsoluteExpiration = TimeSpan.FromHours(1);
});

using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IDistributedCache>();
await cache.SetStringAsync("key", "value");
var value = await cache.GetStringAsync("key");
```

Choose an application-specific writable directory, used by only one process at a
time. See the [full guide](https://damianh.github.io/http-libs/docs/file-distributed-cache/overview/)
for suitability and per-entry expiration, and the
[working sample](https://github.com/damianh/http-libs/tree/main/file-distributed-cache/samples/FileDistributedCacheSample)
for use with HttpHybridCacheHandler.
