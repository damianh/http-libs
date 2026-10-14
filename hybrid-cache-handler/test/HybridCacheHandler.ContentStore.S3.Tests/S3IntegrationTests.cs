using Amazon;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace DamianH.Http.HybridCacheHandler;

public class S3IntegrationTests
{
    public static bool IsEnabled => Environment.GetEnvironmentVariable("HTTP_CACHE_S3_INTEGRATION") == "1";

    [Theory(Skip = "Set HTTP_CACHE_S3_INTEGRATION=1 and configure existing isolated S3 test resources.", SkipUnless = nameof(IsEnabled))]
    [InlineData(0)]
    [InlineData(65_537)]
    [InlineData(5 * 1024 * 1024 + 113)]
    [Trait("Category", "Integration")]
    public async Task Existing_bucket_round_trip_exact_bytes_and_remove(int length)
    {
        var bucket = RequiredEnvironmentVariable("HTTP_CACHE_S3_TEST_BUCKET");
        var prefix = RequiredEnvironmentVariable("HTTP_CACHE_S3_TEST_PREFIX");
        var region = RequiredEnvironmentVariable("HTTP_CACHE_S3_TEST_REGION");
        var configuration = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            MaxErrorRetry = 2
        };
        var endpoint = Environment.GetEnvironmentVariable("HTTP_CACHE_S3_TEST_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo),
                "HTTP_CACHE_S3_TEST_ENDPOINT must be an HTTP(S) endpoint without embedded credentials.");
            configuration.ServiceURL = endpoint;
            configuration.AuthenticationRegion = region;
        }
        var pathStyle = Environment.GetEnvironmentVariable("HTTP_CACHE_S3_TEST_FORCE_PATH_STYLE");
        Assert.True(pathStyle is null or "" or "0" or "1", "HTTP_CACHE_S3_TEST_FORCE_PATH_STYLE must be 0 or 1.");
        configuration.ForcePathStyle = pathStyle == "1";

        using var client = new AmazonS3Client(configuration);
        var store = new S3ContentStore(client, Options.Create(new S3ContentStoreOptions
        {
            BucketName = bucket,
            KeyPrefix = $"{prefix.TrimEnd('/')}/{Guid.NewGuid():N}/",
            MultipartThreshold = 1024 * 1024,
            PartSize = 5 * 1024 * 1024
        }));
        const string key = "integration-body";
        Exception? testFailure = null;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.Null(await store.OpenReadAsync(key, ct));
            var expected = new byte[length];
            new Random(2026).NextBytes(expected);
            using var input = new MemoryStream(expected);
            await store.WriteAsync(key, input, expected.Length, null, ct);
            Assert.True(input.CanRead);
            await using (var read = await store.OpenReadAsync(key, ct))
            {
                Assert.NotNull(read);
                using var output = new MemoryStream();
                await read.CopyToAsync(output, ct);
                Assert.Equal(expected, output.ToArray());
            }
            await store.RemoveAsync(key, ct);
            await store.RemoveAsync(key, ct);
            Assert.Null(await store.OpenReadAsync(key, ct));
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            // Clean exactly this invocation's key, even if the test token was cancelled.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await store.RemoveAsync(key, cleanup.Token);
            }
            catch (Exception cleanupFailure) when (testFailure is not null)
            {
                throw new AggregateException("S3 integration validation and its single-key cleanup both failed.", testFailure, cleanupFailure);
            }
        }
    }

    private static string RequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Explicit integration opt-in requires {name}.");
        return value!;
    }
}
