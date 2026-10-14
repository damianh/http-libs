namespace DamianH.Http.HybridCacheHandler;

/// <summary>Controls the S3 namespace and bounded, sequential upload strategy.</summary>
public sealed class S3ContentStoreOptions
{
    /// <summary>Gets or sets the existing bucket name. The adapter never creates buckets.</summary>
    public string BucketName { get; set; } = "";

    /// <summary>Gets or sets the literal object-key prefix, including any desired trailing slash.</summary>
    public string KeyPrefix { get; set; } = "http-cache/";

    /// <summary>Gets or sets the multipart threshold in bytes (default 16 MiB).</summary>
    public long MultipartThreshold { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the minimum target part size (5 MiB to 5 GiB; default 8 MiB).
    /// Larger objects automatically increase this size to stay within 10,000 parts.
    /// Parts are streamed, not buffered in memory.
    /// </summary>
    public long PartSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>Gets or sets the maximum bytes read from the input per read (default 64 KiB).</summary>
    public int TransferBufferSize { get; set; } = 64 * 1024;

    /// <summary>Gets or sets the independent timeout for aborting a failed multipart upload.</summary>
    public TimeSpan AbortTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
