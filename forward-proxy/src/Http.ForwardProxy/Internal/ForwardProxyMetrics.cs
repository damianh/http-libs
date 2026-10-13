// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Metrics and tracing for the forward proxy.</summary>
internal sealed class ForwardProxyMetrics
{
    public const string MeterName = "DamianH.Http.ForwardProxy";
    public static readonly ActivitySource ActivitySource = new(MeterName);

    private readonly Counter<long> _requests;
    private readonly UpDownCounter<long> _activeTunnels;
    private readonly Counter<long> _tunnelBytes;
    private readonly Histogram<double> _tunnelDuration;

    public ForwardProxyMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("forward_proxy.requests", "{request}", "Proxy requests by kind and outcome.");
        _activeTunnels = meter.CreateUpDownCounter<long>("forward_proxy.tunnels.active", "{tunnel}", "Open CONNECT tunnels.");
        _tunnelBytes = meter.CreateCounter<long>("forward_proxy.tunnel.bytes", "By", "Bytes relayed through CONNECT tunnels.");
        _tunnelDuration = meter.CreateHistogram<double>("forward_proxy.tunnel.duration", "s", "CONNECT tunnel lifetime.");
    }

    public void Request(ForwardProxyRequestKind kind, string outcome) =>
        _requests.Add(1, new("forward_proxy.kind", kind == ForwardProxyRequestKind.Connect ? "connect" : "http"), new("forward_proxy.outcome", outcome));

    public void TunnelOpened() => _activeTunnels.Add(1);

    public void TunnelClosed(double seconds, long bytesUpstream, long bytesDownstream)
    {
        _activeTunnels.Add(-1);
        _tunnelDuration.Record(seconds);
        _tunnelBytes.Add(bytesUpstream, new KeyValuePair<string, object?>("forward_proxy.direction", "upstream"));
        _tunnelBytes.Add(bytesDownstream, new KeyValuePair<string, object?>("forward_proxy.direction", "downstream"));
    }
}

internal static class Outcomes
{
    public const string Allowed = "allowed";
    public const string Unauthorized = "unauthorized";
    public const string Denied = "denied";
    public const string Error = "error";
}

/// <summary>Bounds concurrent tunnels.</summary>
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
