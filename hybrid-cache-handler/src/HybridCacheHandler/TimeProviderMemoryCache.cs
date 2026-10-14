// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

namespace DamianH.Http.HybridCacheHandler;

internal class TimeProviderMemoryCache : IMemoryCache
{
    private readonly MemoryCache _memoryCache;

    public TimeProviderMemoryCache(TimeProvider timeProvider, IOptions<TimeProviderMemoryCacheOptions> optionsAccessor)
    {
        var memoryCacheOptions = new MemoryCacheOptions
        {
            Clock = new TimeProviderSystemClock(timeProvider),
            CompactionPercentage = optionsAccessor.Value.CompactionPercentage,
            ExpirationScanFrequency = optionsAccessor.Value.ExpirationScanFrequency,
            SizeLimit = optionsAccessor.Value.SizeLimit,
            TrackLinkedCacheEntries = optionsAccessor.Value.TrackLinkedCacheEntries,
            TrackStatistics = optionsAccessor.Value.TrackStatistics
        };
        _memoryCache = new MemoryCache(Options.Create(memoryCacheOptions));
    }

    public void Dispose()
        => _memoryCache.Dispose();

    public bool TryGetValue(object key, out object? value)
        => _memoryCache.TryGetValue(key, out value);

    public ICacheEntry CreateEntry(object key)
        => _memoryCache.CreateEntry(key);

    public void Remove(object key)
        => _memoryCache.Remove(key);

    internal void Compact(double percentage)
        => _memoryCache.Compact(percentage);

    private class TimeProviderSystemClock(TimeProvider timeProvider) : ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
}
