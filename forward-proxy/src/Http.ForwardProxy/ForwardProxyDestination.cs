// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// The destination a client asked the proxy to reach. Also the resource passed to the proxy authorization policy.
/// </summary>
/// <param name="Host">Normalized host: lower-case DNS name without a trailing dot, or an IP literal without brackets.</param>
/// <param name="Port">Destination port.</param>
/// <param name="Kind">The kind of proxy request.</param>
public sealed record ForwardProxyDestination(string Host, int Port, ForwardProxyRequestKind Kind);
