// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DamianH.Http.ForwardProxy;

/// <summary>Adds the forward proxy to the request pipeline.</summary>
public static class ForwardProxyApplicationBuilderExtensions
{
    /// <summary>
    /// Handles absolute-form <c>http://</c> proxy requests and HTTP/2 CONNECT tunnels. Other requests continue down the
    /// pipeline. Add this before routing, authentication and authorization middleware: the proxy runs its own
    /// authentication and authorization using <see cref="ForwardProxyOptions.AuthorizationPolicy"/>.
    /// HTTP/1.1 CONNECT is handled at the connection level; see
    /// <see cref="ForwardProxyListenOptionsExtensions.UseForwardProxy(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions)"/>.
    /// </summary>
    public static IApplicationBuilder UseForwardProxy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.ApplicationServices.GetService<ForwardProxyHandler>() is null)
        {
            throw new InvalidOperationException("Forward proxy services are not registered. Call services.AddForwardProxy().");
        }

        return app.UseMiddleware<ForwardProxyMiddleware>();
    }
}
