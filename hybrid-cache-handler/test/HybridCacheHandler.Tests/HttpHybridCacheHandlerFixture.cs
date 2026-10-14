// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DamianH.Http.HybridCacheHandler;

public class HttpHybridCacheHandlerFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly FakeTimeProvider _fakeTimeProvider = new();

    public HttpHybridCacheHandlerFixture(
        HttpMessageHandler? primaryHandler = null,
        Action<HttpHybridCacheHandlerOptions>? configureHandlerOptions = null,
        HybridCache? customCache = null,
        ILargeHttpCacheContentStore? largeContentStore = null,
        Action<IServiceCollection>? configureServices = null)
    {
        primaryHandler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
        };
        configureHandlerOptions ??= _ => { };

        var serviceCollection = new ServiceCollection()
            .AddSingleton<TimeProvider>(_fakeTimeProvider)
            .AddLogging(logging => logging.AddConsole())
            .AddHttpHybridCacheHandler(configureHandlerOptions)
            .AddHttpClient("CachingClient")
            .ConfigurePrimaryHttpMessageHandler(_ => primaryHandler)
            .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>())
            .Services;
        configureServices?.Invoke(serviceCollection);

        // If custom cache provided, replace the keyed HybridCache registration
        if (customCache != null)
        {
            var descriptor = serviceCollection.FirstOrDefault(d =>
                d.IsKeyedService
                && d.ServiceType == typeof(HybridCache)
                && Equals(d.ServiceKey, ServiceCollectionExtensions.HybridCacheKey));
            if (descriptor != null)
            {
                serviceCollection.Remove(descriptor);
            }
            serviceCollection.AddKeyedSingleton(ServiceCollectionExtensions.HybridCacheKey, customCache);
        }

        if (largeContentStore != null)
        {
            serviceCollection.AddSingleton<ILargeHttpCacheContentStore>(largeContentStore);
        }

        _services = serviceCollection.BuildServiceProvider();
    }

    public TimeProvider TimeProvider => _fakeTimeProvider;

    public HttpClient CreateClient()
    {
        var httpClientFactory = _services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("CachingClient");
        return client;
    }

    public void AdvanceTime(TimeSpan delta)
    {
        _fakeTimeProvider.Advance(delta);
        TriggerMemoryCacheExpirationScan();
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        _fakeTimeProvider.SetUtcNow(value);
        TriggerMemoryCacheExpirationScan();
    }

    private void TriggerMemoryCacheExpirationScan()
    {
        // MemoryCache uses timers for periodic expiration scanning, which don't respect TimeProvider.
        // Calling Compact with a tiny percentage forces it to scan for expired entries immediately.
        var memoryCache = _services.GetService<IMemoryCache>() as TimeProviderMemoryCache;
        memoryCache?.Compact(0.0001);
    }

    public async ValueTask DisposeAsync() => await _services.DisposeAsync();
}
