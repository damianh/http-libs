// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Security;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>
/// Adds the in-memory interception endpoint to Kestrel: TLS with the certificate provider's certificate for the
/// tunnel's destination, then HTTP/1.1 or HTTP/2 as negotiated through ALPN.
/// </summary>
internal sealed class InterceptionKestrelSetup(
    IServiceProvider services,
    IOptions<ForwardProxyTlsInterceptionOptions> options,
    ILogger<InterceptionKestrelSetup> logger) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions kestrel)
    {
        if (!services.GetServices<IConnectionListenerFactory>().Any(factory => factory is not InterceptionListenerFactory))
        {
            throw new InvalidOperationException(
                "No Kestrel transport is registered besides the TLS interception endpoint. Call AddForwardProxyTlsInterception() after the web host is configured to use Kestrel, so the default socket transport is registered first.");
        }

        var provider = services.GetService<IForwardProxyCertificateProvider>()
            ?? throw new InvalidOperationException($"TLS interception requires an {nameof(IForwardProxyCertificateProvider)} service.");
        var interceptionOptions = options.Value;
        kestrel.Listen(InterceptionEndPoint.Instance, listen =>
        {
            listen.Protocols = interceptionOptions.ClientProtocols;
            listen.UseHttps(new TlsHandshakeCallbackOptions
            {
                HandshakeTimeout = interceptionOptions.HandshakeTimeout,
                OnConnection = context => GetServerOptionsAsync(context, provider),
            });
            listen.Use(next => connection =>
            {
                if (!ForwardProxyConnectionMiddleware.IsHttp2Alpn(connection))
                {
                    ForwardProxyConnectionMiddleware.ObserveRequestHeads(connection, kestrel);
                }

                return next(connection);
            });
        });
    }

    private async ValueTask<SslServerAuthenticationOptions> GetServerOptionsAsync(TlsHandshakeCallbackContext context, IForwardProxyCertificateProvider provider)
    {
        var destination = context.Connection.Features.Get<IForwardProxyInterceptionFeature>()?.Destination
            ?? throw new InvalidOperationException("The connection is not an intercepted tunnel.");
        SslStreamCertificateContext? certificate;
        try
        {
            certificate = await provider.GetCertificateAsync(destination, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            Log.InterceptionCertificateFailed(logger, ex, destination.Host, destination.Port);
            throw;
        }

        if (certificate is null)
        {
            Log.InterceptionCertificateMissing(logger, destination.Host, destination.Port);
            throw new InvalidOperationException($"No certificate is available for {destination.Host}.");
        }

        return new SslServerAuthenticationOptions { ServerCertificateContext = certificate };
    }
}
