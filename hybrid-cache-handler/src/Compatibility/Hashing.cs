// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;

namespace DamianH.HttpHybridCacheHandler;

internal static class Hashing
{
    public static byte[] Sha256(byte[] content)
    {
#if NET10_0_OR_GREATER
        return SHA256.HashData(content);
#else
        using var algorithm = SHA256.Create();
        return algorithm.ComputeHash(content);
#endif
    }

    public static string ToHex(byte[] bytes, bool lowercase = false)
    {
#if NET10_0_OR_GREATER
        return lowercase ? Convert.ToHexStringLower(bytes) : Convert.ToHexString(bytes);
#else
        var alphabet = lowercase ? "0123456789abcdef" : "0123456789ABCDEF";
        var characters = new char[checked(bytes.Length * 2)];
        for (var i = 0; i < bytes.Length; i++)
        {
            characters[i * 2] = alphabet[bytes[i] >> 4];
            characters[i * 2 + 1] = alphabet[bytes[i] & 15];
        }
        return new string(characters);
#endif
    }
}
