// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>
/// A Kestrel transport for <see cref="InterceptionEndPoint"/>. Instead of accepting sockets it accepts the client side
/// of intercepted tunnels, so Kestrel serves them with its full TLS, HTTP/1.1 and HTTP/2 stack.
/// </summary>
internal sealed class InterceptionListenerFactory : IConnectionListenerFactory, IConnectionListenerFactorySelector
{
    private Listener? _listener;

    public bool CanBind(EndPoint endpoint) => endpoint is InterceptionEndPoint;

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not InterceptionEndPoint)
        {
            throw new NotSupportedException($"{endpoint.GetType().Name} is not supported.");
        }

        var listener = new Listener(this, endpoint);
        Volatile.Write(ref _listener, listener);
        return new(listener);
    }

    /// <summary>Hands a connection to Kestrel. Returns <see langword="false"/> when the endpoint is not listening.</summary>
    public bool TryAccept(ConnectionContext connection) => Volatile.Read(ref _listener)?.TryEnqueue(connection) == true;

    private sealed class Listener(InterceptionListenerFactory owner, EndPoint endPoint) : IConnectionListener
    {
        private readonly Channel<ConnectionContext> _connections = Channel.CreateUnbounded<ConnectionContext>(new() { SingleReader = true });

        public EndPoint EndPoint => endPoint;

        public bool TryEnqueue(ConnectionContext connection) => _connections.Writer.TryWrite(connection);

        public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _connections.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.CompareExchange(ref owner._listener, null, this);
            _connections.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await UnbindAsync().ConfigureAwait(false);
            while (_connections.Reader.TryRead(out var connection))
            {
                connection.Abort();
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
