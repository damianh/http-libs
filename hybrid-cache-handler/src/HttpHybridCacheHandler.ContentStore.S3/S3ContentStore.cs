using System.Net.Http;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>Stores opaque HTTP cache bodies in an existing Amazon S3 bucket.</summary>
public sealed class S3ContentStore : ILargeHttpCacheContentStore
{
    private const long MinimumPartSize = 5L * 1024 * 1024;
    private const long MaximumPartSize = 5L * 1024 * 1024 * 1024;
    private const long MaximumObjectSize = MaximumPartSize * 10_000;
    private const long SinglePutLimit = 5_000_000_000;
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly long _threshold;
    private readonly long _partSize;
    private readonly int _bufferSize;
    private readonly TimeSpan _abortTimeout;

    /// <summary>Creates a store. The injected client remains owned by the application.</summary>
    public S3ContentStore(IAmazonS3 client, IOptions<S3ContentStoreOptions> options)
    {
        Guard.NotNull(client);
        Guard.NotNull(options);
        var value = options.Value;
        Guard.NotNullOrWhiteSpace(value.BucketName);
        Guard.NotNull(value.KeyPrefix);
        if (Encoding.UTF8.GetByteCount(value.KeyPrefix) > 1024 - 64)
            throw new ArgumentException("The prefix plus the SHA-256 key must fit S3's 1024-byte key limit.", nameof(options));
        if (value.MultipartThreshold is < 1 or > SinglePutLimit)
            throw new ArgumentOutOfRangeException(nameof(options), "MultipartThreshold must be between 1 and 5,000,000,000 bytes.");
        if (value.PartSize is < MinimumPartSize or > MaximumPartSize)
            throw new ArgumentOutOfRangeException(nameof(options), "PartSize must be between 5 MiB and 5 GiB.");
        if (value.TransferBufferSize is < 1 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "TransferBufferSize must be between 1 byte and 1 MiB.");
        if (value.AbortTimeout <= TimeSpan.Zero || value.AbortTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options), "AbortTimeout must be positive and no more than five minutes.");
        _client = client;
        _bucket = value.BucketName;
        _prefix = value.KeyPrefix;
        _threshold = value.MultipartThreshold;
        _partSize = value.PartSize;
        _bufferSize = value.TransferBufferSize;
        _abortTimeout = value.AbortTimeout;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        string contentKey, Stream content, long contentLength, IEnumerable<string>? tags, CancellationToken ct)
    {
        var key = GetKey(contentKey);
        Guard.NotNull(content);
        Guard.NotNegative(contentLength);
        if (contentLength > MaximumObjectSize)
            throw new ArgumentOutOfRangeException(nameof(contentLength), "S3 supports at most 10,000 parts of 5 GiB each.");
        if (!content.CanRead || !content.CanSeek)
            throw new ArgumentException("The input must be readable and seekable.", nameof(content));
        if (content.Length - content.Position != contentLength)
            throw new ArgumentException("The remaining input length must equal contentLength.", nameof(contentLength));
        ct.ThrowIfCancellationRequested();

        try
        {
            if (contentLength < _threshold)
            {
                using var input = new UploadSliceStream(content, content.Position, contentLength, _bufferSize, ct);
                var request = new PutObjectRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    InputStream = input,
                    AutoCloseStream = false,
                    ContentType = "application/octet-stream"
                };
                request.Headers.ContentLength = contentLength;
                await _client.PutObjectAsync(request, ct).ConfigureAwait(false);
            }
            else
            {
                await WriteMultipartAsync(key, content, contentLength, ct).ConfigureAwait(false);
            }
        }
        catch (AmazonS3Exception exception)
        {
            throw new HttpCacheContentStoreException("The S3 cache body upload failed.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpCacheContentStoreException("The S3 cache body upload transport failed.", exception);
        }
    }

    private async Task WriteMultipartAsync(string key, Stream content, long length, CancellationToken ct)
    {
        var initiated = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentType = "application/octet-stream",
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256
        }, ct).ConfigureAwait(false);
        var uploadId = initiated.UploadId;
        try
        {
            var partSize = Math.Max(_partSize, (length + 9_999) / 10_000);
            var start = content.Position;
            var parts = new List<PartETag>();
            for (long offset = 0; offset < length; offset += partSize)
            {
                ct.ThrowIfCancellationRequested();
                var size = Math.Min(partSize, length - offset);
                using var input = new UploadSliceStream(content, start + offset, size, _bufferSize, ct);
                var number = parts.Count + 1;
                var part = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = number,
                    PartSize = size,
                    InputStream = input,
                    ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
                    IsLastPart = offset + size == length
                }, ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(part.ChecksumSHA256))
                    throw new IOException("S3 did not return the requested multipart SHA-256 checksum.");
                parts.Add(new PartETag(number, part.ETag) { ChecksumSHA256 = part.ChecksumSHA256 });
            }
            ct.ThrowIfCancellationRequested();
            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = parts
            }, ct).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The caller's cancellation must not prevent cleanup. Preserve the primary failure.
            try
            {
                using var cleanup = new CancellationTokenSource(_abortTimeout);
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = uploadId
                }, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                failure.Data["S3MultipartAbortFailure"] = cleanupFailure;
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct)
    {
        var key = GetKey(contentKey);
        ct.ThrowIfCancellationRequested();
        GetObjectResponse response;
        try
        {
            response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchKey")
        {
            return null;
        }
        try
        {
            if (response.ResponseStream is null)
                throw new IOException("S3 returned a response without a body stream.");
            return new ResponseOwnedStream(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string contentKey, CancellationToken ct)
    {
        var key = GetKey(contentKey);
        ct.ThrowIfCancellationRequested();
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = key
            }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (exception.ErrorCode == "NoSuchKey")
        {
        }
    }

    private string GetKey(string contentKey)
    {
        Guard.NotNull(contentKey);
        return _prefix + Hashing.ToHex(Hashing.Sha256(Encoding.UTF8.GetBytes(contentKey)), lowercase: true);
    }
}
