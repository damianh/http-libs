using System.Net;
using System.Net.Http;
using System.Text;
using Google;
using Google.Cloud.Storage.V1;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>Stores opaque response bytes in an existing Google Cloud Storage bucket.</summary>
/// <remarks>The injected client remains application-owned. Tags are advisory and are not persisted.</remarks>
public sealed class GoogleCloudStorageContentStore : ILargeHttpCacheContentStore
{
    private readonly StorageClient _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly int _downloadBufferSize;

    /// <summary>Creates a store using an application-owned client and a snapshot of the options.</summary>
    public GoogleCloudStorageContentStore(StorageClient client, GoogleCloudStorageContentStoreOptions options)
    {
        Guard.NotNull(client);
        Guard.NotNull(options);
        Guard.NotNullOrWhiteSpace(options.BucketName);
        Guard.NotNull(options.Prefix);
        Guard.NotLessThan(options.DownloadBufferSize, 1024);
        _client = client;
        _bucket = options.BucketName;
        _prefix = options.Prefix.Length == 0 || options.Prefix.EndsWith("/", StringComparison.Ordinal) ? options.Prefix : options.Prefix + "/";
        _downloadBufferSize = options.DownloadBufferSize;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(string contentKey, Stream content, long contentLength,
        IEnumerable<string>? tags, CancellationToken ct)
    {
        var name = ObjectName(contentKey);
        Guard.NotNull(content);
        Guard.NotNegative(contentLength);
        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException("The input stream must be readable and seekable.", nameof(content));
        }
        if (content.Length - content.Position != contentLength)
        {
            throw new ArgumentException("The remaining stream length must equal contentLength.", nameof(contentLength));
        }
        ct.ThrowIfCancellationRequested();
        using var source = new UploadSourceStream(content, contentLength);
        try
        {
            await _client.UploadObjectAsync(new StorageObject
            {
                Bucket = _bucket,
                Name = name,
                ContentType = "application/octet-stream"
            }, source, new UploadObjectOptions
            {
                ChunkSize = UploadObjectOptions.MinimumChunkSize,
                UploadValidationMode = UploadValidationMode.DeleteAndThrow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is GoogleApiException or HttpRequestException or UploadValidationException)
        {
            throw new HttpCacheContentStoreException("Google Cloud Storage content upload failed.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct)
    {
        var name = ObjectName(contentKey);
        ct.ThrowIfCancellationRequested();
        StorageObject metadata;
        try
        {
            metadata = await _client.GetObjectAsync(_bucket, name, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // GCS uses the same notFound reason for missing objects and missing buckets.
            await _client.GetBucketAsync(_bucket, cancellationToken: ct).ConfigureAwait(false);
            return null;
        }

        var generation = metadata.Generation
            ?? throw new InvalidDataException("Google Cloud Storage returned no object generation.");
        ct.ThrowIfCancellationRequested();
        return new DownloadReadStream(
            (destination, token) => _client.DownloadObjectAsync(_bucket, name, destination,
                new DownloadObjectOptions
                {
                    Generation = generation,
                    ChunkSize = 256 * 1024,
                    DownloadValidationMode = DownloadValidationMode.Always
                }, token),
            _downloadBufferSize, ct);
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string contentKey, CancellationToken ct)
    {
        var name = ObjectName(contentKey);
        ct.ThrowIfCancellationRequested();
        try
        {
            await _client.DeleteObjectAsync(_bucket, name, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            await _client.GetBucketAsync(_bucket, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    private string ObjectName(string contentKey)
    {
        Guard.NotNull(contentKey);
        return _prefix + Hashing.ToHex(Hashing.Sha256(Encoding.UTF8.GetBytes(contentKey)), lowercase: true);
    }
}
