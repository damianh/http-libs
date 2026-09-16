// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace DamianH.HttpHybridCacheHandler;

internal static class Guard
{
    public static void NotNull([NotNull] object? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(name);
        }
    }

    public static void NotNullOrWhiteSpace([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        NotNull(value, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be empty or whitespace.", name);
        }
    }

    public static void NotNegative(long value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must not be negative.");
        }
    }

    public static void NotLessThan(long value, long minimum, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"The value must be at least {minimum}.");
        }
    }

    public static void NotDisposed(bool disposed, object instance)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
    }
}
