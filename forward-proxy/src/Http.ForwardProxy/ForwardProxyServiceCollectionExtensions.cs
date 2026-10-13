// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
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
}
