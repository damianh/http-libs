// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

internal sealed class FreshnessCalculator(
    TimeProvider timeProvider,
    HttpHybridCacheHandlerOptions options)
{
    public bool IsFresh(CachedHttpMetadata cached, HttpRequestMessage request)
    {
        var freshnessLifetime = CalculateFreshnessLifetime(cached);
        if (freshnessLifetime == null)
        {
            return false;
        }

        var currentAge = CalculateCurrentAge(cached);
        var requestCacheControl = request.Headers.CacheControl;
        if (requestCacheControl?.MaxAge is TimeSpan requestMaxAge && currentAge > requestMaxAge)
        {
            return false;
        }

        var remainingFreshness = freshnessLifetime.Value - currentAge;
        var minFresh = requestCacheControl?.MinFresh;
        if (minFresh.HasValue && remainingFreshness < minFresh.Value)
        {
            return false;
        }

        if (currentAge < freshnessLifetime.Value)
        {
            return true;
        }

        if (requestCacheControl?.MaxStale == true &&
            !cached.MustRevalidate &&
            !cached.NoCache)
        {
            var staleness = currentAge - freshnessLifetime.Value;
            var maxStaleLimit = requestCacheControl.MaxStaleLimit ?? TimeSpan.MaxValue;
            return staleness <= maxStaleLimit;
        }

        return false;
    }

    public TimeSpan? CalculateFreshnessLifetime(CachedHttpMetadata cached)
    {
        if (cached.MaxAge.HasValue)
        {
            return cached.MaxAge.Value;
        }

        if (cached.Expires.HasValue)
        {
            var responseTime = cached.Date ?? cached.CachedAt;
            var lifetime = cached.Expires.Value - responseTime;
            return lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero;
        }

        if (cached.LastModified.HasValue)
        {
            var responseTime = cached.Date ?? cached.CachedAt;
            var timeSinceModified = responseTime - cached.LastModified.Value;
            if (timeSinceModified > TimeSpan.Zero)
            {
                var heuristicLifetime = TimeSpan.FromSeconds(timeSinceModified.TotalSeconds * options.HeuristicFreshnessPercent);
                return heuristicLifetime < options.HeuristicFreshnessMinimum
                    ? options.HeuristicFreshnessMinimum
                    : heuristicLifetime;
            }
        }

        return null;
    }

    public TimeSpan CalculateCurrentAge(CachedHttpMetadata cached)
    {
        var ageValue = cached.IgnoreStoredAge ? TimeSpan.Zero : cached.Age ?? TimeSpan.Zero;
        var apparentAge = TimeSpan.Zero;
        if (cached.Date.HasValue)
        {
            apparentAge = cached.CachedAt - cached.Date.Value;
            if (apparentAge < TimeSpan.Zero)
            {
                apparentAge = TimeSpan.Zero;
            }
        }

        var correctedReceivedAge = ageValue > apparentAge ? ageValue : apparentAge;
        var residentTime = timeProvider.GetUtcNow() - cached.CachedAt;
        return correctedReceivedAge + residentTime;
    }

    public TimeSpan CalculateStaleness(CachedHttpMetadata cachedResponse)
    {
        var freshnessLifetime = CalculateFreshnessLifetime(cachedResponse) ?? TimeSpan.Zero;
        var age = CalculateCurrentAge(cachedResponse);
        return age - freshnessLifetime;
    }

    public static bool CanServeStaleOnTransportFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex is HttpRequestException or IOException;
    }

    public static bool CanServeStaleOnError(CachedHttpMetadata cachedResponse, TimeSpan staleness)
    {
        if (cachedResponse.MustRevalidate || cachedResponse.ProxyRevalidate || cachedResponse.NoCache)
        {
            return false;
        }

        if (cachedResponse.HasSharedMaxAge)
        {
            return false;
        }

        if (cachedResponse.StaleIfError.HasValue)
        {
            return staleness <= cachedResponse.StaleIfError.Value;
        }

        return true;
    }

    public TimeSpan CalculateEntryLifetime(CachedHttpMetadata metadata)
    {
        var total = CalculateSemanticLifetime(metadata);
        if (total < TimeSpan.FromSeconds(30))
        {
            total = TimeSpan.FromSeconds(30);
        }

        return total;
    }

    public TimeSpan CalculateSemanticLifetime(CachedHttpMetadata metadata)
    {
        var freshness = CalculateFreshnessLifetime(metadata) ?? TimeSpan.Zero;
        var total = freshness;
        if (metadata.StaleWhileRevalidate.HasValue)
        {
            total += metadata.StaleWhileRevalidate.Value;
        }
        if (metadata.StaleIfError.HasValue)
        {
            total += metadata.StaleIfError.Value;
        }

        return total;
    }
}
