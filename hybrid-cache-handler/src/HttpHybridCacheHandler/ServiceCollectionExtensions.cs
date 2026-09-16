// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.HttpHybridCacheHandler;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;
#pragma warning restore IDE0130

/// <summary>
///     Provides extension methods for configuring hybrid cache HTTP handlers in an IServiceCollection.
/// </summary>
/// <remarks>
///     This class contains extension methods that simplify the registration of hybrid cache HTTP handlers
///     and related services within a dependency injection container. These methods are intended to be used during
///     application startup to configure caching behavior for HTTP requests.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     The service key used to register the keyed <see cref="HybridCache"/> instance
    ///     dedicated to <see cref="HttpHybridCacheHandler"/>.
    /// </summary>
    public const string HybridCacheKey = "DamianH.HttpHybridCacheHandler";

    extension(IServiceCollection serviceCollection)
    {
        /// <summary>
        ///     Adds and configures a HttpHybridCacheHandler and its related services to the current IServiceCollection.
        /// </summary>
        /// <remarks>
        ///     This method registers the HttpHybridCacheHandler as a transient service and
        ///     configures its options. It also ensures that required dependencies, such as the TimeProvider,
        ///     <see cref="IMemoryCache"/> (via <see cref="TimeProviderMemoryCache"/>), and a keyed
        ///     <see cref="HybridCache"/> instance, are available in the service collection.
        ///     Note: <see cref="TimeProviderMemoryCache"/> is registered as <see cref="IMemoryCache"/>
        ///     which will be visible to the consumer's DI container.
        /// </remarks>
        /// <param name="configure">
        ///     A delegate that configures the HttpHybridCacheHandlerOptions used to customize the behavior of the
        ///     HttpHybridCacheHandler. Cannot be null.
        /// </param>
        /// <returns>
        ///     The IServiceCollection instance with the HttpHybridCacheHandler and related services registered. This
        ///     enables method chaining.
        /// </returns>
        public IServiceCollection AddHttpHybridCacheHandler(Action<HttpHybridCacheHandlerOptions> configure)
        {
            serviceCollection.AddOptions();
            serviceCollection.TryAddSingleton(TimeProvider.System);
            serviceCollection.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            serviceCollection.AddOptions<TimeProviderMemoryCacheOptions>();
            serviceCollection.TryAddSingleton<TimeProviderMemoryCache>();
            serviceCollection.TryAddSingleton<IMemoryCache>(sp => sp.GetRequiredService<TimeProviderMemoryCache>());
            // Peek at handler options to align HybridCache's MaximumPayloadBytes with MaxCacheableContentSize.
            // HybridCache defaults to 1 MiB; responses between 1 MiB and MaxCacheableContentSize would
            // silently never reach L2 without this alignment.
            var previewOptions = new HttpHybridCacheHandlerOptions();
            configure(previewOptions);
            serviceCollection.TryAddSingleton<IHttpCacheContentStore>(sp =>
            {
                var cache = sp.GetRequiredKeyedService<HybridCache>(HybridCacheKey);
                return new ContentCache(cache);
            });
            serviceCollection.AddKeyedHybridCache(HybridCacheKey, options =>
            {
                // Use a large default expiration so that HybridCache entries are not evicted
                // before the handler's own RFC 9111 freshness logic (IsFresh) can evaluate them.
                // Individual SetAsync calls pass per-entry options with accurate TTLs.
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromHours(24),
                    LocalCacheExpiration = TimeSpan.FromHours(24)
                };
                // Align HybridCache's payload limit with the handler's content size limit so that
                // responses up to MaxCacheableContentSize are not silently dropped by HybridCache.
                // HybridCache internally casts MaximumPayloadBytes to int (checked), so cap at int.MaxValue
                // to avoid OverflowException when MaxCacheableContentSize is long.MaxValue ("unlimited").
                options.MaximumPayloadBytes = Math.Min(previewOptions.MaxCacheableContentSize, int.MaxValue);
            });
            serviceCollection.AddTransient<HttpHybridCacheHandler>();
            serviceCollection.AddOptions<HttpHybridCacheHandlerOptions>()
                .Configure(options => ApplyPreviewOptions(options, previewOptions));
            return serviceCollection;
        }
 
        private static void ApplyPreviewOptions(HttpHybridCacheHandlerOptions options, HttpHybridCacheHandlerOptions previewOptions)
        {
            options.HeuristicFreshnessPercent = previewOptions.HeuristicFreshnessPercent;
            options.HeuristicFreshnessMinimum = previewOptions.HeuristicFreshnessMinimum;
            options.VaryHeaders = previewOptions.VaryHeaders;
            options.MaxCacheableContentSize = Math.Min(previewOptions.MaxCacheableContentSize, int.MaxValue);
            options.FallbackCacheDuration = previewOptions.FallbackCacheDuration;
            options.CompressionThreshold = previewOptions.CompressionThreshold;
            options.LargeContentThreshold = previewOptions.LargeContentThreshold;
            options.SpoolMemoryThreshold = previewOptions.SpoolMemoryThreshold;
            options.MaxSpoolDiskBytes = previewOptions.MaxSpoolDiskBytes;
            options.MaxConcurrentDiskSpools = previewOptions.MaxConcurrentDiskSpools;
            options.SpoolDirectory = previewOptions.SpoolDirectory;
            options.CompressibleContentTypes = previewOptions.CompressibleContentTypes;
            options.CacheableContentTypes = previewOptions.CacheableContentTypes;
            options.IncludeDiagnosticHeaders = previewOptions.IncludeDiagnosticHeaders;
            options.Mode = previewOptions.Mode;
            options.TargetedCacheControlHeaderNames = previewOptions.TargetedCacheControlHeaderNames;
        }

        /// <summary>
        /// Registers the optional content store used for large cached responses.
        /// </summary>
        public IServiceCollection AddHttpHybridCacheLargeContentStore<
#if NET10_0_OR_GREATER
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
#else
            TStore>()
#endif
            where TStore : class, ILargeHttpCacheContentStore
        {
            serviceCollection.TryAddSingleton<TStore>();
            serviceCollection.TryAddSingleton<ILargeHttpCacheContentStore>(sp => sp.GetRequiredService<TStore>());
            return serviceCollection;
        }
    }
}
