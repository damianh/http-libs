// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

public class InspectableHybridCacheTests
{
    [Fact]
    public async Task GetOrCreateAsync_treats_type_mismatch_as_cache_miss()
    {
        var cache = new InspectableHybridCache();
        await cache.SetAsync("key", "cached-string");
        var factoryCalls = 0;

        var value = await cache.GetOrCreateAsync(
            "key",
            0,
            (state, ct) =>
            {
                factoryCalls++;
                return ValueTask.FromResult(123);
            });

        value.ShouldBe(123);
        factoryCalls.ShouldBe(1);
    }
}
