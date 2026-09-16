using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>Registers the optional Amazon S3 large-body store without depending on the handler.</summary>
public static class S3ContentStoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton large-body store using an application-registered Amazon.S3.IAmazonS3.
    /// Register only one large-body backend per handler setup.
    /// </summary>
    public static IServiceCollection AddHttpHybridCacheS3ContentStore(
        this IServiceCollection services,
        Action<S3ContentStoreOptions> configure)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        services.AddOptions<S3ContentStoreOptions>().Configure(configure);
        services.AddSingleton<S3ContentStore>(provider => new S3ContentStore(
            provider.GetRequiredService<Amazon.S3.IAmazonS3>(),
            provider.GetRequiredService<IOptions<S3ContentStoreOptions>>()));
        services.AddSingleton<ILargeHttpCacheContentStore>(provider => provider.GetRequiredService<S3ContentStore>());
        return services;
    }
}
