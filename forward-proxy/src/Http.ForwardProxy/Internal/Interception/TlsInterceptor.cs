// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>Runs intercepted tunnels by handing their client side to the interception endpoint.</summary>
internal sealed class TlsInterceptor(InterceptionListenerFactory listener, IOptions<ForwardProxyOptions> options, ILogger<TlsInterceptor> logger)
{
    private readonly long _idleTimeoutMs = (long)options.Value.TunnelIdleTimeout.TotalMilliseconds;

    /// <summary>Serves the tunnel until the client or Kestrel closes it, or it is idle for the tunnel idle timeout.</summary>
    public async Task<TunnelPump.Result> RunAsync(
        string connectionId,
        IForwardProxyInterceptionFeature interception,
        PipeReader clientInput,
        PipeWriter clientOutput,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        Action abortClient,
        CancellationToken clientClosed)
    {
        var pipe = new ActivityDuplexPipe(clientInput, clientOutput);
        var connection = new InterceptedConnection(connectionId, pipe, interception, localEndPoint, remoteEndPoint, abortClient, clientClosed);
        if (!listener.TryAccept(connection))
        {
            Log.InterceptionEndpointUnavailable(logger, interception.Destination.Host, interception.Destination.Port);
            await connection.DisposeAsync().ConfigureAwait(false);
            return default;
        }

        var idleTimedOut = false;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(_idleTimeoutMs / 4, 10, 1000));
        var timer = new Timer(_ =>
        {
            if (Environment.TickCount64 - pipe.LastActivity > _idleTimeoutMs)
            {
                Volatile.Write(ref idleTimedOut, true);
                connection.Abort();
            }
        }, null, period, period);
        await using (timer.ConfigureAwait(false))
        {
            await connection.Completion.ConfigureAwait(false);
        }

        return new(pipe.BytesRead, pipe.BytesWritten, Volatile.Read(ref idleTimedOut));
    }
}
