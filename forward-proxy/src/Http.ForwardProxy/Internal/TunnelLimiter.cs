// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Bounds concurrent tunnels: CONNECT tunnels and upgraded (WebSocket) connections share one limit.</summary>
internal sealed class TunnelLimiter(int? limit)
{
    private int _count;

    public bool TryAcquire()
    {
        if (limit is not { } max)
        {
            Interlocked.Increment(ref _count);
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _count);
            if (current >= max)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _count, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Release() => Interlocked.Decrement(ref _count);
}
