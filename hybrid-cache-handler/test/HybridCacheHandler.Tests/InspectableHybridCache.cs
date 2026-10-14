// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Hybrid;

namespace DamianH.Http.HybridCacheHandler;

internal sealed class InspectableHybridCache : HybridCache
{
    private readonly ConcurrentDictionary<string, object?> _store = new(StringComparer.Ordinal);

    public CachedHttpEntry? GetMetadataEntry() =>
        _store.Values.OfType<CachedHttpEntry>().FirstOrDefault();

    public bool RemoveContentForVariant(Func<CachedHttpMetadata, bool> predicate)
    {
        var entry = GetMetadataEntry();
        var variant = entry?.Variants.FirstOrDefault(predicate);
        if (variant == null)
        {
            return false;
        }

        return _store.TryRemove(variant.ContentKey, out _);
    }

    public override async ValueTask<T> GetOrCreateAsync<TState, T>(
        string key,
        TState state,
        Func<TState, Ct, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        Ct cancellationToken = default)
    {
        if (_store.TryGetValue(key, out var stored) && stored is T value)
        {
            return value;
        }

        var created = await factory(state, cancellationToken);
        _store[key] = created;
        return created;
    }

    public override ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        Ct cancellationToken = default)
    {
        _store[key] = value;
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveAsync(string key, Ct cancellationToken = default)
    {
        _store.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveAsync(IEnumerable<string> keys, Ct cancellationToken = default)
    {
        foreach (var key in keys)
        {
            _store.TryRemove(key, out _);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask RemoveByTagAsync(string tag, Ct cancellationToken = default) =>
        ValueTask.CompletedTask;

    public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, Ct cancellationToken = default) =>
        ValueTask.CompletedTask;
}
