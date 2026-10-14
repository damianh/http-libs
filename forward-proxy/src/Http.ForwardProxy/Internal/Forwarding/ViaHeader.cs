// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Primitives;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>Reads RFC 9110 section 7.6.3 <c>Via</c> field values.</summary>
internal static class ViaHeader
{
    /// <summary>
    /// Whether any <c>Via</c> entry's received-by (<c>pseudonym</c> or <c>uri-host [ ":" port ]</c>) equals
    /// <paramref name="receivedBy"/>, compared case-insensitively.
    /// </summary>
    public static bool ContainsReceivedBy(StringValues values, string receivedBy)
    {
        foreach (var value in values)
        {
            if (value is not null && ContainsReceivedBy(value.AsSpan(), receivedBy))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsReceivedBy(ReadOnlySpan<char> value, string receivedBy)
    {
        // Via = #( received-protocol RWS received-by [ RWS comment ] ). Commas inside a comment do not separate entries.
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length)
            {
                var c = value[i];
                if (c == '\\' && depth > 0 && i + 1 < value.Length)
                {
                    i++;
                    continue;
                }

                if (c == '(')
                {
                    depth++;
                    continue;
                }

                if (c == ')' && depth > 0)
                {
                    depth--;
                    continue;
                }

                if (c != ',' || depth > 0)
                {
                    continue;
                }
            }

            if (EntryReceivedBy(value[start..i]).Equals(receivedBy, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            start = i + 1;
        }

        return false;
    }

    private static ReadOnlySpan<char> EntryReceivedBy(ReadOnlySpan<char> entry)
    {
        entry = entry.Trim(" \t");
        var protocolEnd = entry.IndexOfAny(' ', '\t');
        if (protocolEnd < 0)
        {
            return [];
        }

        var rest = entry[protocolEnd..].TrimStart(" \t");
        var receivedByEnd = rest.IndexOfAny(' ', '\t');
        return receivedByEnd < 0 ? rest : rest[..receivedByEnd];
    }
}
