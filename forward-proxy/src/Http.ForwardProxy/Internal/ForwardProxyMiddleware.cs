// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>
/// Handles HTTP/2 CONNECT tunnels, absolute-form <c>http://</c> requests and requests received inside intercepted
/// tunnels in the request pipeline.
/// </summary>
internal sealed class ForwardProxyMiddleware(RequestDelegate next, ForwardProxyHandler handler, ILogger<ForwardProxyMiddleware> logger)
{
    private static readonly UriCreationOptions RawPathAndQuery = new() { DangerousDisablePathAndQueryCanonicalization = true };

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Features.Get<IHttp1ConnectionOptionsFeature>() is { IsAwaitingUpgradeOutcome: true } observer)
        {
            return InvokeUpgradeRequestAsync(context, observer);
        }

        return InvokeCoreAsync(context);
    }

    private async Task InvokeUpgradeRequestAsync(HttpContext context, IHttp1ConnectionOptionsFeature observer)
    {
        try
        {
            await InvokeCoreAsync(context).ConfigureAwait(false);
        }
        finally
        {
            // A refused upgrade leaves the connection in HTTP/1.x, so later request heads can be observed again.
            if (context.Response.StatusCode != StatusCodes.Status101SwitchingProtocols)
            {
                observer.ResumeAfterRejectedUpgrade();
            }
        }
    }

    private Task InvokeCoreAsync(HttpContext context)
    {
        var requestFeature = context.Features.Get<IHttpRequestFeature>()!;
        if (context.Features.Get<IForwardProxyInterceptionFeature>() is { } interception)
        {
            return HandleProxyRequestAsync(context, ForwardInterceptedAsync(context, interception.Destination, requestFeature.RawTarget));
        }

        if (HttpMethods.IsConnect(context.Request.Method))
        {
            if (context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true)
            {
                return next(context);
            }

            if (HttpProtocol.IsHttp2(context.Request.Protocol))
            {
                return HandleProxyRequestAsync(context, TunnelAsync(context, requestFeature.RawTarget));
            }

            if (HttpProtocol.IsHttp11(context.Request.Protocol) || HttpProtocol.IsHttp10(context.Request.Protocol))
            {
                Log.Http1ConnectInPipeline(logger, context.Connection.Id);
                context.Response.Headers.Connection = "close";
            }

            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            return Task.CompletedTask;
        }

        var rawTarget = requestFeature.RawTarget;
        if (rawTarget.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || rawTarget.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Forward the path and query exactly as received; canonicalization would change what the origin sees.
            if (!Uri.TryCreate(rawTarget, RawPathAndQuery, out var target))
            {
                handler.SetError(context, ProxyError.BadTarget("Invalid request target."));
                return Task.CompletedTask;
            }

            return HandleProxyRequestAsync(context, handler.ForwardHttpAsync(context, target));
        }

        return next(context);
    }

    /// <summary>Forwards a request received inside an intercepted tunnel to the tunnel's destination.</summary>
    private Task ForwardInterceptedAsync(HttpContext context, ForwardProxyDestination destination, string rawTarget)
    {
        // Only origin-form requests to the tunnel's destination: the client believes it is talking to the origin.
        if (HttpMethods.IsConnect(context.Request.Method) || !rawTarget.StartsWith('/'))
        {
            handler.RejectIntercepted(context, destination, ProxyError.BadTarget("Only origin-form requests are supported in an intercepted tunnel."));
            return Task.CompletedTask;
        }

        if (!IsDestinationHost(context.Request.Host, destination))
        {
            handler.RejectIntercepted(context, destination, new(StatusCodes.Status421MisdirectedRequest, ProxyErrorTypes.HttpRequestDenied, "Host does not match the tunnel's destination."));
            return Task.CompletedTask;
        }

        var host = destination.Host.Contains(':', StringComparison.Ordinal) ? $"[{destination.Host}]" : destination.Host;
        if (!Uri.TryCreate($"https://{host}:{destination.Port}{rawTarget}", RawPathAndQuery, out var target))
        {
            handler.RejectIntercepted(context, destination, ProxyError.BadTarget("Invalid request target."));
            return Task.CompletedTask;
        }

        return handler.ForwardInterceptedAsync(context, destination, target);
    }

    private static bool IsDestinationHost(HostString host, ForwardProxyDestination destination)
    {
        if (!host.HasValue)
        {
            return true;
        }

        return AuthorityParser.TryParse(host.Value, 443, out var name, out var port) && name == destination.Host && port == destination.Port;
    }

    /// <summary>
    /// Turns an unexpected failure while the proxy handles a request into a <c>proxy_internal_error</c> response, or an
    /// aborted connection once the response has started, as the HTTP/1.1 CONNECT path does.
    /// </summary>
    private async Task HandleProxyRequestAsync(HttpContext context, Task proxyTask)
    {
        try
        {
            await proxyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to report the failure to.
        }
        catch (Exception ex)
        {
            Log.ProxyRequestFailed(logger, ex, context.Request.Method, context.Connection.Id);
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            context.Response.Clear();
            handler.SetError(context, new ProxyError(StatusCodes.Status500InternalServerError, ProxyErrorTypes.ProxyInternalError, "Proxy error."));
        }
    }

    private const int Http2ConnectError = 0xa;

    private async Task TunnelAsync(HttpContext context, string target)
    {
        // A tunnel can legitimately be idle or carry unbounded data; the proxy's own idle timeout governs it.
        if (context.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } requestRate)
        {
            requestRate.MinDataRate = null;
        }

        if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } responseRate)
        {
            responseRate.MinDataRate = null;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } maxBodySize)
        {
            maxBodySize.MaxRequestBodySize = null;
        }

        using var tunnel = await handler.OpenTunnelAsync(context, target).ConfigureAwait(false);
        if (tunnel is null)
        {
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        // RFC 9113 section 8.5: over HTTP/2, a TCP error on the destination connection resets the stream with
        // CONNECT_ERROR, and an aborted stream closes the destination connection with RST.
        var isHttp2 = HttpProtocol.IsHttp2(context.Request.Protocol);
        var result = await tunnel.RunAsync(
            context.Request.BodyReader,
            context.Response.BodyWriter,
            context.RequestAborted,
            context,
            context.RequestAborted,
            context.Abort,
            resetUpstreamOnClientAbort: isHttp2).ConfigureAwait(false);
        if (isHttp2 && result.UpstreamFailed && !context.RequestAborted.IsCancellationRequested
            && context.Features.Get<IHttpResetFeature>() is { } reset)
        {
            reset.Reset(Http2ConnectError);
        }
    }
}
