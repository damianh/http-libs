// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Security;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// Supplies the server certificate the proxy presents to clients of intercepted tunnels. Resolved from the root service
/// provider, so it must be a singleton or have no scoped dependencies.
/// </summary>
/// <remarks>
/// <para>
/// The certificate must be valid for <see cref="ForwardProxyDestination.Host"/> (a DNS or IP subject alternative name)
/// and chain to a certificate authority the clients trust. It is always selected by the <c>CONNECT</c> destination;
/// the client's SNI is not used.
/// </para>
/// <para>
/// Called for every intercepted TLS handshake. Implementations should cache certificate contexts, for example per
/// host, because creating an <see cref="SslStreamCertificateContext"/> builds the certificate chain.
/// </para>
/// </remarks>
public interface IForwardProxyCertificateProvider
{
    /// <summary>
    /// Returns the certificate context for <paramref name="destination"/>, or <see langword="null"/> to fail the
    /// handshake and close the tunnel.
    /// </summary>
    /// <param name="destination">The intercepted <c>CONNECT</c> destination.</param>
    /// <param name="cancellationToken">Signals that the handshake was aborted or timed out.</param>
    ValueTask<SslStreamCertificateContext?> GetCertificateAsync(ForwardProxyDestination destination, CancellationToken cancellationToken);
}
