// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Claims;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// Present on requests received through a TLS-intercepted <c>CONNECT</c> tunnel. Middleware added before
/// <c>UseForwardProxy()</c> can use it to recognise intercepted requests and adjust them before they are forwarded.
/// </summary>
public interface IForwardProxyInterceptionFeature
{
    /// <summary>The <c>CONNECT</c> destination; every request on the tunnel is forwarded to it over HTTPS.</summary>
    ForwardProxyDestination Destination { get; }

    /// <summary>
    /// The proxy client authenticated by the <c>CONNECT</c> request. Requests on the tunnel are not authenticated
    /// again; <see cref="Microsoft.AspNetCore.Http.HttpContext.User"/> is initialised to this principal.
    /// </summary>
    ClaimsPrincipal User { get; }
}
