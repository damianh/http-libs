using Google.Cloud.Storage.V1;

namespace DamianH.Http.HybridCacheHandler;

public class GoogleCloudStorageContentStoreIntegrationTests
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("HTTP_CACHE_GCS_RUN_INTEGRATION") == "1";

    [Theory(
        SkipUnless = nameof(IsEnabled),
        Skip = "Set HTTP_CACHE_GCS_RUN_INTEGRATION=1 and configure an existing test bucket and prefix to opt in.")]
    [InlineData(0)]
    [InlineData(1024 * 1024 + 13)]
    public async Task ExistingBucketSupportsExactRoundTripAndIdempotentRemoval(int length)
    {
        var bucket = RequiredSetting("HTTP_CACHE_GCS_TEST_BUCKET");
        var rootPrefix = RequiredSetting("HTTP_CACHE_GCS_TEST_PREFIX");
        var prefix = $"{rootPrefix.TrimEnd('/')}/integration-{Guid.NewGuid():N}/";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var client = StorageClient.Create();
        var store = new GoogleCloudStorageContentStore(client, new()
        {
            BucketName = bucket,
            Prefix = prefix
        });
        const string key = "round-trip";
        var bytes = new byte[length];
        new Random(123).NextBytes(bytes);

        try
        {
            await using var beforeWrite = await store.OpenReadAsync(key, timeout.Token);
            beforeWrite.ShouldBeNull();
            using var source = new MemoryStream(bytes);
            await store.WriteAsync(key, source, length, null, timeout.Token);
            source.CanRead.ShouldBeTrue();

            await using (var read = await store.OpenReadAsync(key, timeout.Token))
            {
                read.ShouldNotBeNull();
                using var actual = new MemoryStream();
                await read.CopyToAsync(actual, timeout.Token);
                actual.ToArray().ShouldBe(bytes);
            }

            await store.RemoveAsync(key, timeout.Token);
            await using var afterRemove = await store.OpenReadAsync(key, timeout.Token);
            afterRemove.ShouldBeNull();
            await store.RemoveAsync(key, timeout.Token);
        }
        finally
        {
            // Clean up only this case's exact key, even if its main operation timed out.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await store.RemoveAsync(key, cleanupTimeout.Token);
        }
    }

    private static string RequiredSetting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Integration tests are enabled but {name} is not configured.");
    }
}
