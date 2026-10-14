// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace DamianH.Http.HybridCacheHandler;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Cache read/write failure while processing request to {RequestUri}. Falling back to origin server.")]
    public static partial void CacheOperationFailed(this ILogger logger, Uri? requestUri, Exception exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Failed to write response to cache for {RequestUri}.")]
    public static partial void CacheWriteFailed(this ILogger logger, Uri? requestUri, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Debug,
        Message = "Failed to remove cache entry for {RequestUri}.")]
    public static partial void CacheRemoveFailed(this ILogger logger, Uri? requestUri, Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Background revalidation failed for {RequestUri}.")]
    public static partial void BackgroundRevalidationFailed(this ILogger logger, Uri? requestUri, Exception exception);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Cache write failed during background revalidation for {RequestUri}.")]
    public static partial void BackgroundCacheWriteFailed(this ILogger logger, Uri? requestUri, Exception exception);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Debug,
        Message = "Cache remove failed during background revalidation for {RequestUri}.")]
    public static partial void BackgroundCacheRemoveFailed(this ILogger logger, Uri? requestUri, Exception exception);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Cached content missing for key {ContentKey}, metadata is orphaned.")]
    public static partial void CachedContentMissing(this ILogger logger, string contentKey);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Debug,
        Message = "Failed to invalidate cache entries for {RequestUri}.")]
    public static partial void CacheInvalidationFailed(this ILogger logger, Uri? requestUri, Exception exception);
}
