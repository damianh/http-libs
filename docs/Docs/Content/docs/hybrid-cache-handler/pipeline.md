---
title: HTTP cache handler pipeline
sidebarLabel: Handler pipeline
description: Configure decompression, connection pooling, resilience, authentication, and handler ordering.
order: 2
section: HTTP hybrid caching
---

# HTTP cache handler pipeline

## Recommended setup

On .NET 10, prefer `SocketsHttpHandler` with automatic decompression for
explicit DNS refresh and connection-pool lifetime control:

```csharp
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
})
```

On .NET Framework 4.7.2, use `HttpClientHandler`:

```csharp
services.AddHttpClient("MyClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression =
            DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
```

This example also uses APIs available to .NET Standard 2.0 consumers. Choose the
primary handler for the application's actual runtime; .NET Standard is not a
runtime. Framework's `HttpClientHandler` does not expose
`PooledConnectionLifetime`, `PooledConnectionIdleTimeout`, or `ConnectTimeout`.
Do not copy those options or `DecompressionMethods.All` into Framework-targeted
code. See [framework compatibility](/docs/hybrid-cache-handler/compatibility/)
for dependency support and header-representation differences.

## AutomaticDecompression explained

There are two different compressions:

1. **Transport compression** (server to client):
   - Controlled by `AutomaticDecompression` on the primary HTTP handler.
   - Reduces network bandwidth.
   - The cache handler receives **decompressed** content.
2. **Cache storage compression**:
   - Controlled by `CompressionThreshold` in the caching options.
   - Reduces cache storage size.
   - Content is compressed before storage.

Example flow (illustrative sizes, not benchmark results):

```text
Server sends: gzipped 512 bytes
    ↓
SocketsHttpHandler: auto-decompresses → 2048 bytes
    ↓
HttpHybridCacheHandler: receives decompressed content
    ↓
Storage compression: compresses → 600 bytes
    ↓
Cache: stores 600 bytes (no Base64 overhead!)
```

The cache handler can inspect and validate response content; Cache-Control, ETag,
and Last-Modified headers are readable for caching decisions. Storage compression
is optional and configurable.

## Handler ordering

The basic pipeline is:

```text
HttpClient → [Outer Handlers] → HttpHybridCacheHandler → SocketsHttpHandler → Network
```

### With Polly resilience

For a cache hit to bypass resilience, register caching before the resilience
handler, so caching is outer and resilience is inner:

```csharp
.AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>())
.AddStandardResilienceHandler(options =>
{
    options.Retry.MaxRetryAttempts = 3;
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
});
```

The request order is cache, then Polly, then `SocketsHttpHandler`. Cache hits use
the fast path without invoking Polly; cache misses with network failures can be
retried by Polly. This is the recommended production ordering for that behavior.

### With authentication

```csharp
.AddHttpMessageHandler(() => new AuthenticationHandler())
.AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
```

To include authentication headers in request-key partitioning, configure
`VaryHeaders` in the `AddHttpHybridCacheHandler` options.

Authentication is applied before caching. Configured `VaryHeaders` partition the
request key, and response `Vary` is always enforced on cache hits.

## Common mistakes

**Not enabling automatic decompression:**

```csharp
new SocketsHttpHandler() // AutomaticDecompression defaults to None
```

The cache handler receives compressed content instead of the intended
ready-to-use representation. Explicitly enable decompression:

```csharp
new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All
}
```

**Choosing a handler unavailable on the application target:**

`HttpClientHandler` with GZip/Deflate decompression is the Framework-compatible
choice. `SocketsHttpHandler` is recommended for .NET 10 applications; the
connection-pooling and `DecompressionMethods.All` examples above are modern-only.

**Putting caching inside Polly when hits should bypass Polly:**

```csharp
.AddStandardResilienceHandler() // Outer
.AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>()) // Inner
```

Instead:

```csharp
.AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>()) // Outer
.AddStandardResilienceHandler() // Inner
```

**Golden rule:** `HttpHybridCacheHandler` should receive **decompressed,
ready-to-use** content.
