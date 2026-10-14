using Azure.Storage.Blobs;
using DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob;

namespace HttpHybridCacheHandler.ContentStore.AzureBlob.Tests;

public sealed class AzureBlobContentStoreIntegrationTests
{
    private const string ConnectionStringVariable = "HTTP_CACHE_AZURE_TEST_CONNECTION_STRING";
    private const string ContainerVariable = "HTTP_CACHE_AZURE_TEST_CONTAINER";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)) &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ContainerVariable));

    [Fact(
        SkipUnless = nameof(IsConfigured),
        Skip = "Set HTTP_CACHE_AZURE_TEST_CONNECTION_STRING and HTTP_CACHE_AZURE_TEST_CONTAINER to opt in.",
        Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task ExistingContainerSupportsCompleteBodyAndEmptyBodyLifecycle()
    {
        var container = new BlobContainerClient(
            Environment.GetEnvironmentVariable(ConnectionStringVariable)!,
            Environment.GetEnvironmentVariable(ContainerVariable)!);
        var store = new AzureBlobContentStore(container, new()
        {
            Namespace = $"http-cache-integration/{Guid.NewGuid():N}"
        });
        var keys = new[] { "empty-body", "multiple-block-body" };
        var ct = TestContext.Current.CancellationToken;
        try
        {
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                var expected = new byte[index == 0 ? 0 : 5 * 1024 * 1024 + 17];
                new Random(17).NextBytes(expected);
                Assert.Null(await store.OpenReadAsync(key, ct));
                using var input = new MemoryStream(expected, writable: false);
                await store.WriteAsync(key, input, expected.Length, null, ct);
                Assert.True(input.CanRead);

                await using (var actual = await store.OpenReadAsync(key, ct))
                {
                    Assert.NotNull(actual);
                    using var output = new MemoryStream();
                    await actual.CopyToAsync(output, ct);
                    Assert.Equal(expected, output.ToArray());
                }

                await store.RemoveAsync(key, ct);
                Assert.Null(await store.OpenReadAsync(key, ct));
                await store.RemoveAsync(key, ct);
            }
        }
        finally
        {
            // Cleanup has its own deadline so cancellation still permits deleting this run's objects.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.WhenAll(keys.Select(key => store.RemoveAsync(key, cleanup.Token).AsTask()));
        }
    }
}
