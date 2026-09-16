---
title: HTTP cache metrics
sidebarLabel: Metrics
description: System.Diagnostics.Metrics counters and HTTP tags emitted by HttpHybridCacheHandler.
order: 5
section: HTTP hybrid caching
---

# HTTP cache metrics

The handler emits counters via `System.Diagnostics.Metrics` under the meter
`DamianH.HttpHybridCacheHandler`:

| Counter | Description |
| --- | --- |
| `cache.hits` | Hits: fresh, revalidated, stale-while-revalidate, and stale-if-error |
| `cache.misses` | Misses, including cache errors and failed revalidations |
| `cache.stale` | Stale entries served via stale-while-revalidate or stale-if-error |
| `cache.size_exceeded` | Responses exceeding `MaxCacheableContentSize` that were not cached |

All counters include these tags, following
[OpenTelemetry semantic conventions](https://opentelemetry.io/docs/specs/semconv/http/http-metrics/):

| Tag | Description | Example |
| --- | --- | --- |
| `http.request.method` | HTTP method | `GET`, `HEAD` |
| `url.scheme` | URL scheme | `http`, `https` |
| `server.address` | Server hostname | `api.example.com` |
| `server.port` | Server port | `443` |

For per-response information, enable
[diagnostic headers](/docs/hybrid-cache-handler/cache-behavior/).
