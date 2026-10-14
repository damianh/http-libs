namespace DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob;

/// <summary>Configures the isolated Azure Blob cache namespace.</summary>
public sealed class AzureBlobContentStoreOptions
{
    /// <summary>
    /// Gets or sets the blob-name prefix. Use a distinct, stable namespace per cache.
    /// Defaults to <c>http-cache</c>; trailing slashes are removed.
    /// </summary>
    public string Namespace { get; set; } = "http-cache";
}
