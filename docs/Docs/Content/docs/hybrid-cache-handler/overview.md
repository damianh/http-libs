---
title: HTTP hybrid caching overview
sidebarLabel: Caching overview
description: RFC 9111 client-side HTTP caching for HttpClient with HybridCache memory and distributed storage.
order: 1
section: HTTP hybrid caching
topics:
  - HttpClient
  - HybridCache
  - HTTP caching
---

# HTTP hybrid caching overview

`DamianH.HttpHybridCacheHandler` provides RFC 9111 compliant client-side HTTP caching
for `HttpClient`, powered by .NET's `HybridCache` for efficient L1 (memory) and L2
(distributed) caching.

[NuGet package](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/) |
[Source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler)

## Installation

Run in the application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler
```

## Frameworks

The handler and all five content-store packages ship `net10.0`,
`netstandard2.0`, and `net472` assets. Samples still require .NET 10;
FileDistributedCache remains a separate .NET 10 product.
See [framework compatibility](/docs/hybrid-cache-handler/compatibility/) for
header normalization, dependency support warnings, and validation coverage.
HybridCache 10.8.0 warns that `net472` is unsupported/untested upstream; the
library's compatibility tests do not change that support policy.

## Quick start (.NET 10)

```csharp
var services = new ServiceCollection();

services.AddHttpHybridCacheHandler(options =>
{
    options.FallbackCacheDuration = TimeSpan.FromMinutes(5);
    options.MaxCacheableContentSize = 10 * 1024 * 1024; // 10MB
    options.CompressionThreshold = 1024; // Compress cached content >1KB
});

services.AddHttpClient("MyClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Enable automatic decompression - server compression handled transparently
        AutomaticDecompression = DecompressionMethods.All,
        // DNS refresh every 5 minutes - critical for cloud/microservices
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // Close idle connections after 2 minutes
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());

using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("MyClient");
using var response = await client.GetAsync("https://api.example.com/data");
```

The default mode is a private, browser-like cache, not a shared proxy cache. The
default `FallbackCacheDuration` is `TimeSpan.MinValue`: the five-minute fallback
above is an explicit choice, not the default. Configure authentication partitioning
and proxy behavior deliberately; see [configuration](/docs/hybrid-cache-handler/configuration/)
and [handler ordering](/docs/hybrid-cache-handler/pipeline/).
For .NET Framework, use the `HttpClientHandler` example in that pipeline guide.

## Features

### Core caching capabilities

- **RFC 9111 compliant**: Implements HTTP caching semantics for client-side caching.
  The [conformance suite](/docs/hybrid-cache-handler/conformance/) documents how behavior
  is tested and why passing every optional suite case is not a goal.
- **HybridCache integration**: L1 memory and L2 distributed caching.
- **Transparent operation**: Works with existing `HttpClient` code.

### Cache-Control directives

**Request directives:**

- `max-age`: Control maximum acceptable response age.
- `max-stale`: Accept stale responses within specified staleness tolerance.
- `min-fresh`: Require responses to remain fresh for a specified duration.
- `no-cache`: Force revalidation with the origin server.
- `no-store`: Bypass cache completely.
- `only-if-cached`: Return cached responses or 504 if not cached.

**Response directives:**

- `max-age`: Define response freshness lifetime.
- `no-cache`: Store but require validation before use.
- `no-store`: Prevent caching.
- `public`/`private`: Control cache visibility.
- `must-revalidate`: Enforce validation when stale.

### Advanced features

- **Conditional requests**: Automatic ETag (`If-None-Match`) and Last-Modified
  (`If-Modified-Since`) validation.
- **Vary support**: Response-driven `Vary` matching with multiple stored variants per resource.
- **Unsafe method invalidation**: Invalidates cached GET/HEAD entries for the target URI
  and same-origin `Location`/`Content-Location` after successful unsafe requests such as
  POST, PUT, or DELETE.
- **Freshness calculation**: `Expires`, `Age`, and Last-Modified-based heuristic freshness.
- **Stale handling**: `stale-while-revalidate` serves stale content during background
  updates; `stale-if-error` serves stale content when the origin is unavailable.
- **Configurable limits**: Per-item content size limits, default 10 MB.
- **Optional large content store**: Separately versioned Azure Blob, Amazon S3, Google
  Cloud Storage, and filesystem adapters.
- **Metrics**: Hit/miss and cache-operation counters via `System.Diagnostics.Metrics`.
- **Custom cache keys**: Extensible key generation.
- **Request collapsing**: The default buffered path coalesces requests through
  HybridCache. Opt-in streaming fills use independent origin streams until an entry
  is published.

See [cache behavior](/docs/hybrid-cache-handler/cache-behavior/),
[metrics](/docs/hybrid-cache-handler/metrics/), and
[content-store contracts and offload](/docs/hybrid-cache-handler/content-stores/overview/).

## Samples

Complete examples remain in the repository:

- [HttpClientFactory guide](/docs/hybrid-cache-handler/samples/http-client-factory/) and
  [source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/samples/HttpClientFactorySample):
  integration with `IHttpClientFactory`.
- [YARP caching proxy guide](/docs/hybrid-cache-handler/samples/yarp-caching-proxy/) and
  [source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/samples/YarpCachingProxySample):
  a caching reverse proxy.
- [FusionCache sample](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/samples/FusionCacheSample):
  FusionCache through its HybridCache adapter for enhanced caching features.
- [FileDistributedCache sample](https://github.com/damianh/http-libs/tree/main/file-distributed-cache/samples/FileDistributedCacheSample):
  file-based L2 caching with the handler. Read the
  [single-instance filesystem limitations](/docs/file-distributed-cache/overview/).
