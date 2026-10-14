// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

/// <summary>
/// Specifies the caching behavior mode.
/// </summary>
public enum CacheMode
{
    /// <summary>
    /// Private cache mode - behaves like a browser cache.
    /// Suitable for client applications, including scaled-out clients and serverless functions.
    /// Caches responses marked as private and respects max-age directives.
    /// </summary>
    Private,

    /// <summary>
    /// Shared cache mode - behaves like a proxy or CDN cache.
    /// Suitable for reverse proxies and API gateways (e.g., YARP).
    /// Does not cache responses marked as private and prefers s-maxage over max-age.
    /// </summary>
    Shared
}
