// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Security;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DamianH.Http.ForwardProxy;

/// <summary>Configures TLS interception of <c>CONNECT</c> tunnels.</summary>
public sealed class ForwardProxyTlsInterceptionOptions
{
    /// <summary>
    /// HTTP versions offered to intercepted clients through ALPN. Defaults to <see cref="HttpProtocols.Http1AndHttp2"/>.
    /// Must be <see cref="HttpProtocols.Http1"/>, <see cref="HttpProtocols.Http2"/> or <see cref="HttpProtocols.Http1AndHttp2"/>.
    /// </summary>
    public HttpProtocols ClientProtocols { get; set; } = HttpProtocols.Http1AndHttp2;

    /// <summary>
    /// Time allowed for an intercepted client to complete its TLS handshake. Defaults to 10 seconds. Must be positive
    /// and at most <see cref="int.MaxValue"/> milliseconds (about 24.8 days).
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Validates destination certificates for intercepted requests. <see langword="null"/> (default) uses the
    /// platform's default validation: the certificate must chain to a trusted root and match the destination host.
    /// A failure is reported to the client as <c>502</c> with <c>tls_certificate_error</c>.
    /// </summary>
    public RemoteCertificateValidationCallback? DestinationCertificateValidation { get; set; }

    /// <summary>
    /// Maximum cached upstream client-certificate identities. Defaults to 128 and must be positive.
    /// The no-certificate pool is separate. Idle entries may be evicted to make room; when all entries are active,
    /// a request needing another identity fails with <c>503 connection_limit_reached</c>.
    /// </summary>
    public int MaxUpstreamClientCertificatePools { get; set; } = 128;

    /// <summary>
    /// Time without active forwarding operations before an upstream client-certificate pool is disposed.
    /// Defaults to 2 minutes. Must be positive and at most <see cref="int.MaxValue"/> milliseconds.
    /// Streaming responses and upgraded connections keep their pool active until forwarding completes.
    /// </summary>
    public TimeSpan UpstreamClientCertificatePoolIdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
