---
title: HTTP cache behavior and diagnostics
sidebarLabel: Cache behavior
description: Cacheable responses, request keys, response variants, revalidation, and diagnostic headers.
order: 4
section: HTTP hybrid caching
---

# HTTP cache behavior and diagnostics

## Diagnostic headers

When `IncludeDiagnosticHeaders` is enabled, the handler adds:

- **X-Cache-Diagnostic**:
  - `HIT-FRESH`: Served from cache, content is fresh.
  - `HIT-REVALIDATED`: Served after successful 304 revalidation.
  - `HIT-STALE-WHILE-REVALIDATE`: Served stale during background revalidation.
  - `HIT-STALE-IF-ERROR`: Served stale due to backend error.
  - `HIT-ONLY-IF-CACHED`: Served from cache with `only-if-cached`.
  - `MISS`: Not cached; fetched from backend.
  - `MISS-REVALIDATED`: Entry was stale and the resource changed.
  - `MISS-CACHE-ERROR`: Cache operation failed and was bypassed.
  - `MISS-ONLY-IF-CACHED`: Not cached with `only-if-cached` (504 Gateway Timeout).
  - `BYPASS-METHOD`: Method is not cacheable (POST, PUT, etc.).
  - `BYPASS-NO-STORE`: Request has `no-store`.
- **X-Cache-Age**: Cached content age in seconds, only on cache hits.
- **X-Cache-MaxAge**: Cached content maximum age in seconds, only on cache hits.
- **X-Cache-Compressed**: `"true"` if stored compressed, only on cache hits.

```csharp
var options = new HttpHybridCacheHandlerOptions
{
    IncludeDiagnosticHeaders = true
};
```

## Cacheable responses

Only GET and HEAD requests are cached. Responses are cached when:

- The response carries freshness information: `max-age`, an `Expires` date, or a
  `Last-Modified` date. With only `Last-Modified`, heuristic freshness applies
  and the status must be in RFC 9111's heuristically cacheable set (`200`,
  `203`, `204`, `206`, `300`, `301`, `308`, `404`, `405`, `410`, `414`, `501`)
  or the response must be marked `public`. A configured `FallbackCacheDuration`
  caches responses carrying none of these, at any status.
- Cache-Control allows caching: not `no-store`, nor `no-cache` without validation.
- Content size is within `MaxCacheableContentSize`.

Also see [content-type filters and cache modes](/docs/hybrid-cache-handler/configuration/).

## Cache key generation

Keys are generated from:

- HTTP method.
- Request URI.
- Optional configured `VaryHeaders` request values.

On reads, the handler enforces stored response `Vary` values and selects a matching
variant.

The default key is method plus URI only. When cache storage is shared across users
or tenants, list an identity-bearing header in `VaryHeaders`; see
[cache modes](/docs/hybrid-cache-handler/configuration/#cachemode.private-default).

## Conditional requests

When serving stale content, the handler automatically adds:

- `If-None-Match` with the cached ETag.
- `If-Modified-Since` with the cached Last-Modified date.

A 304 Not Modified refreshes and serves the cached response.

See the [directive and invalidation overview](/docs/hybrid-cache-handler/overview/)
and [streaming and retention limitations](/docs/hybrid-cache-handler/content-stores/overview/).
