// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy;

/// <summary>The kind of proxy request.</summary>
public enum ForwardProxyRequestKind
{
    /// <summary>A <c>CONNECT</c> tunnel (HTTP/1.1 or HTTP/2).</summary>
    Connect,

    /// <summary>An absolute-form <c>http://</c> request forwarded by the proxy.</summary>
    Http,
}
