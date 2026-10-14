// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http.Headers;

namespace DamianH.Http.HybridCacheHandler;

internal sealed class CacheKeyGenerator(HttpHybridCacheHandlerOptions options)
{
    public string NormalizeRangeHeader(RangeHeaderValue rangeHeader)
    {
        if (string.Equals(rangeHeader.Unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
            rangeHeader.Ranges.Count == 1)
        {
            var item = rangeHeader.Ranges.First();
            return $"bytes={item.From?.ToString() ?? string.Empty}-{item.To?.ToString() ?? string.Empty}";
        }

        return rangeHeader.ToString();
    }

    public string GenerateVaryAwareCacheKey(
        HttpRequestMessage request,
        HttpMethod? cacheMethod = null,
        bool includeRange = false)
    {
        var baseCacheKey = $"{cacheMethod ?? request.Method}:{request.RequestUri}";
        var varyParts = new List<string>();
        foreach (var header in options.VaryHeaders)
        {
            if (request.Headers.TryGetValues(header, out var values))
            {
                var normalized = NormalizeHeaderValues(values);
                varyParts.Add($"{header}:{normalized}");
            }
            else
            {
                varyParts.Add($"{header}:");
            }
        }

        if (includeRange && request.Headers.Range != null)
        {
            varyParts.Add($"Range:{NormalizeRangeHeader(request.Headers.Range)}");
        }

        var varyKeyPart = string.Join("|", varyParts);
        return $"{baseCacheKey}::{varyKeyPart}";
    }

    public bool MatchesStoredVaryHeaders(CachedHttpMetadata cachedResponse, HttpRequestMessage request)
    {
        if (cachedResponse.VaryHeaders is not { Length: > 0 })
        {
            return true;
        }

        if (cachedResponse.VaryHeaderValues is null)
        {
            return false;
        }

        foreach (var varyHeader in cachedResponse.VaryHeaders)
        {
            var requestValue = GetNormalizedHeaderValue(request, varyHeader);
            cachedResponse.VaryHeaderValues.TryGetValue(varyHeader, out var cachedValue);
            if (!string.Equals(cachedValue ?? string.Empty, requestValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public static string GetNormalizedHeaderValue(HttpRequestMessage request, string headerName)
        => request.Headers.TryGetValues(headerName, out var values)
            ? NormalizeHeaderValues(values)
            : string.Empty;

    public static string NormalizeHeaderValues(IEnumerable<string> values)
        => VaryMatcher.NormalizeHeaderValue(values);
}
