---
title: YARP caching proxy sample
sidebarLabel: YARP proxy
description: Run the direct-forwarding YARP example, understand shared-cache configuration, and account for sample routing limitations.
order: 2
section: HTTP caching samples
---

# YARP caching proxy sample

This sample demonstrates a caching reverse proxy using YARP (Yet Another Reverse
Proxy) and `HttpHybridCacheHandler`.

[Sample source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/samples/YarpCachingProxySample) |
[Program.cs](https://github.com/damianh/http-libs/blob/main/hybrid-cache-handler/samples/YarpCachingProxySample/Program.cs)

## Overview

The sample shows:

- Building a reverse proxy that can cache upstream responses.
- Using YARP's `IHttpForwarder` with a custom HTTP client.
- Reducing upstream load through caching.
- Respecting HTTP cache-control directives with RFC 9111 shared-cache behavior
  (the former README referred to the older RFC 7234).

## Run the sample

From the repository root:

```powershell
dotnet run --project hybrid-cache-handler\samples\YarpCachingProxySample\YarpCachingProxySample.csproj --no-launch-profile -- --urls http://localhost:5000
```

This explicitly binds to the documented HTTP port rather than the checked-in
launch profile's `https://localhost:56592` and `http://localhost:56593`.
From another terminal:

```powershell
# First request - forwards toward GitHub
curl http://localhost:5000/api/repos/dotnet/runtime

# Second request - a cache hit only if the first response was cacheable
curl http://localhost:5000/api/repos/dotnet/runtime
```

### Sample limitations

The current program uses direct `IHttpForwarder.SendAsync`. It does not load the
`ReverseProxy` configuration in `appsettings.json` or apply that file's
`PathRemovePrefix` transform. Consequently, `/api` is forwarded as part of the
upstream path; the example requests are **not guaranteed to reach the intended
GitHub API resource or demonstrate cache hits**. GitHub's request requirements and
the upstream response's cacheability also matter. This documentation migration
does not change the sample implementation.

The sample uses `HttpClientHandler` without explicitly enabling automatic
decompression. For production, follow the
[recommended handler pipeline](/docs/hybrid-cache-handler/pipeline/) and verify
forwarding paths, authentication, and shared-cache policy rather than treating the
sample as a production-ready gateway.

## Configuration

- **Mode**: `CacheMode.Shared`.
- **FallbackCacheDuration**: Ten minutes for responses without caching headers.
- **MaxCacheableContentSize**: 50 MB.

Shared mode does not cache `Cache-Control: private` and prefers `s-maxage` over
`max-age`. Authenticated responses need explicit shared-cache permission; see
[cache modes](/docs/hybrid-cache-handler/configuration/).

The former README's `HybridCacheHttpHandler`, `HybridCacheHttpHandlerOptions`,
and `DefaultCacheDuration` names are outdated. The current API names appear below.

## Architecture

```text
Client → YARP Proxy (with HttpHybridCacheHandler) → GitHub API
                ↓
         HybridCache (L1: Memory, L2: Optional Distributed)
```

## Key code

```csharp
builder.Services.AddHybridCache();
builder.Services.AddHttpClient("YarpCachingClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler())
    .AddHttpMessageHandler(sp => new HttpHybridCacheHandler(
        sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(),
        TimeProvider.System,
        Options.Create(new HttpHybridCacheHandlerOptions
        {
            Mode = CacheMode.Shared,
            FallbackCacheDuration = TimeSpan.FromMinutes(10),
            MaxCacheableContentSize = 50 * 1024 * 1024
        }),
        sp.GetRequiredService<ILogger<HttpHybridCacheHandler>>()
    ));
```

This reproduces the current sample registration, not the recommended production
transport configuration. See the source for imports and forwarding setup.

## Use cases

This pattern is useful for:

- API gateways that want to reduce upstream load.
- Development proxies that cache external API responses.
- Edge caching layers for microservices.
- Reducing costs from metered APIs.

## Learn more

- [HTTP caching overview](/docs/hybrid-cache-handler/overview/)
- [YARP documentation](https://microsoft.github.io/reverse-proxy/)
- [HybridCache documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid)
