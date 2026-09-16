// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Caching.Hybrid;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>
/// HybridCache-backed store for cached HTTP response content.
/// </summary>
internal sealed class ContentCache(HybridCache cache) : IHttpCacheContentStore
{
    public async ValueTask WriteAsync(
        string contentKey,
        Stream content,
        long contentLength,
        IEnumerable<string>? tags,
        Ct ct)
    {
        var payload = new byte[checked((int)contentLength)];
        await content.ReadExactlyAsync(payload, ct);

        // Store content as byte[] in HybridCache.
        await cache.SetAsync(contentKey, payload, tags: tags, cancellationToken: ct);
    }

    public async ValueTask<Stream?> OpenReadAsync(string contentKey, Ct ct)
    {
        var payload = await cache.GetOrCreateAsync<byte[]?>(
            contentKey,
            _ => new ValueTask<byte[]?>((byte[]?)null),
            cancellationToken: ct
        );
        return payload == null ? null : new MemoryStream(payload, writable: false);
    }

    public async ValueTask RemoveAsync(string contentKey, Ct ct) =>
        await cache.RemoveAsync(contentKey, ct);
}
