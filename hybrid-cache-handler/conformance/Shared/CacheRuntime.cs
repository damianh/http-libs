// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DamianH.HttpHybridCacheHandler;
using DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conformance;

internal static class CacheRuntime
{
    internal static void AddServices(IServiceCollection services, bool fileSystem, string? root)
    {
        services.AddHybridCache();
        if (fileSystem)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("--content-root is required.");
            services.AddHttpHybridCacheFileSystemContentStore(store =>
            {
                store.RootDirectory = root!;
                store.MaximumAge = TimeSpan.FromDays(1);
                store.MaximumTotalBytes = 1024L * 1024 * 1024;
            });
        }
    }

    internal static HttpHybridCacheHandler CreateHandler(HttpMessageHandler transport, IServiceProvider services, bool fileSystem) =>
        new(transport, services.GetRequiredService<HybridCache>(), TimeProvider.System, contentStore: null,
            new HttpHybridCacheHandlerOptions
            {
                Mode = CacheMode.Shared,
                MaxCacheableContentSize = 50 * 1024 * 1024,
                LargeContentThreshold = fileSystem ? 1 : 1024 * 1024,
                VaryHeaders = []
            },
            services.GetRequiredService<ILogger<HttpHybridCacheHandler>>(),
            services.GetService<ILargeHttpCacheContentStore>());

    internal static string Verify(string framework)
    {
        var expected = framework switch
        {
            "net10.0" => ".NETCoreApp,Version=v10.0",
            "netstandard2.0" => ".NETStandard,Version=v2.0",
            "net472" => ".NETFramework,Version=v4.7.2",
            _ => throw new ArgumentException("Unsupported implementation: " + framework)
        };
        var runtime = RuntimeInformation.FrameworkDescription;
        if (framework == "net472" && !runtime.StartsWith(".NET Framework", StringComparison.Ordinal))
            throw new InvalidOperationException("net472 conformance MUST run on the real .NET Framework CLR: " + runtime);
        if (framework != "net472" && !runtime.StartsWith(".NET 10.", StringComparison.Ordinal))
            throw new InvalidOperationException("Conformance front-end requires .NET 10: " + runtime);

        var descriptions = new List<string> { framework, runtime };
        foreach (var assembly in new[]
                 {
                     typeof(HttpHybridCacheHandler).Assembly,
                     typeof(ILargeHttpCacheContentStore).Assembly,
                     typeof(FileSystemContentStoreOptions).Assembly
                 })
        {
            var target = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            if (target != expected)
                throw new InvalidOperationException($"{assembly.GetName().Name}: expected {expected}, loaded {target} ({assembly.Location}).");
            descriptions.Add($"{assembly.GetName().Name}: {target} ({assembly.Location})");
        }
        return string.Join(Environment.NewLine, descriptions);
    }
}
