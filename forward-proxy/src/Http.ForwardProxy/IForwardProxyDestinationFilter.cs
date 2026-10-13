// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// Custom destination rule, evaluated after authorization and the built-in host and port rules. Register
/// implementations in DI; every registered filter must allow a destination. IP address rules are enforced separately
/// at connect time.
/// </summary>
public interface IForwardProxyDestinationFilter
{
    /// <summary>Returns <see langword="true"/> to allow the destination.</summary>
    ValueTask<bool> IsAllowedAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken);
}
