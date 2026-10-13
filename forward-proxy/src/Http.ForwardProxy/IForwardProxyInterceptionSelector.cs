// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// Decides which <c>CONNECT</c> tunnels are TLS-intercepted. Evaluated after authorization and the destination rules
/// have admitted the tunnel. Resolved from <see cref="HttpContext.RequestServices"/> for each <c>CONNECT</c>, so it may
/// have any lifetime. Without a registered selector no tunnel is intercepted.
/// </summary>
/// <remarks>
/// Only select destinations that speak TLS: an intercepted tunnel expects a TLS ClientHello and closes the connection
/// otherwise. Interception requires
/// <see cref="ForwardProxyServiceCollectionExtensions.AddForwardProxyTlsInterception(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{ForwardProxyTlsInterceptionOptions}?)"/>.
/// </remarks>
public interface IForwardProxyInterceptionSelector
{
    /// <summary>Returns <see langword="true"/> to terminate the client's TLS and handle its requests in the proxy.</summary>
    /// <param name="context">The <c>CONNECT</c> request; <see cref="HttpContext.User"/> is the authenticated proxy client.</param>
    /// <param name="destination">The admitted destination.</param>
    /// <param name="cancellationToken">Signals that the <c>CONNECT</c> request was aborted.</param>
    ValueTask<bool> ShouldInterceptAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken);
}
