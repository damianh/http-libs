// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Hybrid;

namespace DamianH.HttpHybridCacheHandler;

public partial class HttpHybridCacheHandler
{
    private static readonly ConditionalWeakTable<HybridCache, PublicationCoordinator> PublicationCoordinators = new();
    private readonly AsyncLocal<FillContext?> _fillContext = new();
    private readonly CancellationTokenSource _backgroundLifetime = new();
    private int _disposed;
    private bool StreamingEnabled => _largeContentStore != null && _options.LargeContentThreshold > 0;
    private PublicationCoordinator GetPublicationCoordinator() => PublicationCoordinators.GetValue(_cache, _ => new());

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _backgroundLifetime.Cancel();
            _backgroundLifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task RemoveStreamingAwareAsync(string cacheKey, string? uriTag, Ct ct)
    {
        if (!StreamingEnabled)
        {
            await _cache.RemoveAsync(cacheKey, ct);
            return;
        }
        using var invalidationScope = GetPublicationCoordinator().Capture(uriTag);
        var stripe = invalidationScope.Stripe;
        await stripe.Gate.WaitAsync(ct);
        try
        {
            Interlocked.Increment(ref stripe.Epoch);
            await _cache.RemoveAsync(cacheKey, ct);
        }
        finally
        {
            stripe.Gate.Release();
        }
    }

    private void StartBackgroundRevalidation(CachedHttpEntry entry, CachedHttpMetadata metadata,
        HttpRequestMessage request, string cacheKey)
    {
        var cancellationToken = _backgroundLifetime.Token;
        var snapshot = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
#if NET10_0_OR_GREATER
            VersionPolicy = request.VersionPolicy
#endif
        };
        foreach (var header in request.Headers)
        {
            snapshot.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        _ = Task.Run(async () =>
        {
            using (snapshot)
            {
                await BackgroundRevalidateAsync(entry, metadata, snapshot, cacheKey, cancellationToken);
            }
        });
    }

    internal static bool IsExpectedCacheFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or HttpRequestException;

    private bool IsAuthorizedResponseCacheable(HttpResponseMessage response, HttpRequestMessage request)
    {
        if (request.Headers.Authorization == null)
        {
            return true;
        }
        var directives = GetEffectiveCacheDirectives(response);
        return _options.Mode == CacheMode.Shared
            ? directives.Public || directives.HasSharedMaxAge || directives.MustRevalidate
            : directives.Public || directives.Private;
    }

    private async Task PrepareStreamingFillAsync(HttpResponseMessage response, RawHeaderSnapshot rawHeaders,
        HttpRequestMessage request, string cacheKey,
        Func<CachedHttpEntry, CachedHttpMetadata, CachedHttpEntry> update, Ct cancellationToken)
    {
        var expectedLength = response.StatusCode == System.Net.HttpStatusCode.NoContent
            ? 0 : response.Content.Headers.ContentLength;
        if (expectedLength > _options.MaxCacheableContentSize)
        {
            CacheMetrics.CacheSizeExceeded.Add(1, CacheMetrics.CreateMetricTags(request));
            return;
        }

        // Everything used after returning headers is a private immutable snapshot.
        var metadata = CreateMetadata(response, rawHeaders, request, "", 0, 0, false, false);
        var compressible = IsCompressible(response.Content.Headers.ContentType?.MediaType);
        var uriTag = GetUriTag(request.RequestUri);
        var uri = request.RequestUri;
        var metricTags = CacheMetrics.CreateMetricTags(request);
        var context = _fillContext.Value?.Retain() ?? GetPublicationCoordinator().Capture(uriTag);
        var origin = response.Content;
        response.Content = new StreamingCacheContent(origin, expectedLength, _options, cancellationToken,
            async (spool, ct) =>
            {
                using var publicationScope = context.Retain();
                AdaptiveSpool? compressed = null;
                try
                {
                    var stored = spool;
                    var compress = compressible && _options.CompressionThreshold > 0 &&
                        spool.Length >= _options.CompressionThreshold;
                    if (compress)
                    {
                        compressed = new AdaptiveSpool(_options, ex => _logger.CacheWriteFailed(uri, ex));
                        spool.Position = 0;
#if NET10_0_OR_GREATER
                        await using
#else
                        using
#endif
                        (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            await spool.CopyToAsync(gzip, 64 * 1024, ct);
                        }
                        stored = compressed;
                    }
                    var contentKey = $"{_options.ContentKeyPrefix}{(compress ? "gzip:" : "raw:")}{stored.FinishHash()}";
                    var contentStore = ResolveContentStore(spool.Length);
                    stored.Position = 0;
                    await contentStore.WriteAsync(contentKey, stored, stored.Length,
                        uriTag == null ? null : [uriTag], ct);
                    ct.ThrowIfCancellationRequested();

                    var variant = metadata with
                    {
                        ContentKey = contentKey,
                        ContentLength = stored.Length,
                        OriginalContentLength = spool.Length,
                        IsCompressed = compress,
                        IsStoredExternally = ReferenceEquals(contentStore, _largeContentStore),
                        PublicationSession = context.Session,
                        PublicationSequence = context.Sequence
                    };
                    await context.Stripe.Gate.WaitAsync(ct);
                    try
                    {
                        if (context.Epoch != context.Stripe.Epoch)
                        {
                            return;
                        }
                        var current = await GetCacheEntryAsync(cacheKey, ct) ?? new CachedHttpEntry();
                        var entry = update(current, variant);
                        if (ReplacesNewerPublication(current, entry, context))
                        {
                            return;
                        }
                        await _cache.SetAsync(cacheKey, entry, CreateCacheEntryOptions(entry),
                            tags: uriTag == null ? null : [uriTag], cancellationToken: ct);
                    }
                    finally
                    {
                        context.Stripe.Gate.Release();
                    }
                }
                finally
                {
                    compressed?.Dispose();
                }
            },
            ex => _logger.CacheWriteFailed(uri, ex), context.Dispose,
            () => CacheMetrics.CacheSizeExceeded.Add(1, metricTags));

        if (expectedLength == 0)
        {
            // Bodyless consumers may not touch Content at all. Validate an empty
            // representation with a nonempty read and finish publication now.
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
                {
                    throw new IOException("The origin returned bytes for a bodyless response.");
                }
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private static bool ReplacesNewerPublication(CachedHttpEntry current, CachedHttpEntry updated, FillContext context) =>
        current.Variants.Any(variant =>
            variant.PublicationSession == context.Session &&
            variant.PublicationSequence > context.Sequence &&
            !updated.Variants.Any(candidate => ReferenceEquals(candidate, variant)));

    // State exists only while sends or body fills are active. Exact URI keys avoid
    // unrelated invalidations abandoning otherwise valid concurrent fills.
    private sealed class PublicationCoordinator
    {
        private readonly Guid _session = Guid.NewGuid();
        private readonly object _sync = new();
        private readonly Dictionary<string, PublicationStripe> _states = new(StringComparer.Ordinal);
        private long _sequence;

        public FillContext Capture(string? uriTag)
        {
            var key = uriTag ?? "";
            lock (_sync)
            {
                if (!_states.TryGetValue(key, out var state))
                {
                    state = new PublicationStripe();
                    _states.Add(key, state);
                }
                state.References++;
                return new(this, key, state, Volatile.Read(ref state.Epoch), ++_sequence, _session);
            }
        }

        public FillContext Retain(FillContext context)
        {
            lock (_sync)
            {
                Guard.NotDisposed(context.IsReleased, context);
                context.Stripe.References++;
                return new(this, context.UriTag, context.Stripe, context.Epoch, context.Sequence, context.Session);
            }
        }

        public void Release(FillContext context)
        {
            lock (_sync)
            {
                if (--context.Stripe.References == 0)
                {
                    _states.Remove(context.UriTag);
                    context.Stripe.Gate.Dispose();
                }
            }
        }
    }

    private sealed class PublicationStripe
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Epoch;
        public int References;
    }

    private sealed class FillContext(PublicationCoordinator owner, string uriTag, PublicationStripe stripe,
        long epoch, long sequence, Guid session) : IDisposable
    {
        private int _released;
        public bool IsReleased => Volatile.Read(ref _released) != 0;
        public string UriTag { get; } = uriTag;
        public PublicationStripe Stripe { get; } = stripe;
        public long Epoch { get; } = epoch;
        public long Sequence { get; } = sequence;
        public Guid Session { get; } = session;
        public FillContext Retain() => owner.Retain(this);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }
}
