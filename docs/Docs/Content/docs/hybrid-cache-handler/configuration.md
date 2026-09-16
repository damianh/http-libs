---
title: HTTP cache configuration
sidebarLabel: Configuration
description: Private and shared cache modes, option defaults, content filters, and large-body routing.
order: 3
section: HTTP hybrid caching
---

# HTTP cache configuration

## Cache modes

The library supports two modes following RFC 9111 semantics.

### CacheMode.Private (default)

Browser-like behavior suitable for client applications:

- `HttpClient` in web applications, APIs, and background services.
- Scaled-out clients sharing cache across multiple instances or serverless/Lambda.
- Per-user/per-tenant caching scenarios when `VaryHeaders` includes an
  identity-bearing header.

Behavior:

- Caches responses with `Cache-Control: private`.
- Uses `max-age`, ignoring `s-maxage`.
- Caches authenticated requests if marked `private` or `max-age`.
- **When cache storage is shared across users or tenants, include an
  identity-bearing header, such as `Authorization` or a tenant/user header, in
  `VaryHeaders`.** The default request key is method plus URI only; without
  that partition, cached authenticated responses can be disclosed to another
  user or tenant.
- Request keys can be client-specific when `VaryHeaders` is configured.
- Response variants are always matched using stored `Vary` values.

```csharp
new HttpHybridCacheHandlerOptions
{
    Mode = CacheMode.Private, // Shares cache across app instances via Redis L2
    FallbackCacheDuration = TimeSpan.FromMinutes(5)
}
```

### CacheMode.Shared

Proxy/CDN-like behavior suitable for reverse proxies such as YARP or Envoy,
API gateways, and edge caches/CDN-like scenarios.

Behavior:

- Does **not** cache responses with `Cache-Control: private`.
- Prefers `s-maxage` over `max-age`.
- Authenticated responses are cacheable only with explicit shared-cache permissions:
  `public`, `s-maxage`, or `must-revalidate`.
- Supports targeted cache-control headers, for example `CDN-Cache-Control`, via
  `TargetedCacheControlHeaderNames`.
- Cache is shared across all clients/users.

```csharp
new HttpHybridCacheHandlerOptions
{
    Mode = CacheMode.Shared, // RFC 9111 shared cache semantics
    MaxCacheableContentSize = 50 * 1024 * 1024 // 50MB
}
```

## HttpHybridCacheHandlerOptions

- **Mode**: Cache mode (default `CacheMode.Private`). Use `CacheMode.Shared` for
  proxy/CDN scenarios.
- **HeuristicFreshnessPercent**: Heuristic freshness percentage for responses with
  Last-Modified but no explicit freshness (default 0.1, or 10%).
- **HeuristicFreshnessMinimum**: Minimum heuristic freshness lifetime when
  Last-Modified exists but explicit freshness is absent (default 30 seconds).
- **VaryHeaders**: Headers included in Vary-aware request keys (default none, `[]`;
  response `Vary` matching is still enforced).
- **TargetedCacheControlHeaderNames**: Response headers carrying targeted
  shared-cache directives (default `CDN-Cache-Control`). Applied only in shared mode.
- **MaxCacheableContentSize**: Maximum cacheable response content size (default
  10 MB). Larger responses are not cached.
- **FallbackCacheDuration**: Fallback for responses without explicit caching headers
  (default `TimeSpan.MinValue`, meaning responses without caching headers are not cached).
- **CompressionThreshold**: Minimum content size for compression (default 1024 bytes).
  Set to zero or a negative value to disable compression.
- **LargeContentThreshold**: Threshold for routing content to an optional
  `ILargeHttpCacheContentStore` (default 1 MiB). Zero or negative always uses
  HybridCache content storage.
- **CompressibleContentTypes**: Eligible compression types (default `text/*`,
  `application/json`, `application/json+*`, `application/xml`,
  `application/javascript`, `image/svg+xml`).
- **CacheableContentTypes**: Eligible caching types (default `text/*`,
  `application/json`, `application/json+*`, `application/xml`,
  `application/javascript`, `application/xhtml+xml`, `image/*`).
- **ContentKeyPrefix**: Content cache-key prefix (default `"httpcache:content:"`).
  Content is stored separately from metadata to avoid Base64 overhead.
- **IncludeDiagnosticHeaders**: Adds diagnostic response headers such as
  `X-Cache-Diagnostic` (default `false`).

`LargeContentThreshold` uses the original response-body length before internal
storage compression. `MaxCacheableContentSize` still determines whether the body
can be cached at all. Without an external store, the HybridCache-only path remains
available. Metadata and HTTP freshness remain in HybridCache.

For staging options (`SpoolMemoryThreshold`, `MaxSpoolDiskBytes`,
`MaxConcurrentDiskSpools`, and `SpoolDirectory`), ownership, and retention, see
[content-store contracts and offload](/docs/hybrid-cache-handler/content-stores/overview/).
For emitted headers, see [cache diagnostics](/docs/hybrid-cache-handler/cache-behavior/).
