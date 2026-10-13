// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Handles HTTP/2 CONNECT tunnels and absolute-form <c>http://</c> requests in the request pipeline.</summary>
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
        if (HttpMethods.IsConnect(context.Request.Method))
        {
            if (context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true)
            {
                return next(context);
            }

            if (HttpProtocol.IsHttp2(context.Request.Protocol))
            {
                return TunnelAsync(context, requestFeature.RawTarget);
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

            return handler.ForwardHttpAsync(context, target);
        }

        return next(context);
    }

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
        await tunnel.RunAsync(context.Request.BodyReader, context.Response.BodyWriter, context.RequestAborted).ConfigureAwait(false);
    }
}
