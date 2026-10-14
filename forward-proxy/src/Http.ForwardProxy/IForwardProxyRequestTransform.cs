// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// Adjusts requests the proxy forwards to destinations, and their responses, for example to add or replace an
/// <c>Authorization</c> header. Applies to absolute-form <c>http://</c> requests and to requests on TLS-intercepted
/// tunnels; it never sees the bytes of a plain <c>CONNECT</c> tunnel. Register implementations in DI with any lifetime;
/// they are resolved from <see cref="HttpContext.RequestServices"/> for each request and run in registration order.
/// </summary>
/// <remarks>
/// Hop-by-hop and proxy headers have already been removed from the outbound request, and <c>Via</c> added when
/// enabled. An exception from a transform fails the request with <c>500</c> and <c>proxy_internal_error</c>.
/// </remarks>
public interface IForwardProxyRequestTransform
{
    /// <summary>Called before <paramref name="request"/> is sent to the destination.</summary>
    /// <param name="context">The client request. For intercepted tunnels, see <see cref="IForwardProxyInterceptionFeature"/>.</param>
    /// <param name="destination">The destination. Intercepted requests have <see cref="ForwardProxyRequestKind.Connect"/>.</param>
    /// <param name="request">
    /// The outbound request. Changing its <see cref="HttpRequestMessage.RequestUri"/> or overriding <c>Host</c> with
    /// a different normalized destination authority fails the request with <c>500</c> and <c>proxy_internal_error</c>.
    /// </param>
    /// <param name="cancellationToken">Signals that the client aborted the request.</param>
    ValueTask TransformRequestAsync(HttpContext context, ForwardProxyDestination destination, HttpRequestMessage request, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    /// <summary>Called when the destination's response head arrives, before it is copied to the client.</summary>
    /// <param name="context">The client request.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="response">The destination's response. Its content is streamed to the client afterwards.</param>
    /// <param name="cancellationToken">Signals that the client aborted the request.</param>
    ValueTask TransformResponseAsync(HttpContext context, ForwardProxyDestination destination, HttpResponseMessage response, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
