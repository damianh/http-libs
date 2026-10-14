// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.HybridCacheHandler;

/// <summary>
/// Stores and retrieves cached HTTP response content bodies.
/// </summary>
public interface IHttpCacheContentStore
{
    /// <summary>
    /// Writes a complete representation for an opaque cache key.
    /// </summary>
    /// <remarks>
    /// The caller owns the readable, seekable input stream, positioned at the start
    /// of exactly <paramref name="contentLength"/> bytes. Implementations must finish
    /// reading before returning, leave the stream open, and never expose partial writes.
    /// Tags are advisory for cache implementations, not provider-side HTTP invalidation.
    /// Concurrent writes of identical content to the same key must be safe.
    /// Recognized SDK write failures use <see cref="HttpCacheContentStoreException"/>
    /// with their original cause so callers can handle cache failures without SDK dependencies.
    /// </remarks>
    ValueTask WriteAsync(
        string contentKey,
        Stream content,
        long contentLength,
        IEnumerable<string>? tags,
        CancellationToken ct);

    /// <summary>
    /// Opens cached content for reading.
    /// Returns null when the content is missing.
    /// </summary>
    /// <remarks>
    /// The caller owns the returned stream; disposing it releases associated resources.
    /// Each call returns an independent stream. Seeking need not be supported.
    /// Only missing content maps to null; configuration, access, and transport failures
    /// must propagate. Errors occurring after opening propagate through the stream.
    /// </remarks>
    ValueTask<Stream?> OpenReadAsync(string contentKey, CancellationToken ct);

    /// <summary>
    /// Removes one cached body by key; missing content is a no-op.
    /// </summary>
    /// <remarks>
    /// This does not remove referencing metadata. Callers must account for shared
    /// content references before removing a body. Never performs recursive deletion.
    /// </remarks>
    ValueTask RemoveAsync(string contentKey, CancellationToken ct);
}
