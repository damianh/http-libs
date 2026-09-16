using System.Buffers;
using System.Buffers.Binary;
using System.Net.Http;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob;

/// <summary>Stores complete opaque response bodies in an existing Azure Blob container.</summary>
/// <remarks>
/// Clients and input streams remain caller-owned. Tags are advisory and are not stored
/// as Azure tags. Uploads stage bounded blocks and publish only by committing the full list.
/// </remarks>
public sealed class AzureBlobContentStore : ILargeHttpCacheContentStore
{
    private const int BlockSize = 4 * 1024 * 1024;
    private const int MaximumBlocks = 50_000;
    private readonly BlobContainerClient _container;
    private readonly string _prefix;

    /// <summary>Creates a store without provisioning or taking ownership of the client.</summary>
    public AzureBlobContentStore(BlobContainerClient container, AzureBlobContentStoreOptions? options = null)
    {
        Guard.NotNull(container);
        _container = container;
        var prefix = (options ?? new AzureBlobContentStoreOptions()).Namespace;
        Guard.NotNullOrWhiteSpace(prefix);
        _prefix = prefix.TrimEnd('/');
        if (_prefix.Length is 0 or > 900 || _prefix.Any(char.IsControl))
        {
            throw new ArgumentException("The namespace must contain 1-900 non-control characters.", nameof(options));
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        string contentKey, Stream content, long contentLength, IEnumerable<string>? tags, CancellationToken ct)
    {
        var blob = _container.GetBlockBlobClient(GetBlobName(contentKey));
        Guard.NotNull(content);
        Guard.NotNegative(contentLength);
        if (contentLength > (long)BlockSize * MaximumBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength), "The maximum body size is 195.3125 GiB.");
        }
        if (!content.CanRead || !content.CanSeek)
        {
            throw new ArgumentException("Content must be readable and seekable.", nameof(content));
        }
        if (content.Length - content.Position != contentLength)
        {
            throw new ArgumentException("Content length must match the stream's remaining bytes.", nameof(contentLength));
        }
        ct.ThrowIfCancellationRequested();

        var blocks = new List<string>((int)((contentLength + BlockSize - 1) / BlockSize));
        var uploadId = Guid.NewGuid().ToByteArray();
        var buffer = contentLength == 0 ? null : ArrayPool<byte>.Shared.Rent((int)Math.Min(BlockSize, contentLength));
        try
        {
            var remaining = contentLength;
            while (remaining > 0)
            {
                var count = (int)Math.Min(BlockSize, remaining);
                await content.ReadExactlyAsync(buffer!.AsMemory(0, count), ct).ConfigureAwait(false);
                // Fixed-length, per-upload block IDs isolate concurrent writers to the same blob.
                var id = new byte[20];
                uploadId.CopyTo(id, 0);
                BinaryPrimitives.WriteInt32BigEndian(id.AsSpan(16), blocks.Count);
                var blockId = Convert.ToBase64String(id);
                using var block = new MemoryStream(buffer!, 0, count, writable: false);
                await blob.StageBlockAsync(blockId, block, cancellationToken: ct).ConfigureAwait(false);
                blocks.Add(blockId);
                remaining -= count;
            }

            // Detect inputs that changed length while uploading before making anything visible.
            var probe = new byte[1];
            if (await content.ReadAsync(probe, ct).ConfigureAwait(false) != 0)
            {
                throw new IOException("Content exceeded the declared length.");
            }
            ct.ThrowIfCancellationRequested();
            await blob.CommitBlockListAsync(blocks, new CommitBlockListOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/octet-stream" }
            }, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException exception)
        {
            throw new HttpCacheContentStoreException("Azure Blob content upload failed.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpCacheContentStoreException("Azure Blob content upload transport failed.", exception);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(GetBlobName(contentKey));
        ct.ThrowIfCancellationRequested();
        try
        {
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
            return response.Value.Content;
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string contentKey, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(GetBlobName(contentKey));
        ct.ThrowIfCancellationRequested();
        try
        {
            await blob.DeleteAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
        }
    }

    private string GetBlobName(string contentKey)
    {
        Guard.NotNull(contentKey);
        var hash = Hashing.Sha256(Encoding.UTF8.GetBytes(contentKey));
        return $"{_prefix}/{Hashing.ToHex(hash, lowercase: true)}";
    }

    private static bool IsMissingBlob(RequestFailedException exception) =>
        exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString();
}
