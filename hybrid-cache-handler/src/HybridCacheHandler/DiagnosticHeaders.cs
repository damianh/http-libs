// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

/// <summary>
/// Provides constant values for HTTP diagnostic header names used to convey diagnostic or control information in HTTP
/// requests.
/// </summary>
public static class DiagnosticHeaders
{
    /// <summary>
    /// Header name for cache diagnostic information.
    /// </summary>
    public const string CacheDiagnostic = "X-Cache-Diagnostic";

    /// <summary>
    /// Header name for cache age information.
    /// </summary>
    public const string CacheAge = "X-Cache-Age";

    /// <summary>
    /// Header name for cache max-age information.
    /// </summary>
    public const string CacheMaxAge = "X-Cache-MaxAge";

    /// <summary>
    /// Header name for cache compression status.
    /// </summary>
    public const string CacheCompressed = "X-Cache-Compressed";

    /// <summary>
    /// Diagnostic value indicating the request was bypassed due to HTTP method.
    /// </summary>
    public const string ByPassMethod = "BYPASS-METHOD";

    /// <summary>
    /// Diagnostic value indicating the request was bypassed due to Cache-Control: no-store directive.
    /// </summary>
    public const string ByPassNoStore = "BYPASS-NO-STORE";

    /// <summary>
    /// Diagnostic value indicating a cache hit when only-if-cached was requested.
    /// </summary>
    public const string HitOnlyIfCached = "HIT-ONLY-IF-CACHED";

    /// <summary>
    /// Diagnostic value indicating a cache miss when only-if-cached was requested.
    /// </summary>
    public const string MissOnlyIfCached = "MISS-ONLY-IF-CACHED";

    /// <summary>
    /// Diagnostic value indicating a cache miss.
    /// </summary>
    public const string Miss = "MISS";

    /// <summary>
    /// Diagnostic value indicating a cache miss due to cache error.
    /// </summary>
    public const string MissCacheError = "MISS-CACHE-ERROR";

    /// <summary>
    /// Diagnostic value indicating a cache miss after revalidation.
    /// </summary>
    public const string MissRevalidated = "MISS-REVALIDATED";

    /// <summary>
    /// Diagnostic value indicating a cache hit after successful revalidation.
    /// </summary>
    public const string HitRevalidated = "HIT-REVALIDATED";

    /// <summary>
    /// Diagnostic value indicating a cache hit with fresh content.
    /// </summary>
    public const string HitFresh = "HIT-FRESH";

    /// <summary>
    /// Diagnostic value indicating a cache hit answered with a locally generated 304.
    /// </summary>
    public const string HitNotModified = "HIT-NOT-MODIFIED";

    /// <summary>
    /// Diagnostic value indicating a cache hit serving stale content during revalidation.
    /// </summary>
    public const string HitStaleWhileRevalidate = "HIT-STALE-WHILE-REVALIDATE";

    /// <summary>
    /// Diagnostic value indicating a cache hit serving stale content due to error.
    /// </summary>
    public const string HitStaleIfError = "HIT-STALE-IF-ERROR";
}
