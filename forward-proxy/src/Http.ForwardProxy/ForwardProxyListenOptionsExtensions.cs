// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DamianH.Http.ForwardProxy;

/// <summary>Adds HTTP/1.1 CONNECT support to a Kestrel endpoint.</summary>
public static class ForwardProxyListenOptionsExtensions
{
    /// <summary>
    /// Handles HTTP/1.x CONNECT when it is the first request on a connection, then relays the connection as a raw
    /// tunnel. Other connections are passed to Kestrel unchanged. When the endpoint uses TLS, call this after
    /// <c>UseHttps()</c>.
    /// </summary>
    public static ListenOptions UseForwardProxy(this ListenOptions listenOptions)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        listenOptions.Use(next => new ForwardProxyConnectionMiddleware(next, listenOptions.ApplicationServices).OnConnectionAsync);
        return listenOptions;
    }
}
