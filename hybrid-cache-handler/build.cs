#:project ../.github/BuildHelpers/BuildHelpers.csproj

using static BuildHelpers.Targets;
using static Bullseye.Targets;
using static SimpleExec.Command;

SharedTargets(
    "hybrid-cache-handler/hybrid-cache-handler.slnf",
    "hybrid-cache-handler/src/HttpHybridCacheHandler",
    registerPack: false);

var packages = new (string Key, string Project)[]
{
    ("handler", "HttpHybridCacheHandler"),
    ("contentstore", "HttpHybridCacheHandler.ContentStore"),
    ("azureblob", "HttpHybridCacheHandler.ContentStore.AzureBlob"),
    ("s3", "HttpHybridCacheHandler.ContentStore.S3"),
    ("gcs", "HttpHybridCacheHandler.ContentStore.GoogleCloudStorage"),
    ("filesystem", "HttpHybridCacheHandler.ContentStore.FileSystem"),
};

foreach (var (key, project) in packages)
{
    PackTarget($"pack-{key}", $"hybrid-cache-handler/src/{project}",
        $"hybrid-cache-handler/artifacts/{key}");
    if (key != "contentstore")
    {
        TestTarget($"test-{key}", $"hybrid-cache-handler/test/{project}.Tests");
    }
}

// Preserve the product's single-package default; use pack-all explicitly for local/CI bundles.
AggregateTarget(Pack, ["pack-handler"]);
AggregateTarget("pack-all", packages.Select(package => $"pack-{package.Key}"));
AggregateTarget(Test, packages.Where(package => package.Key != "contentstore")
    .Select(package => $"test-{package.Key}"));

const string compatibilityProject = "hybrid-cache-handler/test/HttpHybridCacheHandler.Compatibility.Tests";
var repositoryRoot = Directory.GetCurrentDirectory();
while (!Directory.Exists(Path.Combine(repositoryRoot, ".git")) && !File.Exists(Path.Combine(repositoryRoot, ".git")))
{
    repositoryRoot = Directory.GetParent(repositoryRoot)?.FullName
        ?? throw new InvalidOperationException("Could not find the repository root.");
}

foreach (var (target, framework, properties) in new[]
{
    ("test-compatibility-modern", "net10.0", ""),
    ("test-compatibility-standard", "net10.0", "-p:CompatibilityTargetFramework=netstandard2.0"),
    ("test-compatibility-framework", "net472", "")
})
{
    Target(target, async () =>
    {
        if (framework == "net472" && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Run the net472 test executable on Windows.");
        }
        await RunAsync("dotnet",
            $"test --project {compatibilityProject} -c Release -f {framework} {properties} " +
            $"--report-xunit-trx --report-xunit-trx-filename {target}-tests.trx", repositoryRoot);
    });
}

AggregateTarget("test-compatibility", OperatingSystem.IsWindows()
    ? ["test-compatibility-modern", "test-compatibility-standard", "test-compatibility-framework"]
    : ["test-compatibility-modern", "test-compatibility-standard"]);

Target("check-packages", dependsOn: ["pack-all"], () =>
    RunAsync("pwsh", $"-NoProfile -File {compatibilityProject}/Verify-Packages.ps1", repositoryRoot));

DefaultTarget(dependsOn:
[
    Build,
    Test,
]);

await BuildHelpers.Targets.RunTargetsAndExitAsync(args);
