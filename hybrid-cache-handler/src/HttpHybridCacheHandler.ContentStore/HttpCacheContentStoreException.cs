namespace DamianH.Http.HybridCacheHandler;

/// <summary>
/// Indicates an operational content-store failure without requiring callers to reference a provider SDK.
/// </summary>
/// <remarks>
/// Adapters use this exception for recognized provider write failures, preserving the
/// original exception. Argument errors, cancellation, and programming errors are not
/// converted. A caching caller can abandon a failed write while still delivering the
/// origin response. Direct store callers still observe the failure.
/// </remarks>
public sealed class HttpCacheContentStoreException : IOException
{
    /// <summary>
    /// Initializes a storage failure with its original provider exception.
    /// </summary>
    /// <param name="message">A description of the failed storage operation.</param>
    /// <param name="innerException">The original provider failure.</param>
    public HttpCacheContentStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
