using System.Reflection;
using System.Runtime.Versioning;
using DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob;
using DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;

namespace DamianH.HttpHybridCacheHandler.Compatibility.Tests;

public sealed class AssetSelectionTests
{
#if STANDARD_ASSETS
    internal const string ExpectedFramework = ".NETStandard,Version=v2.0";
#elif NETFRAMEWORK
    internal const string ExpectedFramework = ".NETFramework,Version=v4.7.2";
#else
    internal const string ExpectedFramework = ".NETCoreApp,Version=v10.0";
#endif

    [Fact]
    public void All_six_loaded_assemblies_are_the_requested_assets()
    {
        var types = new[]
        {
            typeof(HttpHybridCacheHandler), typeof(IHttpCacheContentStore),
            typeof(AzureBlobContentStore), typeof(S3ContentStore),
            typeof(GoogleCloudStorageContentStore), typeof(FileSystemContentStore)
        };
        foreach (var type in types)
        {
            var assembly = type.Assembly;
            Assert.Equal(ExpectedFramework, assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
            TestContext.Current.TestOutputHelper!.WriteLine($"{assembly.GetName().Name}: {ExpectedFramework}; {assembly.Location}");
        }
        TestContext.Current.TestOutputHelper!.WriteLine($"Host: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }
}
