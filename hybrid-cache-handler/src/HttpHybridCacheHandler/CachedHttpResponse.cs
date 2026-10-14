// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

internal sealed class CachedHttpEntry
{
    private List<CachedHttpMetadata>? _variants;

    public List<CachedHttpMetadata> Variants
    {
        get => _variants ??= [];
        init => _variants = value;
    }
}

/// <summary>
/// Cached HTTP response metadata without the content body.
/// Content is stored separately to avoid Base64 encoding overhead in distributed cache.
/// </summary>
internal sealed record CachedHttpMetadata
{
    public required int StatusCode { get; init; }
    public required string ContentKey { get; init; }
    public required long ContentLength { get; init; }
    public long? OriginalContentLength { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public required Dictionary<string, string[]> ContentHeaders { get; init; }
    public required DateTimeOffset CachedAt { get; init; }
    public TimeSpan? MaxAge { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public DateTimeOffset? Date { get; init; }
    public TimeSpan? Age { get; init; }
    public string[]? VaryHeaders { get; init; }
    public Dictionary<string, string>? VaryHeaderValues { get; init; }
    public TimeSpan? StaleWhileRevalidate { get; init; }
    public TimeSpan? StaleIfError { get; init; }
    public bool MustRevalidate { get; init; }
    public bool ProxyRevalidate { get; init; }
    public bool NoCache { get; init; }
    public bool HasSharedMaxAge { get; init; }
    public string[]? QualifiedNoCacheHeaderNames { get; init; }
    public bool IgnoreStoredAge { get; init; }
    public bool IsCompressed { get; init; }
    public bool IsPartial { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }
    public long? RangeTotalLength { get; init; }
    public bool IsStoredExternally { get; init; }
    public Guid PublicationSession { get; init; }
    public long PublicationSequence { get; init; }
}
