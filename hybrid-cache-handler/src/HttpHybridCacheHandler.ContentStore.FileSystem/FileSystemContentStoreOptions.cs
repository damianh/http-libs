namespace DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;

/// <summary>Configures storage of opaque HTTP cache bodies on a private local filesystem.</summary>
public sealed class FileSystemContentStoreOptions
{
    /// <summary>Gets or sets the required absolute, dedicated directory owned by one process.</summary>
    public string RootDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional maximum age since publication, independent of HTTP freshness.</summary>
    public TimeSpan? MaximumAge { get; set; }

    /// <summary>Gets or sets an optional soft total-byte limit. Cleanup evicts oldest bodies first.</summary>
    public long? MaximumTotalBytes { get; set; }

    /// <summary>Gets or sets the cleanup interval, defaulting to five minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory) || !IsPathFullyQualified(RootDirectory))
        {
            throw new ArgumentException("An absolute, dedicated RootDirectory is required.", nameof(RootDirectory));
        }

        if (MaximumAge is { } age && age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAge));
        }

        if (MaximumTotalBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTotalBytes));
        }

        if (CleanupInterval <= TimeSpan.Zero || CleanupInterval.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CleanupInterval));
        }
    }

    private static bool IsPathFullyQualified(string path)
    {
#if NETSTANDARD2_0 || NETFRAMEWORK
        if (Path.DirectorySeparatorChar == '/')
        {
            return path[0] == '/';
        }

        static bool IsSeparator(char value) => value is '\\' or '/';
        return path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1])
            || path.Length >= 3 && path[0] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
                && path[1] == ':' && IsSeparator(path[2]);
#else
        return Path.IsPathFullyQualified(path);
#endif
    }
}
