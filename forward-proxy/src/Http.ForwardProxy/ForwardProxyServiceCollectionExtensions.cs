// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
using DamianH.Http.ForwardProxy.Internal.Interception;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DamianH.Http.ForwardProxy;

/// <summary>Registers forward proxy services.</summary>
public static class ForwardProxyServiceCollectionExtensions
{
    /// <summary>Adds the forward proxy services.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="ForwardProxyOptions"/>.</param>
    public static IServiceCollection AddForwardProxy(this IServiceCollection services, Action<ForwardProxyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<ForwardProxyOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        builder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ForwardProxyOptions>, ForwardProxyOptionsValidator>());

        services.AddAuthenticationCore();
        services.AddAuthorization();
        services.AddMetrics();
        services.TryAddSingleton<ForwardProxyMetrics>();
        services.TryAddSingleton<DestinationPolicy>();
        services.TryAddSingleton<ProxyDialer>();
        services.TryAddSingleton<ProxyAuthorizer>();
        services.TryAddSingleton<ForwardProxyHandler>();
        return services;
    }

    /// <summary>
    /// Adds TLS interception: <c>CONNECT</c> tunnels selected by the registered
    /// <see cref="IForwardProxyInterceptionSelector"/> are decrypted with a certificate from the registered
    /// <see cref="IForwardProxyCertificateProvider"/>, and each request inside them runs through the application's
    /// request pipeline before <c>UseForwardProxy()</c> forwards it to the destination over TLS.
    /// </summary>
    /// <remarks>
    /// Requires Kestrel. Call after the host is configured to use Kestrel (as <c>WebApplication.CreateBuilder</c>
    /// does). Intercepted tunnels are served by an in-memory Kestrel endpoint; because that endpoint is configured in
    /// code, Kestrel ignores URLs from <c>ASPNETCORE_URLS</c> or <c>--urls</c>, so configure the proxy's own endpoints
    /// with <see cref="KestrelServerOptions"/> or the <c>Kestrel:Endpoints</c> configuration section.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="ForwardProxyTlsInterceptionOptions"/>.</param>
    public static IServiceCollection AddForwardProxyTlsInterception(this IServiceCollection services, Action<ForwardProxyTlsInterceptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<ForwardProxyTlsInterceptionOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        builder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ForwardProxyTlsInterceptionOptions>, TlsInterceptionOptionsValidator>());

        services.TryAddSingleton<InterceptionListenerFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConnectionListenerFactory, InterceptionListenerFactory>(static sp => sp.GetRequiredService<InterceptionListenerFactory>()));
        services.TryAddSingleton<TlsInterceptor>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IConfigureOptions<KestrelServerOptions>, InterceptionKestrelSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, InterceptionStartupFilter>());
        return services;
    }

    /// <summary>
    /// Adds TLS interception with the given selector and certificate provider, registered as singletons.
    /// </summary>
    /// <typeparam name="TSelector">Chooses which tunnels to intercept.</typeparam>
    /// <typeparam name="TCertificateProvider">Provides the certificate presented to the client for each destination.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="ForwardProxyTlsInterceptionOptions"/>.</param>
    /// <seealso cref="AddForwardProxyTlsInterception(IServiceCollection, Action{ForwardProxyTlsInterceptionOptions}?)"/>
    public static IServiceCollection AddForwardProxyTlsInterception<TSelector, TCertificateProvider>(this IServiceCollection services, Action<ForwardProxyTlsInterceptionOptions>? configure = null)
        where TSelector : class, IForwardProxyInterceptionSelector
        where TCertificateProvider : class, IForwardProxyCertificateProvider
    {
        services.AddForwardProxyTlsInterception(configure);
        services.TryAddSingleton<IForwardProxyInterceptionSelector, TSelector>();
        services.TryAddSingleton<IForwardProxyCertificateProvider, TCertificateProvider>();
        return services;
    }
}
