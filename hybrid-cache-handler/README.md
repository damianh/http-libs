# DamianH.HttpHybridCacheHandler

[![NuGet](https://img.shields.io/nuget/v/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/)
[![Downloads](https://img.shields.io/nuget/dt/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/)

RFC 9111 client-side HTTP caching for `HttpClient`, backed by .NET `HybridCache`
for L1 memory and optional L2 distributed storage.

**[Full documentation](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/overview/)**
is the authoritative guide for configuration, behavior, metrics, and limitations.

## Frameworks

The handler and all five content-store packages ship `net10.0`,
`netstandard2.0`, and `net472` assets. Samples and the separate FileDistributedCache
product remain .NET 10. .NET Standard is a library target, not a runtime.
Older-target headers can be normalized by public enumeration. Framework apps
should enable automatic binding redirects and use `HttpClientHandler` with
GZip/Deflate decompression instead of the modern handler example below.

**Upstream support warning:** HybridCache 10.8.0 warns that `net472` is
unsupported/untested upstream. That warning is not suppressed; this repository's
coverage does not change Microsoft's support policy. Framework tests run on
the installed Windows runtime, commonly 4.8, not necessarily an actual 4.7.2 runtime.
See [framework compatibility](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/compatibility/).

## Install and start (.NET 10)

From your application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler
```

```csharp
services.AddHttpHybridCacheHandler();
services.AddHttpClient("CachedClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
```

## Important defaults and safety

- Default mode is `CacheMode.Private`, not shared proxy caching. Choose shared
  mode and authentication partitioning deliberately; response `Vary` is enforced.
- The default content limit is 10 MB. `FallbackCacheDuration` defaults to
  `TimeSpan.MinValue`: responses without caching headers are not cached by default.
- Use automatic decompression. See the
  [pipeline guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/pipeline/)
  for resilience and authentication ordering.
- Optional external body stores require explicit setup and operator-owned
  retention. Streaming requires `ResponseHeadersRead` and response consumption/
  disposal; early disposal or cancellation does not fill the cache. Concurrent
  streaming misses do not share a live origin stream. Do not eagerly delete
  shared bodies when invalidating one URI.
- Provision a trusted private staging parent, even when using the system temp
  default. On Windows the handler inherits, but does not restrict or validate,
  its ACLs; unique spool directories and leases are not access control.

## Guides

- [Options and cache modes](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/configuration/)
- [Behavior and diagnostics](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/cache-behavior/)
- [Content stores, ownership, staging, and migration](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/overview/)
- [Metrics](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/metrics/)
- [Performance and memory](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/performance/)
- [Benchmarks](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/benchmarks/)
- [RFC 9111 conformance](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/conformance/)
- [HttpClientFactory sample](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/samples/http-client-factory/)
- [YARP caching proxy sample](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/samples/yarp-caching-proxy/)
