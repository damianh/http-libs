// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>
/// The client side of an intercepted tunnel, presented to Kestrel as a transport connection. Aborting it aborts the
/// client connection or stream that carries the tunnel.
/// </summary>
internal sealed class InterceptedConnection : ConnectionContext
{
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _abortClient;
    private readonly CancellationTokenRegistration _clientClosedRegistration;
    private int _aborted;

    public InterceptedConnection(
        string connectionId,
        IDuplexPipe transport,
        IForwardProxyInterceptionFeature interception,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint,
        Action abortClient,
        CancellationToken clientClosed)
    {
        ConnectionId = connectionId;
        Transport = transport;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
        Features.Set(interception);
        _abortClient = abortClient;
        _clientClosedRegistration = clientClosed.UnsafeRegister(static s => ((InterceptedConnection)s!).SignalClosed(), this);
    }

    public override string ConnectionId { get; set; }

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override IDictionary<object, object?> Items { get; set; } = new ConnectionItems();

    public override IDuplexPipe Transport { get; set; }

    public override CancellationToken ConnectionClosed
    {
        get => _closed.Token;
        set => throw new NotSupportedException();
    }

    /// <summary>Completes when Kestrel has finished with the connection.</summary>
    public Task Completion => _completed.Task;

    public override void Abort(ConnectionAbortedException abortReason)
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 0)
        {
            _abortClient();
            SignalClosed();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _clientClosedRegistration.DisposeAsync().ConfigureAwait(false);
        _completed.TrySetResult();
    }

    // Like a socket transport, raise ConnectionClosed off the caller's thread.
    private void SignalClosed() => ThreadPool.UnsafeQueueUserWorkItem(static cts =>
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }, _closed, preferLocal: false);
}
