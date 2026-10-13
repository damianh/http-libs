// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Claims;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

internal sealed class InterceptionFeature(ForwardProxyDestination destination, ClaimsPrincipal user) : IForwardProxyInterceptionFeature
{
    public ForwardProxyDestination Destination => destination;

    public ClaimsPrincipal User => user;
}
