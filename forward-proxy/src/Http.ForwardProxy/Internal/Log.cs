// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace DamianH.Http.ForwardProxy.Internal;

internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Proxy request {Kind} {Host}:{Port} rejected: {StatusCode} {ErrorType}. {Reason}")]
    public static partial void RequestRejected(ILogger logger, ForwardProxyRequestKind kind, string host, int port, int statusCode, string errorType, string reason);

    [LoggerMessage(2, LogLevel.Information, "Proxy request {Kind} {Host}:{Port} not authorized: {StatusCode}.")]
    public static partial void RequestNotAuthorized(ILogger logger, ForwardProxyRequestKind kind, string host, int port, int statusCode);

    [LoggerMessage(3, LogLevel.Debug, "Tunnel to {Host}:{Port} established.")]
    public static partial void TunnelEstablished(ILogger logger, string host, int port);

    [LoggerMessage(4, LogLevel.Information, "Tunnel to {Host}:{Port} closed after {DurationMs:F0} ms; {BytesUpstream} bytes up, {BytesDownstream} bytes down.")]
    public static partial void TunnelClosed(ILogger logger, string host, int port, double durationMs, long bytesUpstream, long bytesDownstream);

    [LoggerMessage(5, LogLevel.Debug, "Tunnel to {Host}:{Port} idle timeout elapsed.")]
    public static partial void TunnelIdleTimeout(ILogger logger, string host, int port);

    [LoggerMessage(6, LogLevel.Debug, "Rejected malformed CONNECT request from connection {ConnectionId}: {StatusCode}.")]
    public static partial void MalformedConnectRequest(ILogger logger, string connectionId, int statusCode);

    [LoggerMessage(7, LogLevel.Information, "Forwarding {Method} request to {Host}:{Port} failed: {Error}.")]
    public static partial void ForwardFailed(ILogger logger, Exception? exception, string method, string host, int port, string error);

    [LoggerMessage(8, LogLevel.Information, "Resolved addresses for {Host}:{Port} are all prohibited.")]
    public static partial void DestinationAddressProhibited(ILogger logger, string host, int port);

    [LoggerMessage(9, LogLevel.Warning, "HTTP/1.1 CONNECT reached the request pipeline on connection {ConnectionId}. Add ListenOptions.UseForwardProxy() to the Kestrel endpoint; CONNECT is only supported as the first request on a connection.")]
    public static partial void Http1ConnectInPipeline(ILogger logger, string connectionId);

    [LoggerMessage(10, LogLevel.Warning, "No proxy authorization policy is configured; the forward proxy accepts anonymous clients.")]
    public static partial void AnonymousProxy(ILogger logger);

    [LoggerMessage(11, LogLevel.Error, "Handling CONNECT request on connection {ConnectionId} failed.")]
    public static partial void ConnectFailed(ILogger logger, Exception exception, string connectionId);
}
