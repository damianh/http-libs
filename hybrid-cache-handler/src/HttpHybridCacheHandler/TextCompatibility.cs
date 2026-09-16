// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;

namespace DamianH.HttpHybridCacheHandler;

internal static class TextCompatibility
{
    public static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
    public static bool IsAsciiLetterOrDigit(char value) =>
        IsAsciiDigit(value) || value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static bool IsNullOrEmpty([NotNullWhen(false)] string? value) => string.IsNullOrEmpty(value);
    public static bool IsNullOrWhiteSpace([NotNullWhen(false)] string? value) => string.IsNullOrWhiteSpace(value);

    public static string[] SplitTrimmed(this string value, char separator)
    {
#if NET10_0_OR_GREATER
        return value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
#else
        return value.Split([separator], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim()).Where(part => part.Length != 0).ToArray();
#endif
    }

#if NETSTANDARD2_0 || NETFRAMEWORK
    public static string[] Split(this string value, char separator, int count) =>
        value.Split([separator], count, StringSplitOptions.None);
#endif
}
