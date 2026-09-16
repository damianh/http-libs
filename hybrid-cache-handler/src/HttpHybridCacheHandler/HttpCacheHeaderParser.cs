// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.HttpHybridCacheHandler;

internal static class HttpCacheHeaderParser
{
    internal readonly record struct CacheControlDirectives(
        TimeSpan? MaxAge,
        TimeSpan? SharedMaxAge,
        TimeSpan? StaleWhileRevalidate,
        TimeSpan? StaleIfError,
        bool NoCache,
        bool NoStore,
        bool MustRevalidate,
        bool Private,
        bool Public);

    public static CacheControlDirectives ParseCacheControl(IEnumerable<string> cacheControlValues)
    {
        TimeSpan? maxAge = null;
        TimeSpan? sharedMaxAge = null;
        TimeSpan? staleWhileRevalidate = null;
        TimeSpan? staleIfError = null;
        var noCache = false;
        var noStore = false;
        var mustRevalidate = false;
        var isPrivate = false;
        var isPublic = false;

        foreach (var value in cacheControlValues)
        {
            foreach (var directive in EnumerateDirectives(value))
            {
                if (directive.Length == 0)
                {
                    continue;
                }

                var equalsIndex = directive.IndexOf('=');
                if (equalsIndex < 0)
                {
                    if (directive.Equals("no-cache", StringComparison.OrdinalIgnoreCase))
                    {
                        noCache = true;
                    }
                    else if (directive.Equals("no-store", StringComparison.OrdinalIgnoreCase))
                    {
                        noStore = true;
                    }
                    else if (directive.Equals("must-revalidate", StringComparison.OrdinalIgnoreCase))
                    {
                        mustRevalidate = true;
                    }
                    else if (directive.Equals("private", StringComparison.OrdinalIgnoreCase))
                    {
                        isPrivate = true;
                    }
                    else if (directive.Equals("public", StringComparison.OrdinalIgnoreCase))
                    {
                        isPublic = true;
                    }

                    continue;
                }

                var name = directive[..equalsIndex];
                var rawValue = directive[(equalsIndex + 1)..];

                if (name.Equals("no-cache", StringComparison.OrdinalIgnoreCase))
                {
                    noCache = true;
                }
                else if (name.Equals("private", StringComparison.OrdinalIgnoreCase))
                {
                    isPrivate = true;
                }
                else if (name.Equals("max-age", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseLenientDeltaSeconds(rawValue, out var parsed))
                    {
                        maxAge = Max(maxAge, parsed);
                    }
                }
                else if (name.Equals("s-maxage", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseLenientDeltaSeconds(rawValue, out var parsed))
                    {
                        sharedMaxAge = Max(sharedMaxAge, parsed);
                    }
                }
                else if (name.Equals("stale-while-revalidate", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseLenientDeltaSeconds(rawValue, out var parsed))
                    {
                        staleWhileRevalidate = Max(staleWhileRevalidate, parsed);
                    }
                }
                else if (name.Equals("stale-if-error", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseLenientDeltaSeconds(rawValue, out var parsed))
                    {
                        staleIfError = Max(staleIfError, parsed);
                    }
                }
            }
        }

        return new CacheControlDirectives(
            maxAge,
            sharedMaxAge,
            staleWhileRevalidate,
            staleIfError,
            noCache,
            noStore,
            mustRevalidate,
            isPrivate,
            isPublic);
    }

    public static TimeSpan? ParseAge(IEnumerable<string> ageValues)
    {
        var firstLine = ageValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        var firstMember = firstLine.Split(',', 2)[0].Trim();
        var semicolonIndex = firstMember.IndexOf(';');
        if (semicolonIndex >= 0)
        {
            firstMember = firstMember[..semicolonIndex].Trim();
        }

        if (firstMember.Length == 0 || !firstMember.All(TextCompatibility.IsAsciiDigit))
        {
            return null;
        }

        var seconds = ParseSaturatedSeconds(firstMember);
        return TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
    }

    public static DateTimeOffset? ParseSingleHttpDate(IEnumerable<string> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var first = enumerator.Current;
        if (enumerator.MoveNext())
        {
            return null;
        }

        return ParseHttpDate(first);
    }

    private static IEnumerable<string> EnumerateDirectives(string value)
    {
        var start = 0;
        var inQuotes = false;

        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }

            if (ch == ',' && !inQuotes)
            {
                yield return value[start..i].Trim();
                start = i + 1;
            }
        }

        if (start <= value.Length)
        {
            yield return value[start..].Trim();
        }
    }

    private static TimeSpan? Max(TimeSpan? current, TimeSpan candidate)
        => current.HasValue && current.Value >= candidate ? current : candidate;

    private static bool TryParseLenientDeltaSeconds(string rawValue, out TimeSpan seconds)
    {
        seconds = TimeSpan.Zero;
        if (rawValue.Length == 0)
        {
            return false;
        }

        var candidate = rawValue;
        if (candidate.Length >= 2 && candidate[0] == '"' && candidate[^1] == '"')
        {
            candidate = candidate[1..^1];
        }
        else if (candidate.Contains('"'))
        {
            return false;
        }

        if (candidate.Length == 0 ||
            candidate[0] == '-' ||
            candidate.Any(char.IsWhiteSpace) ||
            candidate.Contains('\''))
        {
            return false;
        }

        foreach (var ch in candidate)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '.')
            {
                return false;
            }
        }

        var digitStart = -1;
        for (var i = 0; i < candidate.Length; i++)
        {
            if (TextCompatibility.IsAsciiDigit(candidate[i]))
            {
                digitStart = i;
                break;
            }
        }

        if (digitStart < 0)
        {
            return false;
        }

        var digitEnd = digitStart;
        while (digitEnd < candidate.Length && TextCompatibility.IsAsciiDigit(candidate[digitEnd]))
        {
            digitEnd++;
        }

        var numericPart = candidate[digitStart..digitEnd];
        var parsedSeconds = ParseSaturatedSeconds(numericPart);
        seconds = TimeSpan.FromTicks(parsedSeconds * TimeSpan.TicksPerSecond);
        return true;
    }

    private static long ParseSaturatedSeconds(string digits)
    {
        var maxSeconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
        long value = 0;
        foreach (var ch in digits)
        {
            var digit = ch - '0';
            if (value > (maxSeconds - digit) / 10)
            {
                return maxSeconds;
            }

            value = (value * 10) + digit;
        }

        return value;
    }

    private static DateTimeOffset? ParseHttpDate(string value)
        => TryParseImfFixdate(value, out var imfDate)
            ? imfDate
            : TryParseRfc850(value, out var rfc850Date)
                ? rfc850Date
                : TryParseAsctime(value, out var asctimeDate)
                    ? asctimeDate
                    : null;

    private static bool TryParseImfFixdate(string value, out DateTimeOffset date)
    {
        date = default;
        if (value.Length != 29 ||
            value[3] != ',' ||
            value[4] != ' ' ||
            value[7] != ' ' ||
            value[11] != ' ' ||
            value[16] != ' ' ||
            value[19] != ':' ||
            value[22] != ':' ||
            value[25] != ' ')
        {
            return false;
        }

        if (!value.AsSpan(0, 3).ToString().All(char.IsLetter) ||
            !value.AsSpan(26, 3).ToString().Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseInt(value.AsSpan(5, 2), out var day) ||
            !TryParseMonth(value.AsSpan(8, 3), out var month) ||
            !TryParseInt(value.AsSpan(12, 4), out var year) ||
            !TryParseInt(value.AsSpan(17, 2), out var hour) ||
            !TryParseInt(value.AsSpan(20, 2), out var minute) ||
            !TryParseInt(value.AsSpan(23, 2), out var second))
        {
            return false;
        }

        return TryCreateDateTimeOffset(year, month, day, hour, minute, second, out date);
    }

    private static bool TryParseRfc850(string value, out DateTimeOffset date)
    {
        date = default;

        var commaIndex = value.IndexOf(", ", StringComparison.Ordinal);
        if (commaIndex <= 0)
        {
            return false;
        }

        if (!value.AsSpan(0, commaIndex).ToString().All(char.IsLetter))
        {
            return false;
        }

        var rest = value[(commaIndex + 2)..];
        if (rest.Length != 22 ||
            rest[2] != '-' ||
            rest[6] != '-' ||
            rest[9] != ' ' ||
            rest[12] != ':' ||
            rest[15] != ':' ||
            rest[18] != ' ' ||
            !rest.AsSpan(19, 3).ToString().Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseInt(rest.AsSpan(0, 2), out var day) ||
            !TryParseMonth(rest.AsSpan(3, 3), out var month) ||
            !TryParseInt(rest.AsSpan(7, 2), out var yearTwoDigits) ||
            !TryParseInt(rest.AsSpan(10, 2), out var hour) ||
            !TryParseInt(rest.AsSpan(13, 2), out var minute) ||
            !TryParseInt(rest.AsSpan(16, 2), out var second))
        {
            return false;
        }

        var year = yearTwoDigits >= 70
            ? 1900 + yearTwoDigits
            : 2000 + yearTwoDigits;

        return TryCreateDateTimeOffset(year, month, day, hour, minute, second, out date);
    }

    private static bool TryParseAsctime(string value, out DateTimeOffset date)
    {
        date = default;
        if (value.Length != 24 ||
            value[3] != ' ' ||
            value[7] != ' ' ||
            value[10] != ' ' ||
            value[13] != ':' ||
            value[16] != ':' ||
            value[19] != ' ')
        {
            return false;
        }

        if (!value.AsSpan(0, 3).ToString().All(char.IsLetter))
        {
            return false;
        }

        if (!TryParseMonth(value.AsSpan(4, 3), out var month) ||
            !TryParseInt(value.AsSpan(11, 2), out var hour) ||
            !TryParseInt(value.AsSpan(14, 2), out var minute) ||
            !TryParseInt(value.AsSpan(17, 2), out var second) ||
            !TryParseInt(value.AsSpan(20, 4), out var year))
        {
            return false;
        }

        var daySpan = value.AsSpan(8, 2);
        if ((daySpan[0] != ' ' && !TextCompatibility.IsAsciiDigit(daySpan[0])) || !TextCompatibility.IsAsciiDigit(daySpan[1]))
        {
            return false;
        }

        var dayText = daySpan[0] == ' ' ? daySpan[1..].ToString() : daySpan.ToString();
        if (!int.TryParse(dayText, out var day))
        {
            return false;
        }

        return TryCreateDateTimeOffset(year, month, day, hour, minute, second, out date);
    }

    private static bool TryParseMonth(ReadOnlySpan<char> monthText, out int month)
    {
        month = monthText.ToString().ToUpperInvariant() switch
        {
            "JAN" => 1,
            "FEB" => 2,
            "MAR" => 3,
            "APR" => 4,
            "MAY" => 5,
            "JUN" => 6,
            "JUL" => 7,
            "AUG" => 8,
            "SEP" => 9,
            "OCT" => 10,
            "NOV" => 11,
            "DEC" => 12,
            _ => 0
        };

        return month != 0;
    }

    private static bool TryParseInt(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var ch in text)
        {
            if (!TextCompatibility.IsAsciiDigit(ch))
            {
                return false;
            }

            value = (value * 10) + (ch - '0');
        }

        return true;
    }

    private static bool TryCreateDateTimeOffset(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        out DateTimeOffset value)
    {
        try
        {
            value = new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            value = default;
            return false;
        }
    }
}
