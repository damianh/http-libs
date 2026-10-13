// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.Http.ForwardProxy;

/// <summary>An <see cref="IWebProxy"/> that never bypasses, unlike <see cref="WebProxy"/> which skips loopback.</summary>
internal sealed class AlwaysProxy(Uri proxy, ICredentials? credentials = null) : IWebProxy
{
    public ICredentials? Credentials { get; set; } = credentials;

    public Uri GetProxy(Uri destination) => proxy;

    public bool IsBypassed(Uri host) => false;
}
