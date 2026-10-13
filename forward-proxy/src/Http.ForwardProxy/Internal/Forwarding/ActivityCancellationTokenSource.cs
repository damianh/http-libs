// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.
//
// Adapted from YARP v2.3.0 (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Utilities/ActivityCancellationTokenSource.cs).
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT License; see THIRD-PARTY-NOTICES.txt.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace DamianH.Http.ForwardProxy.Internal.Forwarding;

/// <summary>A pooled cancellation source whose timeout restarts on every successful read or write.</summary>
internal sealed class ActivityCancellationTokenSource : CancellationTokenSource
{
    // Avoid updating the timer when doing so won't meaningfully change the overall timeout.
    private const int TimeoutResolutionMs = 20;

    private const int MaxQueueSize = 1024;
    private static readonly ConcurrentQueue<ActivityCancellationTokenSource> SharedSources = new();
    private static int _count;

    private static readonly Action<object?> LinkedTokenCancelDelegate = static s =>
    {
        var cts = (ActivityCancellationTokenSource)s!;

        // A timeout or explicit Cancel can cascade into linked tokens; don't misattribute those.
        if (!cts.IsCancellationRequested)
        {
            cts.CancelledByLinkedToken = true;
            cts.Cancel(throwOnFirstException: false);
        }
    };

    private int _activityTimeoutMs;
    private uint _lastTimeoutTicks;
    private CancellationTokenRegistration _linkedRegistration;

    private ActivityCancellationTokenSource()
    {
    }

    public bool CancelledByLinkedToken { get; private set; }

    public static ActivityCancellationTokenSource Rent(TimeSpan activityTimeout, CancellationToken linkedToken)
    {
        if (SharedSources.TryDequeue(out var cts))
        {
            Interlocked.Decrement(ref _count);
        }
        else
        {
            cts = new ActivityCancellationTokenSource();
        }

        cts._activityTimeoutMs = (int)activityTimeout.TotalMilliseconds;
        cts._linkedRegistration = linkedToken.UnsafeRegister(LinkedTokenCancelDelegate, cts);
        cts._lastTimeoutTicks = (uint)Environment.TickCount;
        cts.CancelAfter(cts._activityTimeoutMs);
        return cts;
    }

    public void ResetTimeout()
    {
        var currentMs = (uint)Environment.TickCount;
        if (currentMs - _lastTimeoutTicks > TimeoutResolutionMs)
        {
            _lastTimeoutTicks = currentMs;
            CancelAfter(_activityTimeoutMs);
        }
    }

    public void Return()
    {
        _linkedRegistration.Dispose();
        _linkedRegistration = default;
        if (TryReset())
        {
            Debug.Assert(!CancelledByLinkedToken);
            if (Interlocked.Increment(ref _count) <= MaxQueueSize)
            {
                SharedSources.Enqueue(this);
                return;
            }

            Interlocked.Decrement(ref _count);
        }

        Dispose();
    }
}
