using System.Reflection;
using System.Runtime.Versioning;
using DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob;
using DamianH.Http.HybridCacheHandler.ContentStore.FileSystem;

namespace DamianH.Http.HybridCacheHandler.Compatibility.Tests;

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
            (Type: typeof(HttpHybridCacheHandler), AssemblyName: "DamianH.Http.HybridCacheHandler"),
            (Type: typeof(IHttpCacheContentStore), AssemblyName: "DamianH.Http.HybridCacheHandler.ContentStore"),
            (Type: typeof(AzureBlobContentStore), AssemblyName: "DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob"),
            (Type: typeof(S3ContentStore), AssemblyName: "DamianH.Http.HybridCacheHandler.ContentStore.S3"),
            (Type: typeof(GoogleCloudStorageContentStore), AssemblyName: "DamianH.Http.HybridCacheHandler.ContentStore.GoogleCloudStorage"),
            (Type: typeof(FileSystemContentStore), AssemblyName: "DamianH.Http.HybridCacheHandler.ContentStore.FileSystem")
        };
        foreach (var (type, assemblyName) in types)
        {
            var assembly = type.Assembly;
            Assert.Equal(assemblyName, assembly.GetName().Name);
            Assert.StartsWith("DamianH.Http.HybridCacheHandler", type.Namespace);
            Assert.Equal(ExpectedFramework, assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
            TestContext.Current.TestOutputHelper!.WriteLine($"{assembly.GetName().Name}: {ExpectedFramework}; {assembly.Location}");
        }
        TestContext.Current.TestOutputHelper!.WriteLine($"Host: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
    }
}
