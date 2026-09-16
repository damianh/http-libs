using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;

namespace DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob;

/// <summary>Registers Azure Blob storage independently of the HTTP handler.</summary>
public static class AzureBlobContentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers one singleton large-content store using an existing registered
    /// <see cref="BlobContainerClient"/>. The application controls client credentials and lifetime.
    /// </summary>
    public static IServiceCollection AddHttpHybridCacheAzureBlobContentStore(
        this IServiceCollection services, Action<AzureBlobContentStoreOptions>? configure = null)
    {
        Guard.NotNull(services);
        var options = new AzureBlobContentStoreOptions();
        configure?.Invoke(options);
        services.AddSingleton<ILargeHttpCacheContentStore>(provider =>
            new AzureBlobContentStore(provider.GetRequiredService<BlobContainerClient>(), options));
        return services;
    }
}
