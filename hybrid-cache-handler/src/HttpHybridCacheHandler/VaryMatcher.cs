// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;

namespace DamianH.HttpHybridCacheHandler;

internal static class VaryMatcher
{
    public static CachedHttpMetadata? SelectVariant(
        CachedHttpEntry entry,
        HttpRequestMessage request,
        Func<CachedHttpMetadata, bool>? variantFilter = null)
    {
        CachedHttpMetadata? bestMatch = null;
        VariantMatchScore bestMatchScore = default;

        foreach (var variant in entry.Variants)
        {
            if (!TryGetVariantMatchScore(variant, request, out var candidateScore))
            {
                continue;
            }

            if (variantFilter != null && !variantFilter(variant))
            {
                continue;
            }

            if (bestMatch == null || CompareMatchScore(candidateScore, bestMatchScore) > 0)
            {
                bestMatch = variant;
                bestMatchScore = candidateScore;
            }
        }

        return bestMatch;
    }

    public static string BuildVariantSignature(CachedHttpMetadata variant) =>
        BuildVariantSignature(variant.VaryHeaders, variant.VaryHeaderValues);

    public static string BuildVariantSignature(string[]? varyHeaders, Dictionary<string, string>? varyHeaderValues)
    {
        if (varyHeaders == null || varyHeaders.Length == 0)
        {
            return "<no-vary>";
        }

        var normalizedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawVaryHeader in varyHeaders)
        {
            if (!TryNormalizeVaryHeaderName(rawVaryHeader, out var normalizedVaryHeader))
            {
                continue;
            }

            normalizedHeaders.Add(normalizedVaryHeader);
        }

        if (normalizedHeaders.Count == 0)
        {
            return "<no-vary>";
        }

        var parts = normalizedHeaders
            .OrderBy(static h => h, StringComparer.OrdinalIgnoreCase)
            .Select(h =>
            {
                var value = string.Empty;
                if (varyHeaderValues != null && TryGetStoredVaryHeaderValue(varyHeaderValues, h, out var storedValue))
                {
                    value = storedValue;
                }

                return $"{h.ToLowerInvariant()}={EscapeVariantSignatureValue(CanonicalizeForSignature(h, value))}";
            });

        return string.Join("|", parts);
    }

    public static string NormalizeHeaderValue(IEnumerable<string> values)
    {
        var normalizedTokens = values
            .SelectMany(static value => SplitHeaderValue(value))
            .Where(static token => token.Length > 0);

        return string.Join(",", normalizedTokens);
    }

    private static IEnumerable<string> SplitHeaderValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        var tokenStart = 0;
        var inQuotes = false;
        var isEscaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (inQuotes && c == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (c != ',' || inQuotes)
            {
                continue;
            }

            var token = value[tokenStart..i].Trim();
            if (token.Length > 0)
            {
                yield return token;
            }

            tokenStart = i + 1;
        }

        var trailingToken = value[tokenStart..].Trim();
        if (trailingToken.Length > 0)
        {
            yield return trailingToken;
        }
    }

    private static bool TryGetVariantMatchScore(
        CachedHttpMetadata variant,
        HttpRequestMessage request,
        out VariantMatchScore score)
    {
        score = default;
        if (variant.VaryHeaders == null || variant.VaryHeaders.Length == 0)
        {
            score = new VariantMatchScore(0, 0, 0d, variant.CachedAt);
            return true;
        }

        if (variant.VaryHeaderValues == null)
        {
            return false;
        }

        var exactHeaderCount = 0;
        var acceptLanguageMatchRank = 0;
        var acceptLanguageWeight = 0d;

        var seenVaryHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawVaryHeader in variant.VaryHeaders)
        {
            if (!TryNormalizeVaryHeaderName(rawVaryHeader, out var varyHeader))
            {
                continue;
            }

            if (!seenVaryHeaders.Add(varyHeader))
            {
                continue;
            }

            var storedValue = variant.VaryHeaderValues.TryGetValue(varyHeader, out var value)
                ? value
                : string.Empty;

            var requestValue = GetNormalizedRequestHeaderValue(request, varyHeader);
            if (varyHeader.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase))
            {
                var acceptLanguageMatch = EvaluateAcceptLanguageMatch(storedValue, requestValue, variant);
                if (!acceptLanguageMatch.IsMatch)
                {
                    return false;
                }

                if (acceptLanguageMatch.MatchRank > acceptLanguageMatchRank)
                {
                    acceptLanguageMatchRank = acceptLanguageMatch.MatchRank;
                    acceptLanguageWeight = acceptLanguageMatch.Weight;
                }
                else if (acceptLanguageMatch.MatchRank == acceptLanguageMatchRank
                    && acceptLanguageMatch.Weight > acceptLanguageWeight)
                {
                    acceptLanguageWeight = acceptLanguageMatch.Weight;
                }

                continue;
            }

            if (!string.Equals(storedValue, requestValue, StringComparison.Ordinal))
            {
                return false;
            }

            exactHeaderCount++;
        }

        score = new VariantMatchScore(exactHeaderCount, acceptLanguageMatchRank, acceptLanguageWeight, variant.CachedAt);
        return true;
    }

    private static bool TryNormalizeVaryHeaderName(string? headerName, out string normalizedHeaderName)
    {
        normalizedHeaderName = string.Empty;
        if (TextCompatibility.IsNullOrWhiteSpace(headerName))
        {
            return false;
        }

        var candidate = headerName.Trim();
        if (candidate.Length == 0 || candidate == "*")
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!TextCompatibility.IsAsciiLetterOrDigit(c) &&
                c != '!' &&
                c != '#' &&
                c != '$' &&
                c != '%' &&
                c != '&' &&
                c != '\'' &&
                c != '*' &&
                c != '+' &&
                c != '-' &&
                c != '.' &&
                c != '^' &&
                c != '_' &&
                c != '`' &&
                c != '|' &&
                c != '~')
            {
                return false;
            }
        }

        normalizedHeaderName = candidate;
        return true;
    }

    private static bool TryGetStoredVaryHeaderValue(
        Dictionary<string, string> varyHeaderValues,
        string normalizedHeaderName,
        out string storedValue)
    {
        if (varyHeaderValues.TryGetValue(normalizedHeaderName, out var directValue))
        {
            storedValue = directValue ?? string.Empty;
            return true;
        }

        foreach (var item in varyHeaderValues)
        {
            if (!TryNormalizeVaryHeaderName(item.Key, out var normalizedStoredHeaderName))
            {
                continue;
            }

            if (normalizedStoredHeaderName.Equals(normalizedHeaderName, StringComparison.OrdinalIgnoreCase))
            {
                storedValue = item.Value ?? string.Empty;
                return true;
            }
        }

        storedValue = string.Empty;
        return false;
    }

    private static int CompareMatchScore(VariantMatchScore left, VariantMatchScore right)
    {
        var comparison = left.ExactHeaderCount.CompareTo(right.ExactHeaderCount);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.AcceptLanguageMatchRank.CompareTo(right.AcceptLanguageMatchRank);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.AcceptLanguageWeight.CompareTo(right.AcceptLanguageWeight);
        if (comparison != 0)
        {
            return comparison;
        }

        return left.CachedAt.CompareTo(right.CachedAt);
    }

    private static string GetNormalizedRequestHeaderValue(HttpRequestMessage request, string headerName) =>
        request.Headers.TryGetValues(headerName, out var values)
            ? NormalizeHeaderValue(values)
            : string.Empty;

    private static AcceptLanguageMatch EvaluateAcceptLanguageMatch(
        string storedRequestValue,
        string presentedRequestValue,
        CachedHttpMetadata variant)
    {
        if (string.Equals(storedRequestValue, presentedRequestValue, StringComparison.Ordinal))
        {
            return new AcceptLanguageMatch(true, 2, 1d);
        }

        if (AreEquivalentAcceptLanguageValues(storedRequestValue, presentedRequestValue))
        {
            return new AcceptLanguageMatch(true, 1, 1d);
        }

        if (string.IsNullOrEmpty(presentedRequestValue))
        {
            return default;
        }

        if (!TryGetContentLanguages(variant, out var contentLanguages))
        {
            return default;
        }

        var presentedRanges = ParseLanguageRanges(presentedRequestValue);
        if (presentedRanges.Count == 0)
        {
            return default;
        }

        var bestWeight = 0d;
        foreach (var language in contentLanguages)
        {
            var matchWeight = GetLanguageMatchWeight(language, presentedRanges);
            if (matchWeight > bestWeight)
            {
                bestWeight = matchWeight;
            }
        }

        return bestWeight > 0d
            ? new AcceptLanguageMatch(true, 0, bestWeight)
            : default;
    }

    private static bool AreEquivalentAcceptLanguageValues(string left, string right)
    {
        var leftMap = BuildLanguageWeightMap(left);
        var rightMap = BuildLanguageWeightMap(right);

        if (leftMap.Count != rightMap.Count)
        {
            return false;
        }

        foreach (var item in leftMap)
        {
            if (!rightMap.TryGetValue(item.Key, out var rightWeight))
            {
                return false;
            }

            if (Math.Abs(item.Value - rightWeight) > 0.0001d)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, double> BuildLanguageWeightMap(string value)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var range in ParseLanguageRanges(value))
        {
            if (!map.TryGetValue(range.Range, out var existing) || range.Weight > existing)
            {
                map[range.Range] = range.Weight;
            }
        }

        return map;
    }

    private static List<LanguageRange> ParseLanguageRanges(string value)
    {
        var ranges = new List<LanguageRange>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return ranges;
        }

        foreach (var rawItem in value.Split(','))
        {
            var item = rawItem.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            var semicolonIndex = item.IndexOf(';');
            var language = semicolonIndex >= 0 ? item[..semicolonIndex] : item;
            language = language.Trim().ToLowerInvariant();
            if (language.Length == 0)
            {
                continue;
            }

            var weight = 1d;
            if (semicolonIndex >= 0)
            {
                var parameters = item[(semicolonIndex + 1)..].Split(';');
                foreach (var rawParameter in parameters)
                {
                    var parameter = rawParameter.Trim();
                    if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var rawQ = parameter[2..].Trim();
                    if (double.TryParse(rawQ, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedQ))
                    {
                        weight = Math.Max(0d, Math.Min(1d, parsedQ));
                    }

                    break;
                }
            }

            ranges.Add(new LanguageRange(language, weight));
        }

        return ranges;
    }

    private static bool TryGetContentLanguages(CachedHttpMetadata variant, out List<string> contentLanguages)
    {
        contentLanguages = [];

        if (TryGetHeaderValues(variant.Headers, "Content-Language", out var responseHeaderValues))
        {
            AppendTokens(contentLanguages, responseHeaderValues);
        }

        if (TryGetHeaderValues(variant.ContentHeaders, "Content-Language", out var contentHeaderValues))
        {
            AppendTokens(contentLanguages, contentHeaderValues);
        }

        return contentLanguages.Count > 0;
    }

    private static bool TryGetHeaderValues(
        Dictionary<string, string[]> headers,
        string headerName,
        out string[] values)
    {
        if (headers.TryGetValue(headerName, out var headerValues) && headerValues != null)
        {
            values = headerValues;
            return true;
        }

        foreach (var item in headers)
        {
            if (!item.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            values = item.Value;
            return true;
        }

        values = [];
        return false;
    }

    private static void AppendTokens(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            foreach (var token in value.Split(','))
            {
                var normalized = token.Trim();
                if (normalized.Length > 0)
                {
                    target.Add(normalized);
                }
            }
        }
    }

    private static double GetLanguageMatchWeight(string contentLanguage, IReadOnlyList<LanguageRange> presentedRanges)
    {
        var normalizedContentLanguage = contentLanguage.Trim().ToLowerInvariant();
        if (normalizedContentLanguage.Length == 0)
        {
            return 0d;
        }

        var bestWeight = 0d;
        foreach (var range in presentedRanges)
        {
            if (!LanguageRangeMatches(range.Range, normalizedContentLanguage))
            {
                continue;
            }

            if (range.Weight > bestWeight)
            {
                bestWeight = range.Weight;
            }
        }

        return bestWeight;
    }

    private static bool LanguageRangeMatches(string range, string languageTag)
    {
        if (range == "*")
        {
            return true;
        }

        if (languageTag.Equals(range, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return languageTag.StartsWith(range + "-", StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizeForSignature(string headerName, string value) =>
        headerName.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)
            ? CanonicalizeAcceptLanguage(value)
            : value;

    private static string EscapeVariantSignatureValue(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("=", "\\=");

    private static string CanonicalizeAcceptLanguage(string value)
    {
        var map = BuildLanguageWeightMap(value);
        if (map.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",",
            map.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => $"{item.Key};q={item.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
    }

    private readonly record struct VariantMatchScore(
        int ExactHeaderCount,
        int AcceptLanguageMatchRank,
        double AcceptLanguageWeight,
        DateTimeOffset CachedAt);

    private readonly record struct AcceptLanguageMatch(bool IsMatch, int MatchRank, double Weight);

    private readonly record struct LanguageRange(string Range, double Weight);
}
