// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

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
        _activeTunnels = meter.CreateUpDownCounter<long>("forward_proxy.tunnels.active", "{tunnel}", "Open tunnels (CONNECT and upgraded connections).");
        _tunnelBytes = meter.CreateCounter<long>("forward_proxy.tunnel.bytes", "By", "Bytes relayed through tunnels (CONNECT and upgraded connections).");
        _tunnelDuration = meter.CreateHistogram<double>("forward_proxy.tunnel.duration", "s", "Tunnel lifetime (CONNECT and upgraded connections).");
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

/// <summary>Tracing, metrics and logging for one open tunnel (CONNECT or upgraded connection).</summary>
internal sealed class TunnelTelemetry
{
    private readonly ForwardProxyMetrics _metrics;
    private readonly ILogger _logger;
    private readonly ForwardProxyDestination _destination;
    private readonly Activity? _activity;
    private readonly long _started;

    public TunnelTelemetry(ForwardProxyMetrics metrics, ILogger logger, ForwardProxyDestination destination)
    {
        _metrics = metrics;
        _logger = logger;
        _destination = destination;
        _activity = ForwardProxyMetrics.ActivitySource.StartActivity("ForwardProxy.Tunnel", ActivityKind.Internal);
        _activity?.SetTag("server.address", destination.Host);
        _activity?.SetTag("server.port", destination.Port);
        metrics.TunnelOpened();
        _started = Stopwatch.GetTimestamp();
    }

    public void Complete(long bytesUpstream, long bytesDownstream, bool idleTimedOut)
    {
        var elapsed = Stopwatch.GetElapsedTime(_started);
        _metrics.TunnelClosed(elapsed.TotalSeconds, bytesUpstream, bytesDownstream);
        if (idleTimedOut)
        {
            Log.TunnelIdleTimeout(_logger, _destination.Host, _destination.Port);
        }

        Log.TunnelClosed(_logger, _destination.Host, _destination.Port, elapsed.TotalMilliseconds, bytesUpstream, bytesDownstream);
        _activity?.Dispose();
    }
}
