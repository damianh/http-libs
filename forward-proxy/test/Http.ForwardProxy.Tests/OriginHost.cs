// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DamianH.Http.ForwardProxy;

/// <summary>An origin server that echoes request details.</summary>
internal sealed class OriginHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private OriginHost(WebApplication app, int port, bool tls)
    {
        _app = app;
        Port = port;
        Uri = new Uri($"{(tls ? "https" : "http")}://127.0.0.1:{port}");
    }

    public int Port { get; }

    public Uri Uri { get; }

    public static async Task<OriginHost> StartAsync(bool tls = false, RequestDelegate? handler = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (tls)
            {
                listen.UseHttps(TestCertificate.Instance);
            }
        }));
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(handler ?? (async context =>
        {
            var request = context.Request;
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[4096];
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
                        return;
                    }

                    await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
                }
            }

            if (request.Query.ContainsKey("hop"))
            {
                context.Response.Headers.Connection = "X-Resp-Hop";
                context.Response.Headers["X-Resp-Hop"] = "1";
                context.Response.Headers["X-Resp-End"] = "1";
            }

            var text = new StringBuilder()
                .Append("method=").Append(request.Method).Append('\n')
                .Append("path=").Append(request.Path).Append(request.QueryString).Append('\n')
                .Append("host=").Append(request.Host).Append('\n')
                .Append("via=").Append(request.Headers.Via.ToString()).Append('\n')
                .Append("authorization=").Append(request.Headers.Authorization.ToString()).Append('\n')
                .Append("proxy-authorization=").Append(request.Headers.ProxyAuthorization.ToString()).Append('\n')
                .Append("proxy-connection=").Append(request.Headers["Proxy-Connection"].ToString()).Append('\n');
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)))
            {
                text.Append("header:").Append(header.Key.ToLowerInvariant()).Append('=').Append(header.Value.ToString()).Append('\n');
            }

            if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
            {
                using var reader = new StreamReader(request.Body);
                text.Append("body=").Append(await reader.ReadToEndAsync(context.RequestAborted)).Append('\n');
            }

            await context.Response.WriteAsync(text.ToString(), context.RequestAborted);
        }));
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new OriginHost(app, ProxyHost.GetPort(app), tls);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
