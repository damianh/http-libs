// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http.Headers;

namespace DamianH.HttpHybridCacheHandler;

internal static class HeaderCompatibility
{
    public static IEnumerable<KeyValuePair<string, string[]>> Read(HttpHeaders headers)
    {
#if NET10_0_OR_GREATER
        foreach (var header in headers.NonValidated)
#else
        // Legacy public APIs can normalize parsed values; preserve the exposed value boundaries.
        foreach (var header in headers)
#endif
        {
            yield return new KeyValuePair<string, string[]>(header.Key, header.Value.ToArray());
        }
    }
}
