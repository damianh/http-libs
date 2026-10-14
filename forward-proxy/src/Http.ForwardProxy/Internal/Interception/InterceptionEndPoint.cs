// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.Http.ForwardProxy.Internal.Interception;

/// <summary>The in-memory Kestrel endpoint that serves decrypted, intercepted tunnels.</summary>
internal sealed class InterceptionEndPoint : EndPoint
{
    public static readonly InterceptionEndPoint Instance = new();

    private InterceptionEndPoint()
    {
    }

    public override string ToString() => "forward-proxy-tls-interception";
}
