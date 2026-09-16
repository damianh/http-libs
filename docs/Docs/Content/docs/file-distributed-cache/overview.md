---
title: "File distributed cache"
sidebarLabel: File cache overview
description: "Use local files for persistent IDistributedCache and IBufferDistributedCache storage in a single process."
order: 1
section: "File distributed cache"
topics:
  - caching
  - persistence
---

# File distributed cache

`DamianH.FileDistributedCache` is a file-based `IDistributedCache` and
`IBufferDistributedCache` implementation for .NET. Each cache entry is stored as
an individual file on the local filesystem, with no external infrastructure.

[NuGet package](https://www.nuget.org/packages/DamianH.FileDistributedCache/) ·
[Source](https://github.com/damianh/http-libs/tree/main/file-distributed-cache)

> **Local storage, not a shared distributed backend.** This cache is designed for
> single-process use. Multiple processes sharing one `CacheDirectory` are not
> supported. A multi-instance application needing a shared cache should use
> Redis, SQL Server, or another distributed backend.

## When to use it

| Scenario | FileDistributedCache | Redis / SQL Server |
| --- | --- | --- |
| Desktop apps, such as WPF, WinForms, or MAUI | Ideal for local disk storage without infrastructure | Often unnecessary infrastructure |
| Mobile apps, such as MAUI or Xamarin | Device-local storage suits the scenario; use a runtime compatible with the package | Not an on-device cache backend |
| CLI tools and background agents | Simple and self-contained | Often unnecessary infrastructure |
| Single-instance services | Persistent L2 without an external service | Either approach can work |
| Microservices / SOA with multiple instances | Not shared across instances | Use a shared backend |
| Scaled-out, load-balanced web apps | Each instance has its own cache | Use a shared cache |

The current package targets .NET 10; the scenario examples above are not a
compatibility guarantee for older framework versions.

FileDistributedCache can be the L2 backend for `HybridCache` when you need
persistence across process restarts but do not have, or do not want, external
cache infrastructure.

### Key characteristics

- **Zero infrastructure:** no Redis, SQL Server, or other external service.
- **Persistent across restarts:** cached entries survive process recycling,
  subject to expiration and the lifetime of the storage directory.
- **AOT compatible:** trimming and native AOT safe.
- **Buffer-based access:** implements `IBufferDistributedCache` for efficient
  `HybridCache` L2 integration without intermediate `byte[]` allocations.
- **Background eviction:** configurable periodic cleanup of expired entries,
  with optional soft limits on entry count and total size.
- **Sliding expiration:** reads update last-access timestamps for sliding windows.
- **Concurrent access within one process:** file-level locking and retry logic
  support concurrent access on Windows. This does not make sharing a directory
  between processes supported.

## Install

From your application's project directory:

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
    options.MaxEntries = 10_000;
    options.MaxTotalSize = 500 * 1024 * 1024; // 500 MiB
    options.EvictionInterval = TimeSpan.FromMinutes(5);
    options.DefaultAbsoluteExpiration = TimeSpan.FromHours(1);
});

using var provider = services.BuildServiceProvider();
var cache = provider.GetRequiredService<IDistributedCache>();

await cache.SetStringAsync("key", "value", new DistributedCacheEntryOptions
{
    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
    SlidingExpiration = TimeSpan.FromMinutes(10)
});

var value = await cache.GetStringAsync("key");
```

Use an application-specific writable directory and allow only one process to use
it at a time. A path such as `Path.Combine(AppContext.BaseDirectory, "my-cache")`
also works when the application directory is writable. For persistence, choose a
directory whose lifecycle you control rather than relying on the default system
temporary directory.

In this example, the entry has a 30-minute absolute lifetime and a 10-minute
sliding window; its explicit expiration settings take precedence over the
configured defaults. Entry count and size limits are **soft**, enforced during
background scans rather than as hard quotas on each write.

## Configuration and HTTP caching

See [Configuration and HybridCache integration](/docs/file-distributed-cache/configuration/)
for every option and default, eviction behavior, and a complete registration
example with [HttpHybridCacheHandler](/docs/hybrid-cache-handler/overview/).

The [FileDistributedCacheSample](https://github.com/damianh/http-libs/tree/main/file-distributed-cache/samples/FileDistributedCacheSample)
demonstrates HTTP caching across process restarts. To run it from the repository
root with the .NET 10 SDK:

```sh
dotnet run --project file-distributed-cache/samples/FileDistributedCacheSample
```

It calls `https://httpbin.org/cache/60`, so it needs network access on the initial
request. Restart within 60 seconds to observe a fresh response reused from disk.
