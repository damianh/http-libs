// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Authentication;

namespace DamianH.Http.ForwardProxy.Authentication;

/// <summary>Registers proxy Basic authentication.</summary>
public static class ProxyBasicExtensions
{
    /// <summary>Adds proxy Basic authentication using <see cref="ProxyBasicDefaults.AuthenticationScheme"/>.</summary>
    public static AuthenticationBuilder AddProxyBasic(this AuthenticationBuilder builder, Action<ProxyBasicAuthenticationOptions> configure) =>
        builder.AddProxyBasic(ProxyBasicDefaults.AuthenticationScheme, configure);

    /// <summary>Adds proxy Basic authentication using the specified scheme name.</summary>
    public static AuthenticationBuilder AddProxyBasic(this AuthenticationBuilder builder, string authenticationScheme, Action<ProxyBasicAuthenticationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddScheme<ProxyBasicAuthenticationOptions, ProxyBasicAuthenticationHandler>(authenticationScheme, configure);
    }
}
