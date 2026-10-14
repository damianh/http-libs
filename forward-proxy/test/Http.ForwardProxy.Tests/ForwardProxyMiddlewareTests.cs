// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class ForwardProxyMiddlewareTests
{
    // Kestrel needs libmsquic for HTTP/3, so the middleware is exercised directly. A classic CONNECT never reaches the
    // handler on this path.
    [Fact]
    public async Task RejectsHttp3ConnectAsNotImplemented()
    {
        var nextCalled = false;
        var middleware = new ForwardProxyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            handler: null!,
            NullLogger<ForwardProxyMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Connect;
        context.Request.Protocol = HttpProtocol.Http3;

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status501NotImplemented);
        context.Response.Headers.ContainsKey("Connection").ShouldBeFalse();
        nextCalled.ShouldBeFalse();
    }
}
