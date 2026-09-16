// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>
/// An HTTP delegating handler that provides client-side caching based on RFC 9111.
/// </summary>
public partial class HttpHybridCacheHandler : DelegatingHandler
{
    /// <summary>
    /// Represents the key used to store or retrieve the cache hits counter in a data store or metrics system.
    /// </summary>
    public const string CacheHitsCounterKey = "cache.hits";
    /// <summary>
    /// Represents the key used to identify the cache misses counter in metrics collections.
    /// </summary>
    public const string CacheMissesCounterKey = "cache.misses";
    /// <summary>
    /// Represents the key used to identify the cache stale counter in metrics collections.
    /// </summary>
    public const string CacheStaleCounterKey = "cache.stale";
    /// <summary>
    /// Represents the key used to identify the cache size exceeded counter in metrics collections.
    /// </summary>
    public const string CacheSizeExceededCounterKey = "cache.size_exceeded";
    private const int MaxVariantsPerEntry = 8;

    private readonly HybridCache _cache;
    private readonly IHttpCacheContentStore _contentStore;
    private readonly ILargeHttpCacheContentStore? _largeContentStore;
    private readonly TimeProvider _timeProvider;
    private readonly HttpHybridCacheHandlerOptions _options;
    private readonly ILogger _logger;
    private readonly FreshnessCalculator _freshnessCalculator;
    private readonly CacheKeyGenerator _cacheKeyGenerator;
    private static readonly HashSet<string> NotModifiedContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Location",
        "Expires",
        "Last-Modified"
    };
    private static readonly HashSet<string> HopByHopHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Connection",
        "Transfer-Encoding",
        "TE",
        "Trailer",
        "Upgrade"
    };
    private static readonly HashSet<HttpStatusCode> MustUnderstandNoStoreExemptStatuses =
    [
        HttpStatusCode.OK,
        HttpStatusCode.NonAuthoritativeInformation,
        HttpStatusCode.NoContent,
        HttpStatusCode.PartialContent,
        HttpStatusCode.MultipleChoices,
        HttpStatusCode.MovedPermanently,
        HttpStatusCode.NotModified,
        HttpStatusCode.NotFound,
        HttpStatusCode.MethodNotAllowed,
        HttpStatusCode.Gone,
        HttpStatusCode.RequestUriTooLong,
        HttpStatusCode.NotImplemented
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpHybridCacheHandler"/> class.
    /// </summary>
    /// <param name="cache">The hybrid cache instance to use for caching.</param>
    /// <param name="timeProvider">The time provider for time-based operations. Uses system time if not specified.</param>
    /// <param name="contentStore">The default cache content store (HybridCache-backed by default).</param>
    /// <param name="serviceProvider">Service provider used to resolve optional large-content store.</param>
    /// <param name="options">Configuration options for the handler. Uses default options if not specified.</param>
    /// <param name="logger">The logger instance. Uses NullLogger if not specified.</param>
    public HttpHybridCacheHandler(
        [FromKeyedServices(ServiceCollectionExtensions.HybridCacheKey)] HybridCache cache,
        TimeProvider timeProvider,
        IHttpCacheContentStore contentStore,
        IServiceProvider serviceProvider,
        IOptions<HttpHybridCacheHandlerOptions> options,
        ILogger<HttpHybridCacheHandler> logger)
    {
        _cache = cache;
        _options = options.Value;
        _options.ValidateSpooling();
        _contentStore = contentStore;
        _largeContentStore = serviceProvider.GetService<ILargeHttpCacheContentStore>();
        _timeProvider = timeProvider;
        _logger = logger;
        _freshnessCalculator = new FreshnessCalculator(_timeProvider, _options);
        _cacheKeyGenerator = new CacheKeyGenerator(_options);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpHybridCacheHandler"/> class.
    /// </summary>
    /// <param name="cache">The hybrid cache instance to use for caching.</param>
    /// <param name="timeProvider">The time provider for time-based operations. Uses system time if not specified.</param>
    /// <param name="options">Configuration options for the handler. Uses default options if not specified.</param>
    /// <param name="logger">The logger instance. Uses NullLogger if not specified.</param>
    public HttpHybridCacheHandler(
        HybridCache cache,
        TimeProvider timeProvider,
        IOptions<HttpHybridCacheHandlerOptions> options,
        ILogger<HttpHybridCacheHandler> logger)
    {
        _cache = cache;
        _options = options.Value;
        _options.ValidateSpooling();
        _contentStore = new ContentCache(cache);
        _timeProvider = timeProvider;
        _logger = logger;
        _freshnessCalculator = new FreshnessCalculator(_timeProvider, _options);
        _cacheKeyGenerator = new CacheKeyGenerator(_options);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpHybridCacheHandler"/> class with a specific inner handler.
    /// </summary>
    /// <param name="innerHandler">The inner handler which is responsible for processing the HTTP response messages.</param>
    /// <param name="cache">The hybrid cache instance to use for caching.</param>
    /// <param name="timeProvider">The time provider for time-based operations. Uses system time if not specified.</param>
    /// <param name="contentStore">The default cache content store, or null to use the HybridCache-backed store.</param>
    /// <param name="options">Configuration options for the handler. Uses default options if not specified.</param>
    /// <param name="logger">The logger instance. Uses NullLogger if not specified.</param>
    /// <param name="largeContentStore">Optional large-response content store.</param>
    public HttpHybridCacheHandler(
        HttpMessageHandler innerHandler,
        HybridCache cache,
        TimeProvider timeProvider,
        IHttpCacheContentStore? contentStore,
        HttpHybridCacheHandlerOptions options,
        ILogger<HttpHybridCacheHandler> logger,
        ILargeHttpCacheContentStore? largeContentStore = null)
        : base(innerHandler)
    {
        _cache = cache;
        _contentStore = contentStore ?? new ContentCache(cache);
        _largeContentStore = largeContentStore;
        _timeProvider = timeProvider;
        _options = options;
        _options.ValidateSpooling();
        _logger = logger;
        _freshnessCalculator = new FreshnessCalculator(_timeProvider, _options);
        _cacheKeyGenerator = new CacheKeyGenerator(_options);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpHybridCacheHandler"/> class with a specific inner handler.
    /// </summary>
    /// <param name="innerHandler">The inner handler which is responsible for processing the HTTP response messages.</param>
    /// <param name="cache">The hybrid cache instance to use for caching.</param>
    /// <param name="timeProvider">The time provider for time-based operations. Uses system time if not specified.</param>
    /// <param name="options">Configuration options for the handler. Uses default options if not specified.</param>
    /// <param name="logger">The logger instance. Uses NullLogger if not specified.</param>
    public HttpHybridCacheHandler(
        HttpMessageHandler innerHandler,
        HybridCache cache,
        TimeProvider timeProvider,
        HttpHybridCacheHandlerOptions options,
        ILogger<HttpHybridCacheHandler> logger)
        : this(innerHandler, cache, timeProvider, null, options, logger)
    {
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Ct ct)
    {
        var coordinator = GetPublicationCoordinator();
        using var fillScope = coordinator.Capture(GetUriTag(request.RequestUri));
        _fillContext.Value = fillScope;
        // Only cache GET and HEAD requests
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            var response = await SendOriginAsync(request, ct);
            await InvalidateCachedResponsesForUnsafeMethodAsync(request, response, ct);
            AddDiagnosticHeaders(response, DiagnosticHeaders.ByPassMethod);
            return response;
        }

        NormalizeIfNoneMatchHeader(request);
        var hasRangeRequest = TryGetSingleByteRange(request, out var requestedRange);

        // Check request Cache-Control directives
        var requestCacheControl = request.Headers.CacheControl;

        // Handle only-if-cached
        if (requestCacheControl?.OnlyIfCached == true)
        {
            try
            {
                var cacheMethod = request.Method == HttpMethod.Head ? HttpMethod.Get : null;
                var cacheKey = GenerateVaryAwareCacheKey(request, cacheMethod, includeRange: hasRangeRequest);
                var completeResponseCacheKey = hasRangeRequest
                    ? GenerateVaryAwareCacheKey(request, cacheMethod)
                    : null;

                var onlyIfCachedRequestUriTag = GetUriTag(request.RequestUri);
                var cacheKeys = completeResponseCacheKey != null && !string.Equals(cacheKey, completeResponseCacheKey, StringComparison.Ordinal)
                    ? new[] { cacheKey, completeResponseCacheKey }
                    : new[] { cacheKey };

                foreach (var candidateCacheKey in cacheKeys)
                {
                    var onlyIfCachedEntry = await GetCacheEntryAsync(candidateCacheKey, ct);
                    if (onlyIfCachedEntry == null)
                    {
                        continue;
                    }

                    var onlyIfCachedVariant = VaryMatcher.SelectVariant(
                        onlyIfCachedEntry,
                        request,
                        candidate => IsUsableForOnlyIfCached(candidate, request));
                    if (onlyIfCachedVariant == null)
                    {
                        continue;
                    }

                    if (request.Method != HttpMethod.Head &&
                        hasRangeRequest &&
                        await TryServeRangeFromCachedMetadataAsync(onlyIfCachedVariant, requestedRange, request, ct) is { } cachedRangeResponse)
                    {
                        AddDiagnosticHeaders(cachedRangeResponse, DiagnosticHeaders.HitOnlyIfCached, onlyIfCachedVariant);
                        return cachedRangeResponse;
                    }

                    var response = await DeserializeResponseAsync(onlyIfCachedVariant, ct);
                    if (response != null)
                    {
                        if (request.Method == HttpMethod.Head)
                        {
                            var cachedHeadResponse = BuildMergedHeadResponse(response, onlyIfCachedVariant, request);
                            response.Dispose();
                            AddDiagnosticHeaders(cachedHeadResponse, DiagnosticHeaders.HitOnlyIfCached, onlyIfCachedVariant);
                            return cachedHeadResponse;
                        }

                        AddDiagnosticHeaders(response, DiagnosticHeaders.HitOnlyIfCached, onlyIfCachedVariant);
                        return response;
                    }

                    await RemoveVariantFromEntryAsync(
                        candidateCacheKey,
                        onlyIfCachedRequestUriTag,
                        onlyIfCachedEntry,
                        onlyIfCachedVariant,
                        request.RequestUri,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.CacheOperationFailed(request.RequestUri, ex);
            }

            // Return 504 Gateway Timeout if not in cache
            var gatewayTimeout = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                RequestMessage = request
            };
            AddDiagnosticHeaders(gatewayTimeout, DiagnosticHeaders.MissOnlyIfCached);
            return gatewayTimeout;
        }

        if (request.Method == HttpMethod.Head)
        {
            return await SendHeadAndUpdateCachedGetAsync(request, ct);
        }

        // Handle no-store - bypass cache entirely
        if (requestCacheControl?.NoStore == true)
        {
            var response = await SendOriginAsync(request, ct);
            AddDiagnosticHeaders(response, DiagnosticHeaders.ByPassNoStore);
            return response;
        }

        // Handle no-cache or max-age=0 - require validation
        var mustRevalidate = requestCacheControl?.NoCache == true
            || requestCacheControl?.MaxAge == TimeSpan.Zero;

        var cacheKey2 = GenerateVaryAwareCacheKey(request, includeRange: hasRangeRequest);
        var completeResponseKey = hasRangeRequest ? GenerateVaryAwareCacheKey(request) : null;
        var requestUriTag = GetUriTag(request.RequestUri);
        HttpResponseMessage? uncachedResponse = null;
        RawHeaderSnapshot? uncachedRawHeaders = null;

        if (hasRangeRequest && completeResponseKey != null)
        {
            var completeResponseEntry = await GetCacheEntryAsync(completeResponseKey, ct);
            var completeResponseCandidate = completeResponseEntry == null
                ? null
                : VaryMatcher.SelectVariant(completeResponseEntry, request, candidate => IsFresh(candidate, request));

            if (completeResponseCandidate != null &&
                !mustRevalidate &&
                !completeResponseCandidate.NoCache)
            {
                var cachedRangeResponse = await TryServeRangeFromCachedMetadataAsync(completeResponseCandidate, requestedRange, request, ct);
                if (cachedRangeResponse != null)
                {
                    CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request));
                    AddDiagnosticHeaders(cachedRangeResponse, DiagnosticHeaders.HitFresh, completeResponseCandidate);
                    return cachedRangeResponse;
                }
            }
        }

        CachedHttpEntry? cachedEntry;
        var readingStreamingOrigin = false;
        try
        {
            if (StreamingEnabled)
            {
                cachedEntry = await GetCacheEntryAsync(cacheKey2, ct);
                if (cachedEntry == null)
                {
                    readingStreamingOrigin = true;
                    uncachedResponse = await SendOriginAsync(request, ct);
                    readingStreamingOrigin = false;
                    uncachedRawHeaders = CaptureRawHeaders(uncachedResponse);
                    if (IsResponseCacheable(uncachedResponse, request) && IsAuthorizedResponseCacheable(uncachedResponse, request))
                    {
                        readingStreamingOrigin = true;
                        await PrepareStreamingFillAsync(uncachedResponse, uncachedRawHeaders, request, cacheKey2,
                            (current, variant) => UpsertVariant(current, variant), ct);
                        readingStreamingOrigin = false;
                    }
                }
            }
            else
            {
                cachedEntry = await _cache.GetOrCreateAsync(
                cacheKey2,
                async cancel =>
                {
                    uncachedResponse = await SendOriginAsync(request, cancel);

                    // Snapshot raw headers before any typed header access parses
                    // (and normalizes) them, so responses replay/pass through verbatim.
                    var rawHeaders = uncachedRawHeaders = CaptureRawHeaders(uncachedResponse);

                    // Don't cache if request had no-store
                    if (request.Headers.CacheControl?.NoStore == true)
                    {
                        return null;
                    }

                    var effectiveDirectives = GetEffectiveCacheDirectives(uncachedResponse);

                    // Authorization header handling depends on cache mode
                    if (request.Headers.Authorization != null)
                    {
                        if (_options.Mode == CacheMode.Shared)
                        {
                            // Shared cache: cache authorized responses only with explicit shared-cache directives.
                            if (!effectiveDirectives.Public &&
                                !effectiveDirectives.HasSharedMaxAge &&
                                !effectiveDirectives.MustRevalidate)
                            {
                                return null;
                            }
                        }
                        else // CacheMode.Private
                        {
                            // Private cache: Require explicit public or private directive for Authorization requests
                            if (!effectiveDirectives.Public &&
                                !effectiveDirectives.Private)
                            {
                                return null;
                            }
                        }
                    }

                    // Check if response is cacheable
                    if (!IsResponseCacheable(uncachedResponse, request, effectiveDirectives))
                    {
                        return null;
                    }

                    var metadata = await SerializeResponse(uncachedResponse, rawHeaders, request);
                    if (metadata == null)
                    {
                        return null;
                    }

                    return new CachedHttpEntry
                    {
                        Variants = [metadata]
                    };
                },
                tags: requestUriTag == null ? null : [requestUriTag],
                cancellationToken: ct
            );
            }
        }
        catch (Exception ex) when (!StreamingEnabled || (!readingStreamingOrigin && IsExpectedCacheFailure(ex)))
        {
            // Cache read/write failure - fall back to origin
            _logger.CacheOperationFailed(request.RequestUri, ex);
            uncachedResponse ??= await SendOriginAsync(request, ct);
            RestoreRawHeaders(uncachedResponse, uncachedRawHeaders);
            AddDiagnosticHeaders(uncachedResponse, DiagnosticHeaders.MissCacheError);
            CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
            return uncachedResponse;
        }

        if (cachedEntry != null)
        {
            // If factory just ran (uncachedResponse != null), return the fresh response
            if (uncachedResponse != null)
            {
                // Factory ran, so this is a miss that we just cached.
                // Re-set with accurate TTL since GetOrCreateAsync used the global default.
                try
                {
                    await _cache.SetAsync(cacheKey2, cachedEntry, CreateCacheEntryOptions(cachedEntry), tags: requestUriTag == null ? null : [requestUriTag], cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    _logger.CacheWriteFailed(request.RequestUri, ex);
                }
                RestoreRawHeaders(uncachedResponse, uncachedRawHeaders);
                AddDiagnosticHeaders(uncachedResponse, DiagnosticHeaders.Miss);
                CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                return uncachedResponse;
            }

            var cachedResponse = VaryMatcher.SelectVariant(cachedEntry, request, candidate => IsFresh(candidate, request))
                ?? VaryMatcher.SelectVariant(cachedEntry, request);
            var revalidateMismatchedVariant = false;
            if (cachedResponse == null)
            {
                cachedResponse = SelectValidatorVariant(cachedEntry);
                if (cachedResponse == null)
                {
                    var variantMissResponse = await SendOriginAsync(request, ct);
                    var variantMissRawHeaders = CaptureRawHeaders(variantMissResponse);
                    if (IsResponseCacheable(variantMissResponse, request))
                    {
                        await TryStoreVariantAsync(
                            cacheKey2,
                            requestUriTag,
                            cachedEntry,
                            variantMissResponse,
                            variantMissRawHeaders,
                            request,
                            (current, variant) => UpsertVariant(current, variant),
                            request.RequestUri,
                            ct);
                    }

                    RestoreRawHeaders(variantMissResponse, variantMissRawHeaders);
                    CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                    AddDiagnosticHeaders(variantMissResponse, DiagnosticHeaders.Miss);
                    return variantMissResponse;
                }

                revalidateMismatchedVariant = true;
            }

            // Check if validation is required (no-cache request or no-cache response)
            if (revalidateMismatchedVariant || mustRevalidate || cachedResponse.NoCache)
            {
                using var validationRequest = CreateValidationRequest(request, cachedResponse, out var validationUsesStoredValidator);
                uncachedResponse = await SendOriginAsync(validationRequest, ct);
                var validationRawHeaders = CaptureRawHeaders(uncachedResponse);

                // Handle 304 Not Modified
                if (uncachedResponse.StatusCode == HttpStatusCode.NotModified)
                {
                    if (revalidateMismatchedVariant)
                    {
                        uncachedResponse.Dispose();
                        var variantMissResponse = await SendOriginAsync(request, ct);
                        var variantMissRawHeaders = CaptureRawHeaders(variantMissResponse);
                        if (IsResponseCacheable(variantMissResponse, request))
                        {
                            await TryStoreVariantAsync(
                                cacheKey2,
                                requestUriTag,
                                cachedEntry,
                                variantMissResponse,
                                variantMissRawHeaders,
                                request,
                                (current, variant) => UpsertVariant(current, variant),
                                request.RequestUri,
                                ct);
                        }

                        CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                        RestoreRawHeaders(variantMissResponse, variantMissRawHeaders);
                        AddDiagnosticHeaders(variantMissResponse, DiagnosticHeaders.MissRevalidated);
                        return variantMissResponse;
                    }

                    return await HandleNotModifiedAsync(
                        request,
                        cacheKey2,
                        requestUriTag,
                        cachedEntry,
                        cachedResponse,
                        uncachedResponse,
                        validationRawHeaders,
                        validationUsesStoredValidator,
                        ct);
                }

                // Got a new response, update the cached entry if cacheable
                if (IsResponseCacheable(uncachedResponse, validationRequest))
                {
                    await TryStoreVariantAsync(
                        cacheKey2,
                        requestUriTag,
                        cachedEntry,
                        uncachedResponse,
                        validationRawHeaders,
                        validationRequest,
                        (current, variant) => UpsertVariant(current, variant),
                        request.RequestUri,
                        ct);
                }
                else
                {
                    var responseCacheControl = uncachedResponse.Headers.CacheControl;
                    if (responseCacheControl?.NoStore == true)
                    {
                        try
                        {
                            await RemoveStreamingAwareAsync(cacheKey2, requestUriTag, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.CacheRemoveFailed(request.RequestUri, ex);
                        }
                    }
                }

                CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                RestoreRawHeaders(uncachedResponse, validationRawHeaders);
                AddDiagnosticHeaders(uncachedResponse, DiagnosticHeaders.MissRevalidated);
                return uncachedResponse;
            }

            if (IsFresh(cachedResponse, request))
            {
                if (!hasRangeRequest && cachedResponse.IsPartial)
                {
                    // A full GET cannot be satisfied from a stored partial response.
                    var partialBypassResponse = await SendOriginAsync(request, ct);
                    var partialBypassRawHeaders = CaptureRawHeaders(partialBypassResponse);

                    if (IsResponseCacheable(partialBypassResponse, request))
                    {
                        await TryStoreVariantAsync(
                            cacheKey2,
                            requestUriTag,
                            cachedEntry,
                            partialBypassResponse,
                            partialBypassRawHeaders,
                            request,
                            (current, variant) => ReplaceVariant(current, cachedResponse, variant),
                            request.RequestUri,
                            ct,
                            mergeWithLatest: false);
                    }

                    RestoreRawHeaders(partialBypassResponse, partialBypassRawHeaders);
                    AddDiagnosticHeaders(partialBypassResponse, DiagnosticHeaders.Miss);
                    CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                    return partialBypassResponse;
                }

                if (hasRangeRequest)
                {
                    var rangeResponse = await TryServeRangeFromCachedMetadataAsync(cachedResponse, requestedRange, request, ct);
                    if (rangeResponse != null)
                    {
                        CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request));
                        AddDiagnosticHeaders(rangeResponse, DiagnosticHeaders.HitFresh, cachedResponse);
                        return rangeResponse;
                    }

                    var unsatisfiedRangeResponse = await SendOriginAsync(request, ct);
                    AddDiagnosticHeaders(unsatisfiedRangeResponse, DiagnosticHeaders.Miss);
                    CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                    return unsatisfiedRangeResponse;
                }

                if (TryCreateConditionalNotModifiedResponse(request, cachedResponse, out var notModifiedResponse))
                {
                    CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request));
                    AddDiagnosticHeaders(notModifiedResponse, DiagnosticHeaders.HitNotModified, cachedResponse);
                    return notModifiedResponse;
                }

                var response = await DeserializeResponseAsync(cachedResponse, ct);
                if (response == null)
                {
                    // Content missing - treat as cache miss
                    await _cache.RemoveAsync(cacheKey2, ct);
                    var freshResponse = await SendOriginAsync(request, ct);
                    AddDiagnosticHeaders(freshResponse, DiagnosticHeaders.MissCacheError);
                    CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                    return freshResponse;
                }
                ApplyAgeHeader(response, cachedResponse);
                CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request));
                AddDiagnosticHeaders(response, DiagnosticHeaders.HitFresh, cachedResponse);
                return response;
            }

            // Response is stale, check stale-while-revalidate
            if (cachedResponse.StaleWhileRevalidate.HasValue)
            {
                var age = CalculateCurrentAge(cachedResponse);
                var freshnessLifetime = CalculateFreshnessLifetime(cachedResponse) ?? TimeSpan.Zero;
                var staleness = age - freshnessLifetime;

                // Within stale-while-revalidate window?
                if (staleness <= cachedResponse.StaleWhileRevalidate.Value)
                {
                    if (TryCreateConditionalNotModifiedResponse(request, cachedResponse, out var notModifiedResponse))
                    {
                        AddDiagnosticHeaders(notModifiedResponse, DiagnosticHeaders.HitNotModified, cachedResponse);

                        // Trigger background revalidation
                        StartBackgroundRevalidation(cachedEntry, cachedResponse, request, cacheKey2);

                        CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request)); // Count as hit (stale-while-revalidate)
                        CacheMetrics.CacheStale.Add(1, CacheMetrics.CreateMetricTags(request));
                        return notModifiedResponse;
                    }

                    // Serve stale content immediately
                    var staleResponse = await DeserializeResponseAsync(cachedResponse, ct);
                    if (staleResponse == null)
                    {
                        // Content missing - treat as cache miss
                        await _cache.RemoveAsync(cacheKey2, ct);
                        var freshResponse = await SendOriginAsync(request, ct);
                        AddDiagnosticHeaders(freshResponse, DiagnosticHeaders.MissCacheError);
                        CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
                        return freshResponse;
                    }
                    ApplyAgeHeader(staleResponse, cachedResponse);
                    AddDiagnosticHeaders(staleResponse, DiagnosticHeaders.HitStaleWhileRevalidate, cachedResponse);

                    // Trigger background revalidation
                    StartBackgroundRevalidation(cachedEntry, cachedResponse, request, cacheKey2);

                    CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request)); // Count as hit (stale-while-revalidate)
                    CacheMetrics.CacheStale.Add(1, CacheMetrics.CreateMetricTags(request));
                    return staleResponse;
                }
            }

            // Response is stale, attempt validation
            using var staleValidationRequest = CreateValidationRequest(request, cachedResponse, out var staleValidationUsesStoredValidator);
            var stalenessForValidation = CalculateStaleness(cachedResponse);

            RawHeaderSnapshot staleValidationRawHeaders;
            try
            {
                uncachedResponse = await SendOriginAsync(staleValidationRequest, ct);
                // Snapshot raw headers before typed access normalizes them
                staleValidationRawHeaders = CaptureRawHeaders(uncachedResponse);
            }
            catch (Exception ex) when (CanServeStaleOnTransportFailure(ex, ct))
            {
                if (CanServeStaleOnError(cachedResponse, stalenessForValidation))
                {
                    CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request));
                    CacheMetrics.CacheStale.Add(1, CacheMetrics.CreateMetricTags(request));
                    var staleResponse = await DeserializeResponseAsync(cachedResponse, ct);
                    if (staleResponse == null)
                    {
                        throw;
                    }

                    AddDiagnosticHeaders(staleResponse, DiagnosticHeaders.HitStaleIfError, cachedResponse);
                    return staleResponse;
                }

                throw;
            }

            // Check for stale-if-error or implicit stale-on-error.
            if ((int)uncachedResponse.StatusCode >= 500 &&
                CanServeStaleOnError(cachedResponse, stalenessForValidation))
            {
                CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request)); // Count as hit (stale on error)
                CacheMetrics.CacheStale.Add(1, CacheMetrics.CreateMetricTags(request));
                var response = await DeserializeResponseAsync(cachedResponse, ct);
                if (response == null)
                {
                    // Content missing - return error response
                    AddDiagnosticHeaders(uncachedResponse, DiagnosticHeaders.MissCacheError);
                    return uncachedResponse;
                }
                AddDiagnosticHeaders(response, DiagnosticHeaders.HitStaleIfError, cachedResponse);
                return response;
            }

            // Handle 304 Not Modified
            if (uncachedResponse.StatusCode == HttpStatusCode.NotModified)
            {
                return await HandleNotModifiedAsync(
                    request,
                    cacheKey2,
                    requestUriTag,
                    cachedEntry,
                    cachedResponse,
                    uncachedResponse,
                    staleValidationRawHeaders,
                    staleValidationUsesStoredValidator,
                    ct);
            }

            // Resource changed (200 or other status) - update cache if cacheable
            var revalidatedDirectives = GetEffectiveCacheDirectives(uncachedResponse);
            if (IsResponseCacheable(uncachedResponse, staleValidationRequest, revalidatedDirectives))
            {
                await TryStoreVariantAsync(
                    cacheKey2,
                    requestUriTag,
                    cachedEntry,
                    uncachedResponse,
                    staleValidationRawHeaders,
                    staleValidationRequest,
                    (current, variant) => UpsertVariant(current, variant),
                    request.RequestUri,
                    ct);
            }
            else
            {
                // Response has no-store or is not cacheable, remove existing cache entry
                if (revalidatedDirectives.NoStore)
                {
                    try
                    {
                        await SetMergedEntryAsync(
                            cacheKey2,
                            requestUriTag,
                            cachedEntry,
                            current => RemoveVariant(current, cachedResponse),
                            ct);
                    }
                    catch (Exception ex)
                    {
                        // Cache remove failure - ignore
                        _logger.CacheRemoveFailed(request.RequestUri, ex);
                    }
                }
            }

            RestoreRawHeaders(uncachedResponse, staleValidationRawHeaders);
            AddDiagnosticHeaders(uncachedResponse, DiagnosticHeaders.MissRevalidated);
            return uncachedResponse;
        }

        // cachedEntry is null, which means:
        // 1. Cache was empty and factory ran, setting uncachedResponse
        // 2. Factory returned null because response wasn't cacheable
        // Either way, uncachedResponse should be set
        if (uncachedResponse == null)
        {
            // This shouldn't happen, but safety fallback
            uncachedResponse = await SendOriginAsync(request, ct);
        }
        else
        {
            RestoreRawHeaders(uncachedResponse, uncachedRawHeaders);
        }

        AddDiagnosticHeaders(uncachedResponse, DiagnosticHeaders.Miss);
        CacheMetrics.CacheMisses.Add(1, CacheMetrics.CreateMetricTags(request));
        return uncachedResponse;
    }

    private async Task TryStoreVariantAsync(
        string cacheKey,
        string? requestUriTag,
        CachedHttpEntry fallbackEntry,
        HttpResponseMessage response,
        RawHeaderSnapshot rawHeaders,
        HttpRequestMessage serializationRequest,
        Func<CachedHttpEntry, CachedHttpMetadata, CachedHttpEntry> updateEntry,
        Uri? requestUri,
        Ct ct,
        bool mergeWithLatest = true)
    {
        if (StreamingEnabled)
        {
            await PrepareStreamingFillAsync(response, rawHeaders, serializationRequest, cacheKey, updateEntry, ct);
            return;
        }
        var variant = await SerializeResponse(response, rawHeaders, serializationRequest);
        if (variant == null)
        {
            return;
        }

        try
        {
            if (mergeWithLatest)
            {
                await SetMergedEntryAsync(
                    cacheKey,
                    requestUriTag,
                    fallbackEntry,
                    current => updateEntry(current, variant),
                    ct);
            }
            else
            {
                var updatedEntry = updateEntry(fallbackEntry, variant);
                await _cache.SetAsync(
                    cacheKey,
                    updatedEntry,
                    CreateCacheEntryOptions(updatedEntry),
                    tags: requestUriTag == null ? null : [requestUriTag],
                    cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.CacheWriteFailed(requestUri, ex);
        }
    }

    private async Task<HttpResponseMessage> HandleNotModifiedAsync(
        HttpRequestMessage request,
        string cacheKey,
        string? requestUriTag,
        CachedHttpEntry cachedEntry,
        CachedHttpMetadata cachedResponse,
        HttpResponseMessage notModifiedResponse,
        RawHeaderSnapshot notModifiedRawHeaders,
        bool usedStoredValidator,
        Ct ct)
    {
        if (!usedStoredValidator)
        {
            RestoreRawHeaders(notModifiedResponse, notModifiedRawHeaders);
            AddDiagnosticHeaders(notModifiedResponse, DiagnosticHeaders.MissRevalidated);
            return notModifiedResponse;
        }

        var updatedVariant = UpdateCachedEntry(cachedResponse, notModifiedResponse);
        try
        {
            await SetMergedEntryAsync(
                cacheKey,
                requestUriTag,
                cachedEntry,
                current => ReplaceVariant(current, cachedResponse, updatedVariant),
                ct);
        }
        catch (Exception ex)
        {
            _logger.CacheWriteFailed(request.RequestUri, ex);
        }

        CacheMetrics.CacheHits.Add(1, CacheMetrics.CreateMetricTags(request));
        if (TryCreateConditionalNotModifiedResponse(request, updatedVariant, out var conditionalNotModifiedResponse))
        {
            AddDiagnosticHeaders(conditionalNotModifiedResponse, DiagnosticHeaders.HitNotModified, updatedVariant);
            notModifiedResponse.Dispose();
            return conditionalNotModifiedResponse;
        }

        var response = await DeserializeResponseAsync(updatedVariant, ct);
        if (response == null)
        {
            await RemoveVariantFromEntryAsync(
                cacheKey,
                requestUriTag,
                cachedEntry,
                updatedVariant,
                request.RequestUri,
                ct);

            // Content missing, return origin response used for revalidation.
            RestoreRawHeaders(notModifiedResponse, notModifiedRawHeaders);
            AddDiagnosticHeaders(notModifiedResponse, DiagnosticHeaders.MissCacheError);
            return notModifiedResponse;
        }

        ApplyAgeHeader(response, updatedVariant);
        AddDiagnosticHeaders(response, DiagnosticHeaders.HitRevalidated, updatedVariant);
        notModifiedResponse.Dispose();
        return response;
    }

    private CachedHttpMetadata UpdateCachedEntry(CachedHttpMetadata cached, HttpResponseMessage notModifiedResponse)
    {
        // Update metadata from 304 response while keeping the cached body
        var notModifiedContentHeaders = notModifiedResponse.Content?.Headers;
        var directives = GetEffectiveCacheDirectives(notModifiedResponse);
        var hasAnyDirectiveHeaders = HasAnyDirectiveHeaders(notModifiedResponse);
        var updatedMaxAge = directives.MaxAge;
        var updatedExpires = notModifiedContentHeaders?.Expires;
        var updatedDate = notModifiedResponse.Headers.Date;
        var responseHasCacheControl = GetHeaderValues(notModifiedResponse.Headers, "Cache-Control").Length > 0;
        var (updatedStaleWhileRevalidate, updatedStaleIfError) = ParseStaleDirectives(notModifiedResponse.Headers);
        var ignoreStoredAge = hasAnyDirectiveHeaders ? directives.IgnoreStoredAge : cached.IgnoreStoredAge;
        var effectiveQualifiedNoCacheHeaderNames = hasAnyDirectiveHeaders
            ? directives.QualifiedNoCacheHeaderNames
            : cached.QualifiedNoCacheHeaderNames;
        var mergedHeaders = new Dictionary<string, string[]>(cached.Headers, StringComparer.OrdinalIgnoreCase);
        var mergedContentHeaders = new Dictionary<string, string[]>(cached.ContentHeaders, StringComparer.OrdinalIgnoreCase);
        var updatedResponseHeaders = CaptureHeaders(notModifiedResponse.Headers);
        var updatedResponseContentHeaders = notModifiedContentHeaders != null
            ? CaptureHeaders(notModifiedContentHeaders)
            : new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        updatedResponseContentHeaders.Remove("Content-Length");
        var stripNames = BuildStoredHeaderStripSet(notModifiedResponse, effectiveQualifiedNoCacheHeaderNames);
        RemoveHeaders(updatedResponseHeaders, stripNames);
        RemoveHeaders(updatedResponseContentHeaders, stripNames);
        UpsertHeaderDictionary(mergedHeaders, updatedResponseHeaders);
        UpsertHeaderDictionary(mergedContentHeaders, updatedResponseContentHeaders);
        NormalizeContentTypeHeader(mergedContentHeaders);
        var updatedEtag = updatedResponseHeaders.TryGetValue("ETag", out var updatedETagValues)
            ? updatedETagValues.FirstOrDefault()
            : null;

        // Extract Age from 304 response if present
        var updatedAge = ParseAgeHeader(notModifiedResponse);

        // Return updated metadata, preserving content reference
        return new CachedHttpMetadata
        {
            StatusCode = cached.StatusCode,
            ContentKey = cached.ContentKey,
            ContentLength = cached.ContentLength,
            OriginalContentLength = cached.OriginalContentLength,
            Headers = mergedHeaders,
            ContentHeaders = mergedContentHeaders,
            CachedAt = _timeProvider.GetUtcNow(),
            MaxAge = updatedMaxAge ?? cached.MaxAge,
            HasSharedMaxAge = hasAnyDirectiveHeaders ? directives.HasSharedMaxAge : cached.HasSharedMaxAge,
            ETag = string.IsNullOrWhiteSpace(updatedEtag) ? cached.ETag : updatedEtag,
            LastModified = notModifiedContentHeaders?.LastModified ?? cached.LastModified,
            Expires = updatedExpires ?? cached.Expires,
            Date = updatedDate ?? cached.Date,
            Age = ignoreStoredAge ? TimeSpan.Zero : updatedAge ?? cached.Age,
            VaryHeaders = cached.VaryHeaders,
            VaryHeaderValues = cached.VaryHeaderValues,
            StaleWhileRevalidate = responseHasCacheControl ? updatedStaleWhileRevalidate : cached.StaleWhileRevalidate,
            StaleIfError = responseHasCacheControl ? updatedStaleIfError : cached.StaleIfError,
            MustRevalidate = hasAnyDirectiveHeaders ? directives.MustRevalidate : cached.MustRevalidate,
            ProxyRevalidate = hasAnyDirectiveHeaders ? directives.ProxyRevalidate : cached.ProxyRevalidate,
            NoCache = hasAnyDirectiveHeaders ? directives.NoCache : cached.NoCache,
            QualifiedNoCacheHeaderNames = effectiveQualifiedNoCacheHeaderNames,
            IgnoreStoredAge = ignoreStoredAge,
            IsCompressed = cached.IsCompressed,
            IsPartial = cached.IsPartial,
            RangeStart = cached.RangeStart,
            RangeEnd = cached.RangeEnd,
            RangeTotalLength = cached.RangeTotalLength,
            IsStoredExternally = cached.IsStoredExternally
        };
    }

    private async Task BackgroundRevalidateAsync(
        CachedHttpEntry cachedEntry,
        CachedHttpMetadata cachedResponse,
        HttpRequestMessage originalRequest,
        string cacheKey,
        Ct cancellationToken)
    {
        var requestUriTag = GetUriTag(originalRequest.RequestUri);
        var requestUriForLogging = originalRequest.RequestUri;
        try
        {
            using var fillScope = GetPublicationCoordinator().Capture(requestUriTag);
            _fillContext.Value = fillScope;
            using var revalidationRequest = CreateValidationRequest(originalRequest, cachedResponse, out var backgroundValidationUsesStoredValidator);
            requestUriForLogging = revalidationRequest.RequestUri;
            using var revalidatedResponse = await SendOriginAsync(revalidationRequest, cancellationToken);
            var currentEntry = await GetCacheEntryAsync(cacheKey, cancellationToken) ?? cachedEntry;

            // Snapshot raw headers before typed access normalizes them
            var revalidatedRawHeaders = CaptureRawHeaders(revalidatedResponse);

            if (revalidatedResponse.StatusCode == HttpStatusCode.NotModified)
            {
                if (!backgroundValidationUsesStoredValidator)
                {
                    return;
                }

                var updatedVariant = UpdateCachedEntry(cachedResponse, revalidatedResponse);
                try
                {
                    await SetMergedEntryAsync(cacheKey, requestUriTag, currentEntry,
                        current => ReplaceVariant(current, cachedResponse, updatedVariant), cancellationToken);
                }
                catch (Exception ex)
                {
                    // Cache write failure during background revalidation - ignore
                    _logger.BackgroundCacheWriteFailed(revalidationRequest.RequestUri, ex);
                }
            }
            else
            {
                var revalidatedDirectives = GetEffectiveCacheDirectives(revalidatedResponse);
                if (IsResponseCacheable(revalidatedResponse, revalidationRequest, revalidatedDirectives))
                {
                    if (StreamingEnabled)
                    {
                        await PrepareStreamingFillAsync(revalidatedResponse, revalidatedRawHeaders, revalidationRequest, cacheKey,
                            (current, variant) => UpsertVariant(current, variant), cancellationToken);
                        await revalidatedResponse.Content.CopyToAsync(Stream.Null, cancellationToken);
                        return;
                    }
                    var freshResponse = await SerializeResponse(revalidatedResponse, revalidatedRawHeaders, revalidationRequest);
                    if (freshResponse != null)
                    {
                        var updatedEntry = UpsertVariant(currentEntry, freshResponse);
                        try
                        {
                            await _cache.SetAsync(cacheKey, updatedEntry, CreateCacheEntryOptions(updatedEntry), tags: requestUriTag == null ? null : [requestUriTag], cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            // Cache write failure during background revalidation - ignore
                            _logger.BackgroundCacheWriteFailed(revalidationRequest.RequestUri, ex);
                        }
                    }
                }
                else
                {
                    if (revalidatedDirectives.NoStore)
                    {
                        try
                        {
                            await SetMergedEntryAsync(cacheKey, requestUriTag, currentEntry,
                                current => RemoveVariant(current, cachedResponse), cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            // Cache remove failure - ignore
                            _logger.BackgroundCacheRemoveFailed(revalidationRequest.RequestUri, ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Background revalidation failed, keep stale entry
            _logger.BackgroundRevalidationFailed(requestUriForLogging, ex);
        }
    }

    private static HttpRequestMessage CreateValidationRequest(
        HttpRequestMessage originalRequest,
        CachedHttpMetadata cachedResponse,
        out bool usedStoredValidator)
    {
        usedStoredValidator = !string.IsNullOrEmpty(cachedResponse.ETag) || cachedResponse.LastModified.HasValue;
        var request = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri);
        foreach (var header in originalRequest.Headers)
        {
            if (usedStoredValidator &&
                (header.Key.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) ||
                 header.Key.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!TextCompatibility.IsNullOrEmpty(cachedResponse.ETag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", NormalizeETagForSending(cachedResponse.ETag));
        }
        else if (cachedResponse.LastModified.HasValue)
        {
            request.Headers.TryAddWithoutValidation(
                "If-Modified-Since",
                cachedResponse.LastModified.Value.ToString("R"));
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendHeadAndUpdateCachedGetAsync(HttpRequestMessage request, Ct ct)
    {
        var headResponse = await SendOriginAsync(request, ct);
        var getCacheKey = GenerateVaryAwareCacheKey(request, cacheMethod: HttpMethod.Get);
        var requestUriTag = GetUriTag(request.RequestUri);

        var cachedGetEntry = await GetCacheEntryAsync(getCacheKey, ct);
        var cachedGet = cachedGetEntry == null
            ? null
            : VaryMatcher.SelectVariant(cachedGetEntry, request);

        if (cachedGet == null || cachedGet.IsPartial)
        {
            AddDiagnosticHeaders(headResponse, DiagnosticHeaders.ByPassMethod);
            return headResponse;
        }

        var headDirectives = GetEffectiveCacheDirectives(headResponse);
        var hasConflictingValidators = HasConflictingValidators(cachedGet, headResponse);

        if ((int)headResponse.StatusCode >= 200 &&
            (int)headResponse.StatusCode < 300 &&
            !headDirectives.NoStore &&
            !hasConflictingValidators)
        {
            var updated = UpdateCachedEntry(cachedGet, headResponse);
            try
            {
                await SetMergedEntryAsync(getCacheKey, requestUriTag, cachedGetEntry!,
                    current => ReplaceVariant(current, cachedGet, updated), ct);
            }
            catch (Exception ex)
            {
                _logger.CacheWriteFailed(request.RequestUri, ex);
            }

            var mergedHeadResponse = BuildMergedHeadResponse(headResponse, updated, request);
            headResponse.Dispose();
            AddDiagnosticHeaders(mergedHeadResponse, DiagnosticHeaders.ByPassMethod);
            return mergedHeadResponse;
        }

        if (headResponse.StatusCode == HttpStatusCode.Gone ||
            (int)headResponse.StatusCode >= 400 ||
            headDirectives.NoStore ||
            hasConflictingValidators)
        {
            try
            {
                await RemoveStreamingAwareAsync(getCacheKey, requestUriTag, ct);
            }
            catch (Exception ex)
            {
                _logger.CacheRemoveFailed(request.RequestUri, ex);
            }
        }

        AddDiagnosticHeaders(headResponse, DiagnosticHeaders.ByPassMethod);
        return headResponse;
    }

    private static HttpResponseMessage BuildMergedHeadResponse(
        HttpResponseMessage originHeadResponse,
        CachedHttpMetadata updatedCachedResponse,
        HttpRequestMessage request)
    {
        var merged = new HttpResponseMessage(originHeadResponse.StatusCode)
        {
            RequestMessage = request,
            Version = originHeadResponse.Version,
            ReasonPhrase = originHeadResponse.ReasonPhrase,
            Content = new ByteArrayContent([])
        };
        merged.Content.Headers.ContentLength = null;

        foreach (var header in updatedCachedResponse.Headers)
        {
            if (header.Key.Equals("Age", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            merged.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in updatedCachedResponse.ContentHeaders)
        {
            merged.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        UpsertHeaders(merged.Headers, CaptureHeaders(originHeadResponse.Headers));
        if (originHeadResponse.Content != null)
        {
            UpsertHeaders(merged.Content.Headers, CaptureHeaders(originHeadResponse.Content.Headers));
        }

        var originalLength = updatedCachedResponse.OriginalContentLength ??
            (updatedCachedResponse.IsCompressed ? (long?)null : updatedCachedResponse.ContentLength);
        if (!merged.Content.Headers.ContentLength.HasValue && originalLength.HasValue)
        {
            merged.Content.Headers.ContentLength = originalLength.Value;
        }

        return merged;
    }

    private static void UpsertHeaders(HttpHeaders destination, Dictionary<string, string[]> headers)
    {
        foreach (var header in headers)
        {
            destination.Remove(header.Key);
            destination.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private async Task<HttpResponseMessage?> TryServeRangeFromCachedMetadataAsync(
        CachedHttpMetadata cachedEntry,
        ByteRangeRequest requestedRange,
        HttpRequestMessage request,
        Ct ct)
    {
        if (cachedEntry.IsPartial)
        {
            return await TryServeRangeFromCachedPartialAsync(cachedEntry, requestedRange, request, ct);
        }

        using var cachedCompleteResponse = await DeserializeResponseAsync(cachedEntry, ct);
        if (cachedCompleteResponse == null)
        {
            return null;
        }

        return await TryBuildRangeResponseAsync(cachedCompleteResponse, requestedRange, request, ct);
    }

    private async Task<HttpResponseMessage?> TryServeRangeFromCachedPartialAsync(
        CachedHttpMetadata cachedPartialEntry,
        ByteRangeRequest requestedRange,
        HttpRequestMessage request,
        Ct ct)
    {
        if (!cachedPartialEntry.IsPartial ||
            !cachedPartialEntry.RangeStart.HasValue ||
            !cachedPartialEntry.RangeEnd.HasValue)
        {
            return null;
        }

        if (!cachedPartialEntry.RangeTotalLength.HasValue &&
            !requestedRange.From.HasValue &&
            requestedRange.To.HasValue)
        {
            return null;
        }

        var totalLength = cachedPartialEntry.RangeTotalLength ?? (cachedPartialEntry.RangeEnd.Value + 1);
        if (!TryResolveRange(requestedRange, totalLength, out var requestedStart, out var requestedEnd))
        {
            return null;
        }

        if (requestedStart < cachedPartialEntry.RangeStart.Value ||
            requestedEnd > cachedPartialEntry.RangeEnd.Value)
        {
            return null;
        }

        using var cachedResponse = await DeserializeResponseAsync(cachedPartialEntry, ct);
        if (cachedResponse == null)
        {
            return null;
        }

        var cachedPayload = await cachedResponse.Content.ReadAsByteArrayAsync(ct);
        var payloadRangeStart = cachedPartialEntry.RangeStart.Value;
        long payloadRangeEnd;
        try
        {
            payloadRangeEnd = checked(payloadRangeStart + cachedPayload.LongLength - 1);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (!requestedRange.From.HasValue && requestedRange.To.HasValue)
        {
            var suffixLength = requestedRange.To.Value;
            if (suffixLength <= 0 || suffixLength > cachedPayload.LongLength)
            {
                return null;
            }

            requestedEnd = payloadRangeEnd;
            requestedStart = requestedEnd - suffixLength + 1;
        }
        else if (!requestedRange.To.HasValue && requestedEnd > payloadRangeEnd)
        {
            return null;
        }

        if (requestedStart < payloadRangeStart || requestedEnd > payloadRangeEnd)
        {
            return null;
        }

        var relativeStartLong = requestedStart - payloadRangeStart;
        var relativeEndLong = requestedEnd - payloadRangeStart;
        if (relativeStartLong < 0 ||
            relativeEndLong < relativeStartLong ||
            relativeEndLong >= cachedPayload.LongLength ||
            relativeStartLong > int.MaxValue ||
            relativeEndLong > int.MaxValue)
        {
            return null;
        }

        var relativeStart = (int)relativeStartLong;
        var relativeEnd = (int)relativeEndLong;
        var sliceLength = relativeEnd - relativeStart + 1;
        var rangePayload = new byte[sliceLength];
        Array.Copy(cachedPayload, relativeStart, rangePayload, 0, sliceLength);

        var rangedResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = request,
            Version = cachedResponse.Version,
            ReasonPhrase = cachedResponse.ReasonPhrase,
            Content = new ByteArrayContent(rangePayload)
        };

        foreach (var header in cachedResponse.Headers)
        {
            rangedResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in cachedResponse.Content.Headers)
        {
            rangedResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        rangedResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(requestedStart, requestedEnd, totalLength);
        rangedResponse.Content.Headers.ContentLength = sliceLength;
        if (!rangedResponse.Headers.AcceptRanges.Contains("bytes"))
        {
            rangedResponse.Headers.AcceptRanges.Add("bytes");
        }

        return rangedResponse;
    }

    private static bool HasConflictingValidators(CachedHttpMetadata cached, HttpResponseMessage response)
    {
        var responseEtag = GetHeaderValues(response.Headers, "ETag").FirstOrDefault();
        if (!TextCompatibility.IsNullOrEmpty(cached.ETag) &&
            !TextCompatibility.IsNullOrEmpty(responseEtag) &&
            !WeakEntityTagEquals(cached.ETag, responseEtag))
        {
            return true;
        }

        var responseLastModified = response.Content?.Headers.LastModified;
        if (cached.LastModified.HasValue &&
            responseLastModified.HasValue &&
            cached.LastModified.Value != responseLastModified.Value)
        {
            return true;
        }

        return false;
    }

    private static bool TryGetSingleByteRange(HttpRequestMessage request, out ByteRangeRequest range)
    {
        range = default;
        var rangeHeader = request.Headers.Range;
        if (rangeHeader == null ||
            !string.Equals(rangeHeader.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            rangeHeader.Ranges.Count != 1)
        {
            return false;
        }

        var item = rangeHeader.Ranges.First();
        if (!item.From.HasValue && !item.To.HasValue)
        {
            return false;
        }

        range = new ByteRangeRequest(item.From, item.To);
        return true;
    }

    private static async Task<HttpResponseMessage?> TryBuildRangeResponseAsync(
        HttpResponseMessage completeResponse,
        ByteRangeRequest requestedRange,
        HttpRequestMessage request,
        Ct ct)
    {
        using var completePayloadStream = await completeResponse.Content.ReadAsStreamAsync(ct);
        if (!completePayloadStream.CanSeek)
        {
            return null;
        }

        var totalLength = completePayloadStream.Length;
        if (!TryResolveRange(requestedRange, totalLength, out var start, out var end))
        {
            return null;
        }

        var sliceLengthLong = end - start + 1;
        if (sliceLengthLong <= 0 || sliceLengthLong > int.MaxValue)
        {
            return null;
        }

        var sliceLength = (int)sliceLengthLong;
        var rangePayload = new byte[sliceLength];
        completePayloadStream.Seek(start, SeekOrigin.Begin);

        var bytesRead = 0;
        while (bytesRead < sliceLength)
        {
            var read = await completePayloadStream.ReadAsync(rangePayload.AsMemory(bytesRead), ct);
            if (read == 0)
            {
                return null;
            }

            bytesRead += read;
        }

        var partialResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = request,
            Version = completeResponse.Version,
            ReasonPhrase = completeResponse.ReasonPhrase,
            Content = new ByteArrayContent(rangePayload)
        };

        foreach (var header in completeResponse.Headers)
        {
            partialResponse.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in completeResponse.Content.Headers)
        {
            partialResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        partialResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, totalLength);
        partialResponse.Content.Headers.ContentLength = sliceLength;

        if (!partialResponse.Headers.AcceptRanges.Contains("bytes"))
        {
            partialResponse.Headers.AcceptRanges.Add("bytes");
        }

        return partialResponse;
    }

    private static bool TryResolveRange(ByteRangeRequest range, long totalLength, out long start, out long end)
    {
        start = 0;
        end = 0;

        if (totalLength <= 0)
        {
            return false;
        }

        if (range.From.HasValue && range.To.HasValue)
        {
            start = range.From.Value;
            end = range.To.Value;
        }
        else if (range.From.HasValue)
        {
            start = range.From.Value;
            end = totalLength - 1;
        }
        else
        {
            var suffixLength = range.To.GetValueOrDefault();
            if (suffixLength <= 0)
            {
                return false;
            }

            if (suffixLength >= totalLength)
            {
                start = 0;
            }
            else
            {
                start = totalLength - suffixLength;
            }

            end = totalLength - 1;
        }

        return start >= 0 && end >= start && end < totalLength;
    }

    private string NormalizeRangeHeader(RangeHeaderValue rangeHeader)
        => _cacheKeyGenerator.NormalizeRangeHeader(rangeHeader);

    private string GenerateVaryAwareCacheKey(
        HttpRequestMessage request,
        HttpMethod? cacheMethod = null,
        bool includeRange = false)
        => _cacheKeyGenerator.GenerateVaryAwareCacheKey(request, cacheMethod, includeRange);

    private static void NormalizeIfNoneMatchHeader(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues))
        {
            return;
        }

        var normalized = ifNoneMatchValues
            .SelectMany(SplitETagList)
            .Select(NormalizeETagForSending)
            .Where(static value => !string.IsNullOrEmpty(value))
            .ToArray();

        if (normalized.Length == 0)
        {
            return;
        }

        request.Headers.Remove("If-None-Match");
        request.Headers.TryAddWithoutValidation("If-None-Match", string.Join(", ", normalized));
    }

    private bool MatchesStoredVaryHeaders(CachedHttpMetadata cachedResponse, HttpRequestMessage request)
        => _cacheKeyGenerator.MatchesStoredVaryHeaders(cachedResponse, request);

    private static string GetNormalizedHeaderValue(HttpRequestMessage request, string headerName)
        => CacheKeyGenerator.GetNormalizedHeaderValue(request, headerName);
    private static string NormalizeHeaderValues(IEnumerable<string> values)
        => CacheKeyGenerator.NormalizeHeaderValues(values);

    private bool TryCreateConditionalNotModifiedResponse(
        HttpRequestMessage request,
        CachedHttpMetadata cachedResponse,
        out HttpResponseMessage response)
    {
        if (!EvaluateClientConditional(request, cachedResponse))
        {
            response = null!;
            return false;
        }

        response = CreateNotModifiedResponse(request, cachedResponse);
        ApplyAgeHeader(response, cachedResponse);
        return true;
    }

    private static bool EvaluateClientConditional(HttpRequestMessage request, CachedHttpMetadata cachedResponse)
    {
        if (request.Headers.TryGetValues("If-None-Match", out var ifNoneMatchValues))
        {
            var storedEtag = cachedResponse.ETag;
            foreach (var candidate in ifNoneMatchValues.SelectMany(SplitETagList))
            {
                if (candidate == "*")
                {
                    return true;
                }

                if (TextCompatibility.IsNullOrEmpty(storedEtag))
                {
                    continue;
                }

                if (WeakEntityTagEquals(candidate, storedEtag))
                {
                    return true;
                }
            }

            return false;
        }

        if (!request.Headers.TryGetValues("If-Modified-Since", out var ifModifiedSinceValues))
        {
            return false;
        }

        var ifModifiedSince = ifModifiedSinceValues.FirstOrDefault();
        if (!TryParseHttpDate(ifModifiedSince, out var ifModifiedSinceDate))
        {
            return false;
        }

        if (cachedResponse.LastModified.HasValue)
        {
            return cachedResponse.LastModified.Value <= ifModifiedSinceDate;
        }

        return cachedResponse.Date.HasValue && cachedResponse.Date.Value >= ifModifiedSinceDate;
    }

    private static HttpResponseMessage CreateNotModifiedResponse(HttpRequestMessage request, CachedHttpMetadata cachedResponse)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            RequestMessage = request,
            Content = new NoBodyHttpContent()
        };

        foreach (var header in cachedResponse.Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in cachedResponse.ContentHeaders)
        {
            if (NotModifiedContentHeaders.Contains(header.Key))
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return response;
    }

    private sealed class NoBodyHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static bool TryParseHttpDate(string? value, out DateTimeOffset parsed)
    {
        var httpDate = TextCompatibility.IsNullOrWhiteSpace(value)
            ? null
            : HttpCacheHeaderParser.ParseSingleHttpDate([value]);

        parsed = httpDate.GetValueOrDefault();
        return httpDate.HasValue;
    }

    private static bool WeakEntityTagEquals(string left, string right)
        => string.Equals(
            NormalizeETagForComparison(left),
            NormalizeETagForComparison(right),
            StringComparison.Ordinal);

    private static string NormalizeETagForComparison(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        if (normalized.Length >= 2 &&
            (normalized[0] == 'W' || normalized[0] == 'w'))
        {
            if (normalized[1] == '/' || normalized[1] == '\\')
            {
                normalized = normalized[2..];
            }
            else if (normalized[1] == '"')
            {
                normalized = normalized[1..];
            }
        }

        normalized = normalized.Trim();
        if (normalized.Length >= 2 &&
            normalized[0] == '"' &&
            normalized[^1] == '"')
        {
            normalized = normalized[1..^1];
        }

        return normalized;
    }

    private static string NormalizeETagForSending(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized == "*")
        {
            return normalized;
        }

        if ((normalized[0] == 'W' || normalized[0] == 'w') && normalized.Length >= 2)
        {
            if (normalized[1] == '/' || normalized[1] == '\\' || normalized[1] == '"')
            {
                return normalized;
            }
        }

        if (normalized.Length >= 2 &&
            normalized[0] == '"' &&
            normalized[^1] == '"')
        {
            return normalized;
        }

        return $"\"{normalized}\"";
    }

    private static IEnumerable<string> SplitETagList(string value)
    {
        var builder = new StringBuilder();
        var inQuotes = false;
        var consecutiveBackslashes = 0;

        foreach (var character in value)
        {
            if (character == '"' && consecutiveBackslashes % 2 == 0)
            {
                inQuotes = !inQuotes;
            }

            if (character == ',' && !inQuotes)
            {
                var etag = builder.ToString().Trim();
                if (etag.Length > 0)
                {
                    yield return etag;
                }

                builder.Clear();
                consecutiveBackslashes = 0;
                continue;
            }

            builder.Append(character);
            consecutiveBackslashes = character == '\\' ? consecutiveBackslashes + 1 : 0;
        }

        var final = builder.ToString().Trim();
        if (final.Length > 0)
        {
            yield return final;
        }
    }

    private async Task InvalidateCachedResponsesForUnsafeMethodAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        Ct ct)
    {
        if (!IsUnsafeMethod(request.Method)
            || !IsNonErrorStatus(response.StatusCode)
            || request.RequestUri == null)
        {
            return;
        }

        var targetUri = request.RequestUri;
        var urisToInvalidate = new HashSet<Uri>();
        urisToInvalidate.Add(targetUri);

        var locationUri = ResolveSameOriginUri(targetUri, response.Headers.Location);
        if (locationUri != null)
        {
            urisToInvalidate.Add(locationUri);
        }

        var contentLocationUri = ResolveSameOriginUri(targetUri, response.Content?.Headers.ContentLocation);
        if (contentLocationUri != null)
        {
            urisToInvalidate.Add(contentLocationUri);
        }

        foreach (var uri in urisToInvalidate)
        {
            var uriTag = GetUriTag(uri);
            if (uriTag == null)
            {
                continue;
            }

            try
            {
                using var invalidationScope = GetPublicationCoordinator().Capture(uriTag);
                var stripe = invalidationScope.Stripe;
                await stripe.Gate.WaitAsync(ct);
                try
                {
                    Interlocked.Increment(ref stripe.Epoch);
                    await _cache.RemoveByTagAsync(uriTag, cancellationToken: ct);
                }
                finally
                {
                    stripe.Gate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.CacheInvalidationFailed(uri, ex);
            }
        }
    }

    private static bool IsUnsafeMethod(HttpMethod method) =>
        method != HttpMethod.Get
        && method != HttpMethod.Head
        && method != HttpMethod.Options
        && method != HttpMethod.Trace;

    private static bool IsNonErrorStatus(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and < 400;

    private static Uri? ResolveSameOriginUri(Uri requestUri, Uri? candidate)
    {
        if (candidate == null)
        {
            return null;
        }

        var resolvedUri = candidate.IsAbsoluteUri ? candidate : new Uri(requestUri, candidate);
        return IsSameOrigin(requestUri, resolvedUri) ? resolvedUri : null;
    }

    private static bool IsSameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;

    private static string? GetUriTag(Uri? uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };
        return $"httpcache:uri:{builder.Uri.AbsoluteUri}";
    }

    private bool IsUsableForOnlyIfCached(CachedHttpMetadata cached, HttpRequestMessage request)
    {
        if (IsFresh(cached, request))
        {
            return true;
        }

        return CalculateCurrentAge(cached) <= CalculateSemanticLifetime(cached);
    }

    private static CachedHttpMetadata? SelectValidatorVariant(CachedHttpEntry entry)
    {
        CachedHttpMetadata? candidate = null;
        foreach (var variant in entry.Variants)
        {
            if (string.IsNullOrEmpty(variant.ETag) && !variant.LastModified.HasValue)
            {
                continue;
            }

            if (candidate == null || variant.CachedAt > candidate.CachedAt)
            {
                candidate = variant;
            }
        }

        return candidate;
    }

    private bool IsFresh(CachedHttpMetadata cached, HttpRequestMessage request)
        => _freshnessCalculator.IsFresh(cached, request);

    private TimeSpan? CalculateFreshnessLifetime(CachedHttpMetadata cached)
        => _freshnessCalculator.CalculateFreshnessLifetime(cached);

    private TimeSpan CalculateCurrentAge(CachedHttpMetadata cached)
        => _freshnessCalculator.CalculateCurrentAge(cached);

    private TimeSpan CalculateStaleness(CachedHttpMetadata cachedResponse)
        => _freshnessCalculator.CalculateStaleness(cachedResponse);

    private static bool CanServeStaleOnTransportFailure(Exception ex, CancellationToken cancellationToken)
        => FreshnessCalculator.CanServeStaleOnTransportFailure(ex, cancellationToken);

    private static bool CanServeStaleOnError(CachedHttpMetadata cachedResponse, TimeSpan staleness)
        => FreshnessCalculator.CanServeStaleOnError(cachedResponse, staleness);

    private EffectiveCacheDirectives GetEffectiveCacheDirectives(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        var cacheControlValue = string.Join(", ", GetHeaderValues(response.Headers, "Cache-Control"));

        var qualifiedNoCacheHeaderNames = ParseQualifiedNoCacheHeaderNames(cacheControlValue);
        var hasUnqualifiedNoCache = !string.IsNullOrWhiteSpace(cacheControlValue) &&
            CacheControlRegexes.UnqualifiedNoCache().IsMatch(cacheControlValue);
        var hasMustUnderstand = !string.IsNullOrWhiteSpace(cacheControlValue) &&
            CacheControlRegexes.MustUnderstand().IsMatch(cacheControlValue);
        var parsedCacheControl = string.IsNullOrWhiteSpace(cacheControlValue)
            ? default
            : HttpCacheHeaderParser.ParseCacheControl([cacheControlValue]);
        var parsedMaxAge = parsedCacheControl.MaxAge;
        var parsedSharedMaxAge = parsedCacheControl.SharedMaxAge;

        var noStore = cacheControl?.NoStore == true || ContainsDirectiveToken(cacheControlValue, "no-store");
        var noCache = hasUnqualifiedNoCache || (cacheControl?.NoCache == true && qualifiedNoCacheHeaderNames.Length == 0);
        var isPrivate = cacheControl?.Private == true || ContainsDirectiveToken(cacheControlValue, "private");
        var isPublic = cacheControl?.Public == true || ContainsDirectiveToken(cacheControlValue, "public");
        var mustRevalidate = cacheControl?.MustRevalidate == true || ContainsDirectiveToken(cacheControlValue, "must-revalidate");
        var proxyRevalidate = cacheControl?.ProxyRevalidate == true || ContainsDirectiveToken(cacheControlValue, "proxy-revalidate");
        var maxAge = _options.Mode == CacheMode.Shared
            ? cacheControl?.SharedMaxAge ?? parsedSharedMaxAge ?? cacheControl?.MaxAge ?? parsedMaxAge
            : cacheControl?.MaxAge ?? parsedMaxAge;
        var hasSharedMaxAge = cacheControl?.SharedMaxAge != null || parsedSharedMaxAge.HasValue;
        var ignoreStoredAge = false;

        if (_options.Mode == CacheMode.Shared &&
            TryGetTargetedCacheControlValue(response, out var targetedCacheControlValue) &&
            TryParseTargetedCacheControl(targetedCacheControlValue, out var targeted))
        {
            noStore = targeted.NoStore;
            noCache = targeted.NoCache;
            isPrivate = targeted.Private;
            isPublic = targeted.Public ?? (targeted.Private ? false : isPublic);
            mustRevalidate = targeted.MustRevalidate;
            proxyRevalidate = targeted.ProxyRevalidate ?? proxyRevalidate;
            maxAge = targeted.MaxAge;
            hasSharedMaxAge = targeted.MaxAge.HasValue;
        }

        if (noStore &&
            hasMustUnderstand &&
            MustUnderstandNoStoreExemptStatuses.Contains(response.StatusCode))
        {
            noStore = false;
        }

        return new EffectiveCacheDirectives(
            NoStore: noStore,
            NoCache: noCache,
            Private: isPrivate,
            Public: isPublic,
            MustRevalidate: mustRevalidate,
            ProxyRevalidate: proxyRevalidate,
            MaxAge: maxAge,
            HasSharedMaxAge: hasSharedMaxAge,
            QualifiedNoCacheHeaderNames: qualifiedNoCacheHeaderNames,
            IgnoreStoredAge: ignoreStoredAge);
    }

    private bool TryGetTargetedCacheControlValue(HttpResponseMessage response, out string value)
    {
        foreach (var headerName in EnumerateTargetedCacheControlHeaderNames())
        {
            var values = GetHeaderValues(response.Headers, headerName);
            if (values.Length > 0)
            {
                value = string.Join(", ", values);
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private IEnumerable<string> EnumerateTargetedCacheControlHeaderNames()
    {
        if (_options.TargetedCacheControlHeaderNames is not { Length: > 0 } headerNames)
        {
            yield break;
        }

        foreach (var headerName in headerNames)
        {
            if (!string.IsNullOrWhiteSpace(headerName))
            {
                yield return headerName;
            }
        }
    }

    private static string[] GetHeaderValues(HttpHeaders headers, string headerName)
    {
        if (headers.TryGetValues(headerName, out var values))
        {
            return values.ToArray();
        }

        foreach (var nonValidated in HeaderCompatibility.Read(headers))
        {
            if (string.Equals(nonValidated.Key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return nonValidated.Value;
            }
        }

        return [];
    }

    private static (TimeSpan? StaleWhileRevalidate, TimeSpan? StaleIfError) ParseStaleDirectives(HttpHeaders headers)
    {
        TimeSpan? staleWhileRevalidate = null;
        TimeSpan? staleIfError = null;
        var cacheControlValues = GetHeaderValues(headers, "Cache-Control");
        if (cacheControlValues.Length == 0)
        {
            return (staleWhileRevalidate, staleIfError);
        }

        var cacheControlString = string.Join(", ", cacheControlValues);
        var swrMatch = CacheControlRegexes.StaleWhileRevalidate().Match(cacheControlString);
        if (swrMatch.Success && int.TryParse(swrMatch.Groups[1].Value, out var swrSeconds))
        {
            staleWhileRevalidate = TimeSpan.FromSeconds(swrSeconds);
        }

        var sieMatch = CacheControlRegexes.StaleIfError().Match(cacheControlString);
        if (sieMatch.Success && int.TryParse(sieMatch.Groups[1].Value, out var sieSeconds))
        {
            staleIfError = TimeSpan.FromSeconds(sieSeconds);
        }

        return (staleWhileRevalidate, staleIfError);
    }

    private static bool ContainsDirectiveToken(string cacheControlValue, string token)
    {
        if (string.IsNullOrWhiteSpace(cacheControlValue))
        {
            return false;
        }

        return cacheControlValue
            .SplitTrimmed(',')
            .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseTargetedCacheControl(string value, out TargetedCacheDirectives directives)
    {
        directives = new TargetedCacheDirectives();
        foreach (var member in value.SplitTrimmed(','))
        {
            if (string.IsNullOrWhiteSpace(member))
            {
                continue;
            }

            var token = member.Split(';', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var rawToken = token;
            var rawEqualsIndex = rawToken.IndexOf('=');
            if (rawEqualsIndex > 0 &&
                rawEqualsIndex < rawToken.Length - 1 &&
                (char.IsWhiteSpace(rawToken[rawEqualsIndex - 1]) ||
                 char.IsWhiteSpace(rawToken[rawEqualsIndex + 1])))
            {
                directives = new TargetedCacheDirectives();
                return false;
            }

            token = NormalizeDirectiveToken(token);
            if (!IsValidDirectiveToken(token))
            {
                directives = new TargetedCacheDirectives();
                return false;
            }

            var equalsIndex = token.IndexOf('=');
            if (equalsIndex < 0)
            {
                if (token.Equals("no-store", StringComparison.OrdinalIgnoreCase))
                {
                    directives.NoStore = true;
                }
                else if (token.Equals("no-cache", StringComparison.OrdinalIgnoreCase))
                {
                    directives.NoCache = true;
                }
                else if (token.Equals("private", StringComparison.OrdinalIgnoreCase))
                {
                    directives.Private = true;
                    directives.Public = false;
                }
                else if (token.Equals("public", StringComparison.OrdinalIgnoreCase))
                {
                    directives.Public = true;
                    directives.Private = false;
                }
                else if (token.Equals("must-revalidate", StringComparison.OrdinalIgnoreCase))
                {
                    directives.MustRevalidate = true;
                }
                else if (token.Equals("proxy-revalidate", StringComparison.OrdinalIgnoreCase))
                {
                    directives.ProxyRevalidate = true;
                }

                continue;
            }

            if (equalsIndex == 0 || equalsIndex == token.Length - 1)
            {
                directives = new TargetedCacheDirectives();
                return false;
            }

            if (char.IsWhiteSpace(token[equalsIndex - 1]) ||
                char.IsWhiteSpace(token[equalsIndex + 1]))
            {
                directives = new TargetedCacheDirectives();
                return false;
            }

            var key = token[..equalsIndex];
            var rawValue = token[(equalsIndex + 1)..];
            if (key.Equals("max-age", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(rawValue, out var seconds) &&
                seconds >= 0 &&
                seconds <= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
            {
                directives.MaxAge = TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
            }
        }

        return true;
    }

    private static string NormalizeDirectiveToken(string token)
    {
        var equalsIndex = token.IndexOf('=');
        if (equalsIndex < 0)
        {
            return token;
        }

        var key = token[..equalsIndex].TrimEnd();
        var value = token[(equalsIndex + 1)..].TrimStart();
        return string.Concat(key, "=", value);
    }

    private static bool IsValidDirectiveToken(string token)
    {
        foreach (var ch in token)
        {
            if (ch == '=')
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                return false;
            }

            if (char.IsLetterOrDigit(ch))
            {
                continue;
            }

            if (ch is '!' or '#' or '$' or '%' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string[] ParseQualifiedNoCacheHeaderNames(string cacheControlValue)
    {
        if (string.IsNullOrWhiteSpace(cacheControlValue))
        {
            return [];
        }

        var matches = CacheControlRegexes.QualifiedNoCache().Matches(cacheControlValue);
        if (matches.Count == 0)
        {
            return [];
        }

        var headerNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            foreach (var value in match.Groups[1].Value
                .SplitTrimmed(','))
            {
                var headerName = value.Trim('"');
                if (!string.IsNullOrWhiteSpace(headerName) && seen.Add(headerName))
                {
                    headerNames.Add(headerName);
                }
            }
        }

        return [.. headerNames];
    }

    private bool HasAnyDirectiveHeaders(HttpResponseMessage response)
        => response.Headers.Contains("Cache-Control") ||
           EnumerateTargetedCacheControlHeaderNames().Any(response.Headers.Contains);

    private static TimeSpan? ParseAgeHeader(HttpResponseMessage response)
    {
        var ageValues = GetHeaderValues(response.Headers, "Age");
        return ageValues.Length == 0 ? null : HttpCacheHeaderParser.ParseAge(ageValues);
    }

    private static Dictionary<string, string[]> CaptureHeaders(HttpHeaders headers)
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in HeaderCompatibility.Read(headers))
        {
            values[header.Key] = header.Value;
        }

        return values;
    }

    private static HashSet<string> BuildStoredHeaderStripSet(
        HttpResponseMessage response,
        string[]? qualifiedNoCacheHeaderNames)
    {
        var headerNames = new HashSet<string>(HopByHopHeaderNames, StringComparer.OrdinalIgnoreCase);
        if (response.Headers.TryGetValues("Connection", out var connectionValues))
        {
            foreach (var connectionValue in connectionValues)
            {
                foreach (var token in connectionValue.SplitTrimmed(','))
                {
                    headerNames.Add(token);
                }
            }
        }

        if (qualifiedNoCacheHeaderNames != null)
        {
            foreach (var name in qualifiedNoCacheHeaderNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    headerNames.Add(name);
                }
            }
        }

        return headerNames;
    }

    private static HashSet<string> BuildReplayForbiddenHeaderSet(string[]? qualifiedNoCacheHeaderNames)
    {
        var forbidden = new HashSet<string>(HopByHopHeaderNames, StringComparer.OrdinalIgnoreCase);
        if (qualifiedNoCacheHeaderNames != null)
        {
            foreach (var name in qualifiedNoCacheHeaderNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    forbidden.Add(name);
                }
            }
        }

        return forbidden;
    }

    private static void RemoveHeaders(Dictionary<string, string[]> headers, HashSet<string> namesToStrip)
    {
        foreach (var key in headers.Keys.ToArray())
        {
            if (namesToStrip.Contains(key))
            {
                headers.Remove(key);
            }
        }
    }

    private static void UpsertHeaderDictionary(
        Dictionary<string, string[]> destination,
        Dictionary<string, string[]> updates)
    {
        foreach (var header in updates)
        {
            destination[header.Key] = header.Value;
        }
    }

    private static void NormalizeContentTypeHeader(Dictionary<string, string[]> headers)
    {
        if (!headers.TryGetValue("Content-Type", out var values))
        {
            return;
        }

        headers["Content-Type"] = values
            .Select(value => value.Replace("; ", ";"))
            .ToArray();
    }

    private static PartialRangeMetadata? TryGetPartialMetadata(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            return null;
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange == null ||
            !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            !contentRange.From.HasValue ||
            !contentRange.To.HasValue)
        {
            return null;
        }

        return new PartialRangeMetadata(
            IsPartial: true,
            Start: contentRange.From.Value,
            End: contentRange.To.Value,
            TotalLength: contentRange.Length);
    }

    /// <summary>
    /// Computes <see cref="HybridCacheEntryOptions"/> from cached metadata so that
    /// HybridCache evicts the entry at approximately the same time the handler would
    /// consider it unusable. The TTL encompasses freshness lifetime plus any
    /// stale-while-revalidate and stale-if-error windows.
    /// </summary>
    private HybridCacheEntryOptions CreateCacheEntryOptions(CachedHttpMetadata metadata)
    {
        var total = CalculateEntryLifetime(metadata);
        return new HybridCacheEntryOptions
        {
            Expiration = total,
            LocalCacheExpiration = total
        };
    }

    private HybridCacheEntryOptions CreateCacheEntryOptions(CachedHttpEntry entry)
    {
        var total = TimeSpan.FromSeconds(30);
        foreach (var variant in entry.Variants)
        {
            var variantLifetime = CalculateEntryLifetime(variant);
            if (variantLifetime > total)
            {
                total = variantLifetime;
            }
        }

        return new HybridCacheEntryOptions
        {
            Expiration = total,
            LocalCacheExpiration = total
        };
    }

    private TimeSpan CalculateEntryLifetime(CachedHttpMetadata metadata)
        => _freshnessCalculator.CalculateEntryLifetime(metadata);

    private TimeSpan CalculateSemanticLifetime(CachedHttpMetadata metadata)
        => _freshnessCalculator.CalculateSemanticLifetime(metadata);

    private async Task<CachedHttpEntry?> GetCacheEntryAsync(string cacheKey, Ct cancellationToken) =>
        await _cache.GetOrCreateAsync<CachedHttpEntry?>(
            cacheKey,
            _ => new ValueTask<CachedHttpEntry?>((CachedHttpEntry?)null),
            cancellationToken: cancellationToken
        );

    private async Task SetMergedEntryAsync(
        string cacheKey,
        string? requestUriTag,
        CachedHttpEntry fallbackEntry,
        Func<CachedHttpEntry, CachedHttpEntry> update,
        Ct cancellationToken)
    {
        if (StreamingEnabled)
        {
            using var context = _fillContext.Value?.Retain() ?? GetPublicationCoordinator().Capture(requestUriTag);
            await context.Stripe.Gate.WaitAsync(cancellationToken);
            try
            {
                if (context.Epoch != context.Stripe.Epoch)
                {
                    return;
                }
                var current = await GetCacheEntryAsync(cacheKey, cancellationToken) ?? new CachedHttpEntry();
                var entry = update(current);
                if (ReplacesNewerPublication(current, entry, context))
                {
                    return;
                }
                entry = new CachedHttpEntry
                {
                    Variants = entry.Variants.Select(variant =>
                        current.Variants.Any(previous => ReferenceEquals(previous, variant))
                            ? variant
                            : variant with { PublicationSession = context.Session, PublicationSequence = context.Sequence }).ToList()
                };
                if (entry.Variants.Count == 0)
                {
                    await _cache.RemoveAsync(cacheKey, cancellationToken);
                }
                else
                {
                    await _cache.SetAsync(cacheKey, entry, CreateCacheEntryOptions(entry),
                        tags: requestUriTag == null ? null : [requestUriTag], cancellationToken: cancellationToken);
                }
            }
            finally
            {
                context.Stripe.Gate.Release();
            }
            return;
        }
        var latestEntry = await GetCacheEntryAsync(cacheKey, cancellationToken) ?? fallbackEntry;
        var updatedEntry = update(latestEntry);
        if (updatedEntry.Variants.Count == 0)
        {
            await _cache.RemoveAsync(cacheKey, cancellationToken);
            return;
        }

        await _cache.SetAsync(
            cacheKey,
            updatedEntry,
            CreateCacheEntryOptions(updatedEntry),
            tags: requestUriTag == null ? null : [requestUriTag],
            cancellationToken: cancellationToken);
    }

    private CachedHttpEntry UpsertVariant(CachedHttpEntry entry, CachedHttpMetadata variant)
    {
        var variants = entry.Variants.ToList();
        var signature = VaryMatcher.BuildVariantSignature(variant);
        var existingIndex = variants.FindIndex(v =>
            string.Equals(VaryMatcher.BuildVariantSignature(v), signature, StringComparison.Ordinal));

        if (existingIndex >= 0)
        {
            variants[existingIndex] = variant;
        }
        else
        {
            variants.Add(variant);
        }

        return BuildEntryWithLimit(variants);
    }

    private CachedHttpEntry ReplaceVariant(
        CachedHttpEntry entry,
        CachedHttpMetadata existingVariant,
        CachedHttpMetadata updatedVariant)
    {
        var existingSignature = VaryMatcher.BuildVariantSignature(existingVariant);
        var variants = entry.Variants.ToList();
        var index = variants.FindIndex(v =>
            string.Equals(VaryMatcher.BuildVariantSignature(v), existingSignature, StringComparison.Ordinal));

        if (index < 0)
        {
            return UpsertVariant(entry, updatedVariant);
        }

        variants[index] = updatedVariant;
        return BuildEntryWithLimit(variants);
    }

    private CachedHttpEntry RemoveVariant(CachedHttpEntry entry, CachedHttpMetadata variantToRemove)
    {
        var signature = VaryMatcher.BuildVariantSignature(variantToRemove);
        var variants = entry.Variants
            .Where(v => !string.Equals(VaryMatcher.BuildVariantSignature(v), signature, StringComparison.Ordinal))
            .ToList();

        return BuildEntryWithLimit(variants);
    }

    private async Task RemoveVariantFromEntryAsync(
        string cacheKey,
        string? requestUriTag,
        CachedHttpEntry fallbackEntry,
        CachedHttpMetadata variantToRemove,
        Uri? requestUri,
        Ct cancellationToken)
    {
        try
        {
            await SetMergedEntryAsync(
                cacheKey,
                requestUriTag,
                fallbackEntry,
                current => RemoveVariant(current, variantToRemove),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.CacheRemoveFailed(requestUri, ex);
        }
    }

    private static CachedHttpEntry BuildEntryWithLimit(List<CachedHttpMetadata> variants)
    {
        if (variants.Count > MaxVariantsPerEntry)
        {
            variants = variants
                .OrderByDescending(v => v.CachedAt)
                .Take(MaxVariantsPerEntry)
                .ToList();
        }

        return new CachedHttpEntry
        {
            Variants = variants
        };
    }

    private bool IsResponseCacheable(
        HttpResponseMessage response,
        HttpRequestMessage? request = null,
        EffectiveCacheDirectives? directivesOverride = null)
    {
        var directives = directivesOverride ?? GetEffectiveCacheDirectives(response);

        // Don't cache if response has no-store
        if (directives.NoStore)
        {
            return false;
        }

        // Shared cache mode: MUST NOT cache responses with private directive
        if (_options.Mode == CacheMode.Shared && directives.Private)
        {
            return false;
        }

        // Responses with no-cache can be cached but must be revalidated (RFC 9111 §5.2.2.4)
        // They're cacheable if they have validators, even without explicit freshness
        if (directives.NoCache)
        {
            // Can cache if we have validators
            if (response.Headers.ETag != null || response.Content.Headers.LastModified != null)
            {
                return true;
            }
            return false;
        }

        // Don't cache if Vary: * (RFC 7234 §4.1)
        if (response.Headers.TryGetValues("Vary", out var varyValues))
        {
            if (varyValues.Any(v => v.Contains("*")))
            {
                return false;
            }
        }

        // Don't cache if content size exceeds maximum
        if (response.Content.Headers.ContentLength.HasValue)
        {
            if (response.Content.Headers.ContentLength.Value >= _options.MaxCacheableContentSize)
            {
                if (request != null)
                {
                    CacheMetrics.CacheSizeExceeded.Add(1, CacheMetrics.CreateMetricTags(request));
                }
                return false;
            }
        }

        // Don't cache if content type is not in allowed list
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType == null)
        {
            return false;
        }

        var isAllowed = _options.CacheableContentTypes.Any(allowed =>
            contentType.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            (allowed.EndsWith("/*") && contentType.StartsWith(allowed[..^2], StringComparison.OrdinalIgnoreCase)));

        if (!isAllowed)
        {
            return false;
        }

        // Check for Cache-Control header with max-age (including max-age=0)
        if (directives.MaxAge.HasValue)
        {
            return true;
        }

        // Check for Expires header
        if (response.Content.Headers.TryGetValues("Expires", out _))
        {
            return true;
        }

        // Check for Last-Modified header (allows heuristic freshness)
        if (response.Content.Headers.LastModified.HasValue)
        {
            return IsHeuristicallyCacheableStatus(response.StatusCode) || directives.Public;
        }

        // If default cache duration is configured, response is cacheable
        if (_options.FallbackCacheDuration > TimeSpan.MinValue)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Snapshots header values (unparsed on modern targets). Must be called immediately after a
    /// response is received, before any typed header access (e.g. Headers.CacheControl)
    /// parses the raw values — parsed values re-serialize reordered/case-normalized,
    /// and legacy targets can only preserve the values exposed by public enumeration.
    /// </summary>
    private static RawHeaderSnapshot CaptureRawHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in HeaderCompatibility.Read(response.Headers))
        {
            headers[header.Key] = header.Value;
        }

        var contentHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (response.Content != null)
        {
            foreach (var header in HeaderCompatibility.Read(response.Content.Headers))
            {
                contentHeaders[header.Key] = header.Value;
            }
        }

        return new RawHeaderSnapshot(headers, contentHeaders);
    }

    private sealed record RawHeaderSnapshot(
        Dictionary<string, string[]> Headers,
        Dictionary<string, string[]> ContentHeaders);

    /// <summary>
    /// Restores raw header values captured by <see cref="CaptureRawHeaders"/> onto a
    /// response whose headers may have been parsed (and thus re-serialized normalized)
    /// by typed header access, so the response is passed through verbatim.
    /// </summary>
    private static void RestoreRawHeaders(HttpResponseMessage response, RawHeaderSnapshot? rawHeaders)
    {
        if (rawHeaders == null)
        {
            return;
        }

        response.Headers.Clear();
        foreach (var header in rawHeaders.Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (response.Content == null)
        {
            if (rawHeaders.ContentHeaders.Count == 0)
            {
                return;
            }

            response.Content = new ByteArrayContent([]);
        }

        response.Content.Headers.Clear();
        foreach (var header in rawHeaders.ContentHeaders)
        {
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private async Task<CachedHttpMetadata?> SerializeResponse(HttpResponseMessage response, RawHeaderSnapshot rawHeaders, HttpRequestMessage? request = null)
    {
        var directives = GetEffectiveCacheDirectives(response);
        if (directives.NoStore)
        {
            return null;
        }

        // Check content length before reading if available
        if (response.Content.Headers.ContentLength.HasValue &&
            response.Content.Headers.ContentLength.Value > _options.MaxCacheableContentSize)
        {
            if (request != null)
            {
                CacheMetrics.CacheSizeExceeded.Add(1, CacheMetrics.CreateMetricTags(request));
            }
            return null;
        }

        // Raw content headers, captured before reading/replacing the content
        var originalContentHeaders = rawHeaders.ContentHeaders;

        // Use SegmentedBuffer to avoid LOH allocations for large responses
        var stream = await response.Content.ReadAsStreamAsync();
        using var segmentedBuffer = new SegmentedBuffer();
        var buffer = ArrayPool<byte>.Shared.Rent(81920); // 80KB buffer

        byte[] finalContent;
        try
        {
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                // Check size limit while reading
                if (segmentedBuffer.Length + bytesRead > _options.MaxCacheableContentSize)
                {
                    // Content too large - restore it so caller can use the response
                    segmentedBuffer.Write(buffer.AsSpan(0, bytesRead));
                    var content = segmentedBuffer.ToArray();
                    response.Content = new ByteArrayContent(content);
                    foreach (var header in originalContentHeaders)
                    {
                        response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    if (request != null)
                    {
                        CacheMetrics.CacheSizeExceeded.Add(1, CacheMetrics.CreateMetricTags(request));
                    }
                    return null;
                }
                segmentedBuffer.Write(buffer.AsSpan(0, bytesRead));
            }

            finalContent = segmentedBuffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Apply compression if enabled and content is large enough
        var isCompressed = false;
        var originalContent = finalContent;
        var contentToCache = finalContent;
        if (_options.CompressionThreshold > 0 &&
            finalContent.Length >= _options.CompressionThreshold &&
            IsCompressible(response.Content.Headers.ContentType?.MediaType))
        {
            contentToCache = CompressContent(finalContent);
            isCompressed = true;
        }

        var requestUriTag = GetUriTag(request?.RequestUri);
        IEnumerable<string>? contentTags = requestUriTag == null ? null : [requestUriTag];
        var contentKey = CreateContentKey(contentToCache);
        var contentStore = ResolveContentStore(originalContent.Length);
        using var storedStream = new MemoryStream(contentToCache, writable: false);
        await contentStore.WriteAsync(contentKey, storedStream, contentToCache.LongLength, contentTags, Ct.None);

        response.Content = new ByteArrayContent(originalContent);
        foreach (var header in originalContentHeaders)
        {
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return CreateMetadata(response, rawHeaders, request, contentKey, contentToCache.LongLength, originalContent.LongLength, isCompressed,
            ReferenceEquals(contentStore, _largeContentStore));
    }

    private CachedHttpMetadata CreateMetadata(HttpResponseMessage response, RawHeaderSnapshot rawHeaders,
        HttpRequestMessage? request, string contentKey, long storedLength, long originalLength, bool isCompressed, bool storedExternally)
    {
        var directives = GetEffectiveCacheDirectives(response);
        var originalContentHeaders = rawHeaders.ContentHeaders;
        var headers = new Dictionary<string, string[]>(rawHeaders.Headers, StringComparer.OrdinalIgnoreCase);
        var contentHeaders = new Dictionary<string, string[]>(originalContentHeaders, StringComparer.OrdinalIgnoreCase);

        var stripHeaderNames = BuildStoredHeaderStripSet(response, directives.QualifiedNoCacheHeaderNames);
        RemoveHeaders(headers, stripHeaderNames);
        RemoveHeaders(contentHeaders, stripHeaderNames);
        NormalizeContentTypeHeader(contentHeaders);

        // Determine MaxAge from effective directives.
        var maxAge = directives.MaxAge;

        // Extract ETag
        string? etag = rawHeaders.Headers.TryGetValue("ETag", out var etagValues)
            ? etagValues.FirstOrDefault()
            : null;

        // Extract Last-Modified
        var lastModified = response.Content.Headers.LastModified;

        // Extract Expires using strict HTTP-date parsing
        DateTimeOffset? expires = null;
        var hasExpires = response.Content.Headers.TryGetValues("Expires", out var expiresValues);
        if (hasExpires)
        {
            expires = HttpCacheHeaderParser.ParseSingleHttpDate(expiresValues!) ?? DateTimeOffset.MinValue;
        }

        // If no explicit caching headers, use default cache duration
        if (!maxAge.HasValue &&
            !hasExpires &&
            !response.Content.Headers.LastModified.HasValue &&
            _options.FallbackCacheDuration > TimeSpan.MinValue)
        {
            maxAge = _options.FallbackCacheDuration;
        }

        // Extract Date
        var date = response.Headers.Date;

        // Extract Age
        var age = directives.IgnoreStoredAge ? TimeSpan.Zero : ParseAgeHeader(response);

        // Extract Vary headers and their values from the request
        string[]? varyHeaders = null;
        Dictionary<string, string>? varyHeaderValues = null;

        if (response.Headers.TryGetValues("Vary", out var varyHeaderList))
        {
            // Parse comma-separated Vary header
            varyHeaders = varyHeaderList
                .SelectMany(v => v.Split(','))
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrEmpty(v) && v != "*")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (varyHeaders.Length > 0 && request != null)
            {
                varyHeaderValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var varyHeader in varyHeaders)
                {
                    if (request.Headers.TryGetValues(varyHeader, out var requestHeaderValues))
                    {
                        var normalizedValue = VaryMatcher.NormalizeHeaderValue(requestHeaderValues);
                        varyHeaderValues[varyHeader] = normalizedValue;
                    }
                    else
                    {
                        // Header not present in request
                        varyHeaderValues[varyHeader] = string.Empty;
                    }
                }
            }
        }

        // Extract RFC 5861 stale-while-revalidate and stale-if-error
        TimeSpan? staleWhileRevalidate = null;
        TimeSpan? staleIfError = null;
        var mustRevalidate = directives.MustRevalidate;
        var proxyRevalidate = directives.ProxyRevalidate;
        var noCache = directives.NoCache;

        (staleWhileRevalidate, staleIfError) = ParseStaleDirectives(response.Headers);

        var partialMetadata = TryGetPartialMetadata(response);

        return new CachedHttpMetadata
        {
            StatusCode = (int)response.StatusCode,
            ContentKey = contentKey,
            ContentLength = storedLength,
            OriginalContentLength = originalLength,
            Headers = headers,
            ContentHeaders = contentHeaders,
            CachedAt = _timeProvider.GetUtcNow(),
            MaxAge = maxAge,
            HasSharedMaxAge = directives.HasSharedMaxAge,
            ETag = etag,
            LastModified = lastModified,
            Expires = expires,
            Date = date,
            Age = age,
            VaryHeaders = varyHeaders,
            VaryHeaderValues = varyHeaderValues,
            StaleWhileRevalidate = staleWhileRevalidate,
            StaleIfError = staleIfError,
            MustRevalidate = mustRevalidate,
            ProxyRevalidate = proxyRevalidate,
            NoCache = noCache,
            QualifiedNoCacheHeaderNames = directives.QualifiedNoCacheHeaderNames,
            IgnoreStoredAge = directives.IgnoreStoredAge,
            IsCompressed = isCompressed,
            IsPartial = partialMetadata?.IsPartial ?? false,
            RangeStart = partialMetadata?.Start,
            RangeEnd = partialMetadata?.End,
            RangeTotalLength = partialMetadata?.TotalLength,
            IsStoredExternally = storedExternally
        };
    }

    private async Task<HttpResponseMessage?> DeserializeResponseAsync(CachedHttpMetadata metadata, Ct cancellationToken)
    {
        if (metadata.IsStoredExternally && _largeContentStore == null)
        {
            return null;
        }
        var contentStore = metadata.IsStoredExternally && _largeContentStore != null
            ? _largeContentStore
            : _contentStore;

        // Get content from separate storage
        var contentStream = await contentStore.OpenReadAsync(metadata.ContentKey, cancellationToken);
        if (contentStream == null)
        {
            // Content missing - metadata is orphaned
            _logger.CachedContentMissing(metadata.ContentKey);
            return null;
        }

        if (metadata.IsCompressed)
        {
            contentStream = new GZipStream(contentStream, CompressionMode.Decompress);
        }

        var response = new HttpResponseMessage((HttpStatusCode)metadata.StatusCode)
        {
            Content = new StreamContent(contentStream)
        };

        var forbiddenHeaderNames = BuildReplayForbiddenHeaderSet(metadata.QualifiedNoCacheHeaderNames);

        foreach (var header in metadata.Headers)
        {
            if (forbiddenHeaderNames.Contains(header.Key))
            {
                continue;
            }

            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var header in metadata.ContentHeaders)
        {
            if (forbiddenHeaderNames.Contains(header.Key))
            {
                continue;
            }

            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var ageSeconds = Math.Max(0L, (long)Math.Floor(CalculateCurrentAge(metadata).TotalSeconds));
        response.Headers.Remove("Age");
        response.Headers.TryAddWithoutValidation("Age", ageSeconds.ToString(CultureInfo.InvariantCulture));

        return response;
    }

    private IHttpCacheContentStore ResolveContentStore(long contentLength) =>
        _largeContentStore != null
        && _options.LargeContentThreshold > 0
        && contentLength >= _options.LargeContentThreshold
            ? _largeContentStore
            : _contentStore;

    private string CreateContentKey(byte[] content) =>
        $"{_options.ContentKeyPrefix}{Hashing.ToHex(Hashing.Sha256(content))}";

    private bool IsCompressible(string? mediaType)
    {
        if (TextCompatibility.IsNullOrEmpty(mediaType))
        {
            return false;
        }

        return _options.CompressibleContentTypes.Any(contentType =>
            contentType.EndsWith("/*")
                ? mediaType.StartsWith(contentType[..^2], StringComparison.OrdinalIgnoreCase)
                : mediaType.StartsWith(contentType, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] CompressContent(byte[] content)
    {
        using var outputStream = new MemoryStream();
        using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzipStream.Write(content, 0, content.Length);
        }
        return outputStream.ToArray();
    }

    private static HttpCacheHeaderParser.CacheControlDirectives ParseResponseCacheControl(HttpResponseMessage response)
        => response.Headers.TryGetValues("Cache-Control", out var values)
            ? HttpCacheHeaderParser.ParseCacheControl(values)
            : default;

    private void ApplyAgeHeader(HttpResponseMessage response, CachedHttpMetadata cachedResponse)
    {
        var currentAge = CalculateCurrentAge(cachedResponse);
        var ageSeconds = Math.Max(0L, (long)Math.Floor(currentAge.TotalSeconds));
        response.Headers.Remove("Age");
        response.Headers.TryAddWithoutValidation("Age", ageSeconds.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsHeuristicallyCacheableStatus(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NonAuthoritativeInformation => true,
            HttpStatusCode.NoContent => true,
            HttpStatusCode.PartialContent => true,
            HttpStatusCode.MultipleChoices => true,
            HttpStatusCode.MovedPermanently => true,
            (HttpStatusCode)308 => true,
            HttpStatusCode.NotFound => true,
            HttpStatusCode.MethodNotAllowed => true,
            HttpStatusCode.Gone => true,
            HttpStatusCode.RequestUriTooLong => true,
            HttpStatusCode.NotImplemented => true,
            _ => false
        };

    private void AddDiagnosticHeaders(HttpResponseMessage response, string reason, CachedHttpMetadata? cachedResponse = null)
    {
        if (!_options.IncludeDiagnosticHeaders)
        {
            return;
        }

        response.Headers.TryAddWithoutValidation(DiagnosticHeaders.CacheDiagnostic, reason);

        if (cachedResponse != null)
        {
            var age = _timeProvider.GetUtcNow() - cachedResponse.CachedAt;
            response.Headers.TryAddWithoutValidation(DiagnosticHeaders.CacheAge, $"{(int)age.TotalSeconds}s");

            if (cachedResponse.MaxAge.HasValue)
            {
                response.Headers.TryAddWithoutValidation(DiagnosticHeaders.CacheMaxAge, $"{(int)cachedResponse.MaxAge.Value.TotalSeconds}s");
            }

            if (cachedResponse.IsCompressed)
            {
                response.Headers.TryAddWithoutValidation(DiagnosticHeaders.CacheCompressed, "true");
            }
        }
    }

    private static byte[] DecompressContent(byte[] compressedContent)
    {
        using var inputStream = new MemoryStream(compressedContent);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        gzipStream.CopyTo(outputStream);
        return outputStream.ToArray();
    }

    private readonly record struct ByteRangeRequest(long? From, long? To);

    private readonly record struct PartialRangeMetadata(
        bool IsPartial,
        long Start,
        long End,
        long? TotalLength);

    private sealed class TargetedCacheDirectives
    {
        public bool NoStore { get; set; }
        public bool NoCache { get; set; }
        public bool Private { get; set; }
        public bool? Public { get; set; }
        public bool MustRevalidate { get; set; }
        public bool? ProxyRevalidate { get; set; }
        public TimeSpan? MaxAge { get; set; }
    }

    private sealed record EffectiveCacheDirectives(
        bool NoStore,
        bool NoCache,
        bool Private,
        bool Public,
        bool MustRevalidate,
        bool ProxyRevalidate,
        TimeSpan? MaxAge,
        bool HasSharedMaxAge,
        string[] QualifiedNoCacheHeaderNames,
        bool IgnoreStoredAge);
}
