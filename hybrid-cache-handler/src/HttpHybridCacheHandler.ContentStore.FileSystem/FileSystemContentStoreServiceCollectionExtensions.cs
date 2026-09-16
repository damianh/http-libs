using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;

/// <summary>Dependency injection registration for the filesystem large-body store.</summary>
public static class FileSystemContentStoreServiceCollectionExtensions
{
    /// <summary>Registers one filesystem-backed large-content store, without depending on the HTTP handler.</summary>
    public static IServiceCollection AddHttpHybridCacheFileSystemContentStore(
        this IServiceCollection services,
        Action<FileSystemContentStoreOptions> configure)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ILargeHttpCacheContentStore>(provider => new FileSystemContentStore(
            provider.GetRequiredService<IOptions<FileSystemContentStoreOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<FileSystemContentStore>>() ?? NullLogger<FileSystemContentStore>.Instance));
        return services;
    }
}
