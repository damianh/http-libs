namespace DamianH.Http.HybridCacheHandler;

/// <summary>Configures an existing Google Cloud Storage cache namespace.</summary>
public sealed class GoogleCloudStorageContentStoreOptions
{
    /// <summary>Gets or sets the existing bucket name. The adapter never creates buckets.</summary>
    public string BucketName { get; set; } = "";

    /// <summary>Gets or sets the object prefix. A slash is appended when needed.</summary>
    public string Prefix { get; set; } = "http-cache/";

    /// <summary>Gets or sets the bounded download pipe's pause threshold, in bytes (default 64 KiB).</summary>
    public int DownloadBufferSize { get; set; } = 64 * 1024;
}
