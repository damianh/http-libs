// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.HttpHybridCacheHandler;

/// <summary>
/// Configuration options for <see cref="HttpHybridCacheHandler"/>.
/// </summary>
public class HttpHybridCacheHandlerOptions
{
    /// <summary>Memory threshold per staging stream (default 64 KiB). Compression may use a second staging stream.</summary>
    public int SpoolMemoryThreshold { get; set; } = 64 * 1024;

    /// <summary>Per-process aggregate active disk spool budget (default 1 GiB). Exhaustion bypasses caching.</summary>
    public long MaxSpoolDiskBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Per-process maximum number of disk spools, including compression staging (default 32).</summary>
    public int MaxConcurrentDiskSpools { get; set; } = 32;

    /// <summary>Parent directory for process-owned spool directories. When null, uses <see cref="Path.GetTempPath()"/>.</summary>
    /// <remarks>
    /// The application must select a trusted private parent, including verifying the default temporary directory
    /// for the account running the process. On Windows, provision the parent with inheritable ACLs that restrict
    /// access to trusted principals before spooling; the handler inherits these ACLs without restricting or
    /// validating them. Unique directory names and leases manage spool lifetime, not access control.
    /// </remarks>
    public string? SpoolDirectory { get; set; }

    internal void ValidateSpooling()
    {
        Guard.NotNegative(SpoolMemoryThreshold);
        Guard.NotNegative(MaxSpoolDiskBytes);
        Guard.NotNegative(MaxConcurrentDiskSpools);
        if (SpoolDirectory is { Length: 0 })
        {
            throw new ArgumentException("The spool directory must not be empty.", nameof(SpoolDirectory));
        }
    }

    /// <summary>
    /// Default minimum content size in bytes to enable compression. Set to 1 KB.
    /// </summary>
    public const long DefaultCompressionThreshold = 1024;

    /// <summary>
    /// Default heuristic freshness percentage for responses with Last-Modified but no explicit freshness info.
    /// Set to 0.1 (10% of Last-Modified age as per RFC 7234 recommendation).
    /// </summary>
    public const double DefaultHeuristicFreshnessPercent = 0.1;

    /// <summary>
    /// Default minimum heuristic freshness lifetime for responses with Last-Modified but no explicit freshness info.
    /// Set to 30 seconds to avoid immediately stale responses due to very recent Last-Modified timestamps.
    /// </summary>
    public static readonly TimeSpan DefaultHeuristicFreshnessMinimum = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default maximum size in bytes for cacheable response content. Set to 10 MB.
    /// </summary>
    public const long DefaultMaxCacheableContentSize = 10 * 1024 * 1024;

    /// <summary>
    /// Default response size threshold for routing content to an external large-content store.
    /// Set to 1 MiB.
    /// </summary>
    public const long DefaultLargeContentThreshold = 1024 * 1024;

    /// <summary>
    /// Default list of cacheable content types. Values are
    /// text/*, application/json, application/json+*, application/xml,
    /// application/javascript, application/xhtml+xml, image/*.
    /// </summary>
    public static readonly string[] DefaultCacheableContentTypes =
    [
        "text/*",
        "application/json",
        "application/json+*",
        "application/xml",
        "application/javascript",
        "application/xhtml+xml",
        "image/*"
    ];

    /// <summary>
    /// Default list of compressible content types. Values are
    /// text/*, application/json, application/json+*, application/xml,
    /// application/javascript, application/xhtml+xml, application/rss+xml,
    /// application/atom+xml, image/svg+xml.
    /// </summary>
    public static readonly string[] DefaultCompressibleContentTypes =
    [
        "text/*",
        "application/json",
        "application/json+*",
        "application/xml",
        "application/javascript",
        "image/svg+xml"
    ];

    /// <summary>
    /// Default targeted cache-control headers used by shared caches (RFC 9213).
    /// </summary>
    public static readonly string[] DefaultTargetedCacheControlHeaderNames =
    [
        "CDN-Cache-Control"
    ];

    /// <summary>
    /// Default headers to include in Vary-aware cache keys. Values are
    /// none (response Vary matching is always enforced independently).
    /// </summary>
    public static string[] DefaultVaryHeaders { get; } =
    [];

    /// <summary>
    /// Heuristic freshness percentage for responses with Last-Modified but no explicit freshness info.
    /// Default is set to <see cref="DefaultHeuristicFreshnessPercent"/>.
    /// </summary>
    public double HeuristicFreshnessPercent { get; set; } = DefaultHeuristicFreshnessPercent;

    /// <summary>
    /// Minimum heuristic freshness lifetime for responses with Last-Modified but no explicit freshness info.
    /// Default is set to <see cref="DefaultHeuristicFreshnessMinimum"/>.
    /// </summary>
    public TimeSpan HeuristicFreshnessMinimum { get; set; } = DefaultHeuristicFreshnessMinimum;

    /// <summary>
    /// Headers to include in request-key partitioning as a performance heuristic.
    /// Correctness is enforced by matching the stored response Vary fields on cache hits.
    /// Default is set to <see cref="DefaultVaryHeaders"/>.
    /// </summary>
    public string[] VaryHeaders { get; set; } = DefaultVaryHeaders;

    /// <summary>
    /// Maximum size in bytes for cacheable response content.
    /// Responses larger than this will not be cached. Default is set
    /// to <see cref="DefaultMaxCacheableContentSize"/>.
    /// </summary>
    public long MaxCacheableContentSize { get; set; } = DefaultMaxCacheableContentSize;

    /// <summary>
    /// Default cache duration for responses without explicit caching headers.
    /// If value is TimeSpan.MinValue then responses without caching headers are
    /// not cached. Default value is TimeSpan.MinValue.
    /// </summary>
    public TimeSpan FallbackCacheDuration { get; set; } = TimeSpan.MinValue;

    /// <summary>
    /// Minimum content size in bytes to enable compression.
    /// Content smaller than this will not be compressed.
    /// Default is set to <see cref="DefaultCompressionThreshold"/>.
    /// Set to 0 or negative value to disable compression.
    /// </summary>
    public long CompressionThreshold { get; set; } = DefaultCompressionThreshold;

    /// <summary>
    /// Response size threshold in bytes for routing cache content to an optional
    /// <see cref="ILargeHttpCacheContentStore"/> implementation.
    /// Set to 0 or a negative value to always use HybridCache content storage.
    /// </summary>
    public long LargeContentThreshold { get; set; } = DefaultLargeContentThreshold;

    /// <summary>
    /// Gets or sets the list of MIME types that are eligible for compression.
    /// Default is set to <see cref="DefaultCompressibleContentTypes"/>.
    /// </summary>
    public string[] CompressibleContentTypes { get; set; } = DefaultCompressibleContentTypes;

    /// <summary>
    /// Gets or sets the list of MIME content types that are eligible for caching.
    /// Default value is <see cref="DefaultCacheableContentTypes"/>
    /// </summary>
    public string[] CacheableContentTypes { get; set; } = DefaultCacheableContentTypes;

    /// <summary>
    /// Whether to include diagnostic headers in responses.
    /// When enabled, adds X-Cache-Diagnostic header with cache behavior information.
    /// Default is false.
    /// </summary>
    public bool IncludeDiagnosticHeaders { get; set; }

    /// <summary>
    /// Prefix for content cache keys.
    /// Default is "httpcache:content:".
    /// Content is always stored separately from metadata to avoid Base64 encoding overhead.
    /// </summary>
    public string ContentKeyPrefix { get; init; } = "httpcache:content:";

    /// <summary>
    /// Cache mode determining caching behavior.
    /// Default is Private (browser-like cache, suitable for scaled-out clients).
    /// Use Shared for proxy/CDN scenarios (e.g., YARP).
    /// </summary>
    public CacheMode Mode { get; set; } = CacheMode.Private;

    private string[] _targetedCacheControlHeaderNames = [.. DefaultTargetedCacheControlHeaderNames];

    /// <summary>
    /// Response header names containing targeted cache directives (RFC 9213).
    /// These directives are applied only when <see cref="Mode"/> is <see cref="CacheMode.Shared"/>.
    /// Default is <see cref="DefaultTargetedCacheControlHeaderNames"/>.
    /// </summary>
    public string[] TargetedCacheControlHeaderNames
    {
        get => _targetedCacheControlHeaderNames;
        set => _targetedCacheControlHeaderNames = value is null ? [] : [.. value];
    }
}
