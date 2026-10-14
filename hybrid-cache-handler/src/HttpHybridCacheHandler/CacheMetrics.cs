// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DamianH.Http.HybridCacheHandler;

internal static class CacheMetrics
{
    private static readonly Meter Meter = new(
        "DamianH.Http.HybridCacheHandler",
        typeof(HttpHybridCacheHandler).Assembly.GetName().Version?.ToString() ?? "1.0.0");

    internal static readonly Counter<long> CacheHits = Meter.CreateCounter<long>(
        HttpHybridCacheHandler.CacheHitsCounterKey,
        description: "Number of cache hits");

    internal static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>(
        HttpHybridCacheHandler.CacheMissesCounterKey,
        description: "Number of cache misses");

    internal static readonly Counter<long> CacheStale = Meter.CreateCounter<long>(
        HttpHybridCacheHandler.CacheStaleCounterKey,
        description: "Number of stale cache entries served");

    internal static readonly Counter<long> CacheSizeExceeded = Meter.CreateCounter<long>(
        HttpHybridCacheHandler.CacheSizeExceededCounterKey,
        description: "Number of responses exceeding max cacheable size");

    internal static TagList CreateMetricTags(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        return new TagList
        {
            { "http.request.method", request.Method.Method },
            { "url.scheme", uri?.Scheme ?? "unknown" },
            { "server.address", uri?.Host ?? "unknown" },
            { "server.port", uri?.Port ?? 0 }
        };
    }
}
