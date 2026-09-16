---
title: HttpClientFactory caching sample
sidebarLabel: HttpClientFactory
description: Run the named HttpClient sample and inspect its HybridCache registration, fallback, decompression, and cache-hit timings.
order: 1
section: HTTP caching samples
---

# HttpClientFactory caching sample

This sample demonstrates `HttpHybridCacheHandler` with `IHttpClientFactory` and
Microsoft dependency injection, the same registration pattern used in ASP.NET Core.

[Sample source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/samples/HttpClientFactorySample) |
[Program.cs](https://github.com/damianh/http-libs/blob/main/hybrid-cache-handler/samples/HttpClientFactorySample/Program.cs)

## Overview

The sample shows:

- Registering HybridCache and the caching handler through dependency injection.
- Configuring a named `HttpClient` with the handler.
- Making requests that benefit from client-side caching.
- Observing cache hits through timing differences.

## Run the sample

From the repository root:

```powershell
dotnet run --project hybrid-cache-handler\samples\HttpClientFactorySample\HttpClientFactorySample.csproj
```

The console application makes three identical requests to
`https://httpbin.org/cache/60` and waits for a key before exiting. The intended flow:

1. First request: full round-trip to the origin.
2. Second request: served from cache, normally faster.
3. Third request: still served from cache.

The sample prints elapsed time, status, the first response's Cache-Control, and
later responses' Age headers. These are demonstration timings, not benchmarks;
origin availability and cache directives determine the actual results.

The former README referred to the GitHub API; the current program uses httpbin.

## Configuration

- **FallbackCacheDuration**: Explicitly set to five minutes for responses without
  caching headers.
- **MaxCacheableContentSize**: Uses the default 10 MB maximum.
- **CompressionThreshold**: 1024 bytes.
- **Transport**: `SocketsHttpHandler` with automatic decompression, a five-minute
  pooled connection lifetime, and a two-minute idle timeout.

The former README's `HybridCacheHttpHandler`, `HybridCacheHttpHandlerOptions`,
and `DefaultCacheDuration` names are outdated. Current names are
`HttpHybridCacheHandler`, `HttpHybridCacheHandlerOptions`, and
`FallbackCacheDuration`.

## Key code

The current sample registers the handler through its service helper:

```csharp
builder.Services.AddHttpHybridCacheHandler(options =>
{
    options.FallbackCacheDuration = TimeSpan.FromMinutes(5);
    options.CompressionThreshold = 1024;
});

builder.Services.AddHttpClient("CachedClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
```

See the source for imports and host setup.

## Learn more

- [HTTP cache configuration](/docs/hybrid-cache-handler/configuration/)
- [Handler pipeline](/docs/hybrid-cache-handler/pipeline/)
- [IHttpClientFactory documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory)
- [HybridCache documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)
