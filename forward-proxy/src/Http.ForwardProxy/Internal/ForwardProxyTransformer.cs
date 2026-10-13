// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Yarp.ReverseProxy.Forwarder;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>Rewrites an absolute-form proxy request into an origin request.</summary>
internal sealed class ForwardProxyTransformer(Uri target, ForwardProxyOptions options) : HttpTransformer
{
    private const string ProxyConnection = "Proxy-Connection";

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
        proxyRequest.RequestUri = target;
        proxyRequest.Headers.Remove(HeaderNames.ProxyAuthorization);
        proxyRequest.Headers.Remove(ProxyConnection);
        if (options.AddViaHeader)
        {
            proxyRequest.Headers.TryAddWithoutValidation(HeaderNames.Via, Via(httpContext.Request.Protocol));
        }
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
    {
        var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken).ConfigureAwait(false);
        if (proxyResponse is not null && options.AddViaHeader)
        {
            httpContext.Response.Headers.Append(HeaderNames.Via, Via($"HTTP/{proxyResponse.Version}"));
        }

        return result;
    }

    private string Via(string protocol)
    {
        var version = protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ? protocol[5..] : protocol;
        if (version == "2.0")
        {
            version = "2";
        }
        else if (version == "3.0")
        {
            version = "3";
        }

        return $"{version} {options.ProxyName}";
    }
}
