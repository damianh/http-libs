// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.ForwardProxy.Internal;

internal sealed class ProxyErrorException(ProxyError error) : Exception(error.Reason)
{
    public ProxyError Error { get; } = error;
}
