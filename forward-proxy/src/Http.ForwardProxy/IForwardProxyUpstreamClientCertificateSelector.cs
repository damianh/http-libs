// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// Selects the client certificate presented to the destination for each TLS-intercepted request.
/// Register an implementation in DI with any lifetime; it is resolved from
/// <see cref="HttpContext.RequestServices"/> after request transforms and before the request is sent.
/// Plain HTTP forwarding and opaque <c>CONNECT</c> tunnels do not call this selector.
/// </summary>
/// <remarks>
/// The certificate must be currently valid and have a usable private key. The proxy pools connections separately
/// for each certificate's SHA-256 DER identity and never disposes the returned certificate; it owns a
/// private-key-preserving copy instead. Do not dispose the returned object concurrently with forwarding starting.
/// The destination must request client authentication to receive a certificate.
/// An exception or invalid certificate fails the request with <c>500 proxy_internal_error</c>, without fallback.
/// </remarks>
public interface IForwardProxyUpstreamClientCertificateSelector
{
    /// <summary>
    /// Returns the upstream client certificate, or <see langword="null"/> to forward without one.
    /// An absent selector also forwards without a client certificate; enforce required-certificate policy in the app.
    /// </summary>
    /// <param name="context">The decrypted client request, after preceding middleware and request transforms.</param>
    /// <param name="destination">The intercepted <c>CONNECT</c> destination.</param>
    /// <param name="cancellationToken">
    /// Signals client abort or expiry of <see cref="ForwardProxyOptions.ConnectTimeout"/>.
    /// </param>
    ValueTask<X509Certificate2?> SelectAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken);
}
