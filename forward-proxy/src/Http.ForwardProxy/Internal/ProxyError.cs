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

/// <summary>RFC 9209 section 2.3 error types used by the proxy.</summary>
internal static class ProxyErrorTypes
{
    public const string DnsError = "dns_error";
    public const string DnsTimeout = "dns_timeout";
    public const string DestinationNotFound = "destination_not_found";
    public const string DestinationUnavailable = "destination_unavailable";
    public const string DestinationIpProhibited = "destination_ip_prohibited";
    public const string DestinationIpUnroutable = "destination_ip_unroutable";
    public const string ConnectionRefused = "connection_refused";
    public const string ConnectionTimeout = "connection_timeout";
    public const string ConnectionLimitReached = "connection_limit_reached";
    public const string HttpRequestError = "http_request_error";
    public const string HttpRequestDenied = "http_request_denied";
    public const string HttpResponseIncomplete = "http_response_incomplete";
    public const string ProxyInternalError = "proxy_internal_error";
}

internal sealed class ProxyErrorException(ProxyError error) : Exception(error.Reason)
{
    public ProxyError Error { get; } = error;
}
