// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>
/// Sets <see cref="Microsoft.AspNetCore.Http.HttpContext.User"/> to the tunnel's <c>CONNECT</c> principal at the
/// start of the pipeline, so all middleware sees who the intercepted client is.
/// </summary>
internal sealed class InterceptionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Features.Get<IForwardProxyInterceptionFeature>() is { } interception)
            {
                context.User = interception.User;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}
