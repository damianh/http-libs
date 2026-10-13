// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>
/// Kestrel connection middleware that handles HTTP/1.x CONNECT as the first request on a connection. Kestrel has no
/// API for turning an HTTP/1.1 request into a raw tunnel, so the request head is parsed here before Kestrel's HTTP
/// layer sees it. Any other traffic is passed to Kestrel untouched.
/// </summary>
internal sealed class ForwardProxyConnectionMiddleware(ConnectionDelegate next, IServiceProvider services, KestrelServerOptions serverOptions)
{
    private const string ConnectMethod = "CONNECT";
    private static readonly byte[] TunnelEstablished = "HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray();

    private ForwardProxyHandler? _handler;
    private ILogger? _logger;

    public async Task OnConnectionAsync(ConnectionContext connection)
    {
        if (IsHttp2Alpn(connection))
        {
            await next(connection).ConfigureAwait(false);
            return;
        }

        _handler ??= services.GetService<ForwardProxyHandler>()
            ?? throw new InvalidOperationException("Forward proxy services are not registered. Call services.AddForwardProxy().");
        _logger ??= services.GetRequiredService<ILogger<ForwardProxyConnectionMiddleware>>();
        var handler = _handler;
        var input = connection.Transport.Input;

        ConnectRequestParser.ConnectRequest? request;
        bool passThrough;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed))
        {
            timeout.CancelAfter(handler.Options.ConnectRequestHeadersTimeout);
            try
            {
                (passThrough, request) = await ReadConnectRequestAsync(connection, input, handler.Options.MaxConnectRequestHeadersSize, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (passThrough)
        {
            ObserveRequestHeads(connection);
            await next(connection).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            return;
        }

        await HandleConnectAsync(connection, handler, request).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the CONNECT request head. <c>PassThrough</c> is <see langword="true"/> when the connection does not start
    /// with CONNECT, in which case nothing is consumed from <paramref name="input"/>. A <see langword="null"/> request
    /// without pass-through means the connection should be closed (a malformed request has been answered).
    /// </summary>
    private async Task<(bool PassThrough, ConnectRequestParser.ConnectRequest? Request)> ReadConnectRequestAsync(
        ConnectionContext connection,
        PipeReader input,
        int maxHeadSize,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;
            var detection = ConnectRequestParser.Detect(buffer);
            if (detection == ConnectRequestParser.Detection.NotConnect
                || (detection == ConnectRequestParser.Detection.NeedMoreData && result.IsCompleted))
            {
                input.AdvanceTo(buffer.Start);
                return (true, null);
            }

            if (detection == ConnectRequestParser.Detection.Connect)
            {
                var status = ConnectRequestParser.TryParse(buffer, maxHeadSize, out var request, out var consumed, out var errorStatusCode);
                if (status == ConnectRequestParser.ParseStatus.Complete)
                {
                    input.AdvanceTo(consumed);
                    return (false, request);
                }

                if (status == ConnectRequestParser.ParseStatus.Invalid)
                {
                    input.AdvanceTo(buffer.End);
                    Log.MalformedConnectRequest(_logger!, connection.ConnectionId, errorStatusCode);
                    await WriteResponseAsync(connection.Transport.Output, errorStatusCode, new HeaderDictionary(), ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
                    return (false, null);
                }
            }

            if (result.IsCompleted)
            {
                input.AdvanceTo(buffer.End);
                return (false, null);
            }

            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private async Task HandleConnectAsync(ConnectionContext connection, ForwardProxyHandler handler, ConnectRequestParser.ConnectRequest request)
    {
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        await using var scope = scopeFactory.CreateAsyncScope();
        using var responseBody = new MemoryStream();
        var context = CreateContext(connection, request, scope.ServiceProvider, responseBody);

        ForwardProxyHandler.Tunnel? tunnel;
        try
        {
            tunnel = await handler.OpenTunnelAsync(context, request.Target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ConnectFailed(_logger!, ex, connection.ConnectionId);
            context.Response.Headers.Clear();
            handler.SetError(context, new ProxyError(StatusCodes.Status500InternalServerError, ProxyErrorTypes.ProxyInternalError, "Proxy error."));
            responseBody.SetLength(0);
            tunnel = null;
        }

        var output = connection.Transport.Output;
        if (tunnel is null)
        {
            await WriteResponseAsync(output, context.Response.StatusCode, context.Response.Headers, responseBody.GetBuffer().AsMemory(0, (int)responseBody.Length)).ConfigureAwait(false);
            return;
        }

        using (tunnel)
        {
            // Kestrel signals ConnectionClosed when the client half-closes, so it can't bound the tunnel; the tunnel
            // ends when either side closes, a write fails, or the idle timeout elapses.
            await output.WriteAsync(TunnelEstablished).ConfigureAwait(false);
            await tunnel.RunAsync(connection.Transport.Input, output, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kestrel rewrites a <c>Connection</c> header that includes <c>close</c>, <c>keep-alive</c> or <c>upgrade</c>,
    /// dropping the other options it lists. Observe the raw request heads so those options' headers can still be
    /// removed before forwarding.
    /// </summary>
    private void ObserveRequestHeads(ConnectionContext connection)
    {
        var limits = serverOptions.Limits;
        var observer = new Http1RequestHeadObserver(connection.Transport.Input, Math.Max(limits.MaxRequestLineSize, limits.MaxRequestHeadersTotalSize));
        connection.Transport = new ObservedDuplexPipe(observer, connection.Transport.Output);
        connection.Features.Set<IHttp1ConnectionOptionsFeature>(observer);
    }

    private sealed class ObservedDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input => input;

        public PipeWriter Output => output;
    }

    private static DefaultHttpContext CreateContext(ConnectionContext connection, ConnectRequestParser.ConnectRequest request, IServiceProvider requestServices, Stream responseBody)
    {
        var headers = new HeaderDictionary();
        foreach (var (name, value) in request.Headers)
        {
            headers.Append(name, value);
        }

        var tls = connection.Features.Get<ITlsConnectionFeature>();
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Method = ConnectMethod,
            Protocol = request.Protocol,
            Scheme = tls is null ? "http" : "https",
            RawTarget = request.Target,
            Path = "",
            Headers = headers,
        });
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            ConnectionId = connection.ConnectionId,
            LocalIpAddress = (connection.LocalEndPoint as IPEndPoint)?.Address,
            LocalPort = (connection.LocalEndPoint as IPEndPoint)?.Port ?? 0,
            RemoteIpAddress = (connection.RemoteEndPoint as IPEndPoint)?.Address,
            RemotePort = (connection.RemoteEndPoint as IPEndPoint)?.Port ?? 0,
        });
        // Not ConnectionClosed: Kestrel fires that on a client half-close, which is legitimate after CONNECT. The
        // handshake is bounded by ConnectTimeout instead.
        features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature());
        features.Set<IHttpRequestIdentifierFeature>(new HttpRequestIdentifierFeature { TraceIdentifier = connection.ConnectionId });
        if (tls is not null)
        {
            features.Set(tls);
        }

        return new DefaultHttpContext(features) { RequestServices = requestServices };
    }

    private static async Task WriteResponseAsync(PipeWriter output, int statusCode, IHeaderDictionary headers, ReadOnlyMemory<byte> body)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(ReasonPhrases.GetReasonPhrase(statusCode)).Append("\r\n");
        foreach (var (name, values) in headers)
        {
            if (IsFramingHeader(name))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (value is null || value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                {
                    continue;
                }

                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        builder.Append("Content-Length: ").Append(body.Length).Append("\r\nConnection: close\r\n\r\n");
        output.Write(Encoding.Latin1.GetBytes(builder.ToString()));
        if (!body.IsEmpty)
        {
            output.Write(body.Span);
        }

        await output.FlushAsync().ConfigureAwait(false);
    }

    private static bool IsFramingHeader(string name) =>
        string.Equals(name, HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.Connection, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp2Alpn(ConnectionContext connection) =>
        connection.Features.Get<ITlsApplicationProtocolFeature>()?.ApplicationProtocol.Span.SequenceEqual(SslApplicationProtocol.Http2.Protocol.Span) == true;
}
