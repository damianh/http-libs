// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DamianH.Http.ForwardProxy.Internal;

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
