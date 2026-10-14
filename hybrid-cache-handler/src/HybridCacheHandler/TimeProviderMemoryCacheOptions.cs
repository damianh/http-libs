// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

internal class TimeProviderMemoryCacheOptions
{
    public double CompactionPercentage { get; set; }

    public TimeSpan ExpirationScanFrequency { get; set; }

    public long? SizeLimit { get; set; }

    public bool TrackLinkedCacheEntries { get; set; }

    public bool TrackStatistics { get; set; }
}
