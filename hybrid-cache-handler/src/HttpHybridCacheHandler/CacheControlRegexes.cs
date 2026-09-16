// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace DamianH.HttpHybridCacheHandler;

/// <summary>
/// Cached regexes for cache-control parsing.
/// </summary>
internal static partial class CacheControlRegexes
{
#if NET10_0_OR_GREATER
    [GeneratedRegex(@"stale-while-revalidate\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    internal static partial Regex StaleWhileRevalidate();

    [GeneratedRegex(@"stale-if-error\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    internal static partial Regex StaleIfError();

    [GeneratedRegex(@"(?:^|,)\s*no-cache\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    internal static partial Regex QualifiedNoCache();

    [GeneratedRegex(@"(?:^|,)\s*no-cache\s*(?:,|$)", RegexOptions.IgnoreCase)]
    internal static partial Regex UnqualifiedNoCache();

    [GeneratedRegex(@"(?:^|,)\s*must-understand\s*(?:,|$)", RegexOptions.IgnoreCase)]
    internal static partial Regex MustUnderstand();
#else
    private const RegexOptions LegacyOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex StaleWhileRevalidateRegex = new(@"stale-while-revalidate\s*=\s*(\d+)", LegacyOptions);
    private static readonly Regex StaleIfErrorRegex = new(@"stale-if-error\s*=\s*(\d+)", LegacyOptions);
    private static readonly Regex QualifiedNoCacheRegex = new(@"(?:^|,)\s*no-cache\s*=\s*""([^""]*)""", LegacyOptions);
    private static readonly Regex UnqualifiedNoCacheRegex = new(@"(?:^|,)\s*no-cache\s*(?:,|$)", LegacyOptions);
    private static readonly Regex MustUnderstandRegex = new(@"(?:^|,)\s*must-understand\s*(?:,|$)", LegacyOptions);
    internal static Regex StaleWhileRevalidate() => StaleWhileRevalidateRegex;
    internal static Regex StaleIfError() => StaleIfErrorRegex;
    internal static Regex QualifiedNoCache() => QualifiedNoCacheRegex;
    internal static Regex UnqualifiedNoCache() => UnqualifiedNoCacheRegex;
    internal static Regex MustUnderstand() => MustUnderstandRegex;
#endif
}
