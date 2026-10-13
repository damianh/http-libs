// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>A proxy-generated error: status code plus RFC 9209 Proxy-Status error type.</summary>
internal readonly record struct ProxyError(int StatusCode, string ErrorType, string Reason)
{
    public static ProxyError BadTarget(string reason) => new(StatusCodes.Status400BadRequest, ProxyErrorTypes.HttpRequestError, reason);

    public static ProxyError Denied(string reason) => new(StatusCodes.Status403Forbidden, ProxyErrorTypes.HttpRequestDenied, reason);
}
