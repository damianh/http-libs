using Google.Cloud.Storage.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>Registers the Google Cloud Storage large-body store without a handler dependency.</summary>
public static class GoogleCloudStorageContentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers one large-content backend using an application-registered <see cref="StorageClient"/>.
    /// </summary>
    public static IServiceCollection AddHttpHybridCacheGoogleCloudStorageContentStore(
        this IServiceCollection services,
        Action<GoogleCloudStorageContentStoreOptions> configure)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        services.AddOptions<GoogleCloudStorageContentStoreOptions>().Configure(configure);
        services.AddSingleton<ILargeHttpCacheContentStore>(provider =>
            new GoogleCloudStorageContentStore(
                provider.GetRequiredService<StorageClient>(),
                provider.GetRequiredService<IOptions<GoogleCloudStorageContentStoreOptions>>().Value));
        return services;
    }
}
