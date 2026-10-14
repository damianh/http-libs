// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class HttpForwardingTests
{
    [Fact]
    public async Task ForwardsAbsoluteFormRequests()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(o => o.ProxyName = "egress");
        using var client = ConnectTunnelTests.CreateClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Uri, "/submit?q=1"))
        {
            Content = new StringContent("payload"),
        };
        request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic c2VjcmV0");
        request.Headers.TryAddWithoutValidation("Proxy-Connection", "keep-alive");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        body.ShouldContain("method=POST\n");
        body.ShouldContain("path=/submit?q=1\n");
        body.ShouldContain($"host=127.0.0.1:{origin.Port}\n");
        body.ShouldContain("via=1.1 egress\n");
        body.ShouldContain("proxy-authorization=\n");
        body.ShouldContain("proxy-connection=\n");
        body.ShouldContain("body=payload\n");
        response.Headers.Via.ToString().ShouldBe("1.1 egress");
    }

    [Fact]
    public async Task DoesNotRouteAbsoluteFormRequestsToTheProxyApplication()
    {
        await using var proxy = await ProxyHost.StartAsync(o => o.AllowedNetworks.Clear());
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "GET http://localhost/health HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(403);
        response.Header("Proxy-Status").ShouldBe("forward-proxy; error=destination_ip_prohibited");
    }

    [Fact]
    public async Task RejectsDisallowedHttpPorts()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(o => o.AllowedHttpPorts = new HashSet<int> { 80 });
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using var response = await client.GetAsync(origin.Uri, TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(403);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=http_request_denied"]);
    }

    [Fact]
    public async Task RejectsAbsoluteFormHttpsTargets()
    {
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "GET https://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(400);
        response.Header("Proxy-Status").ShouldBe("forward-proxy; error=http_request_error");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public async Task AppendsToReceivedViaHeaders(string version)
    {
        await using var origin = await StartOriginAsync(context =>
        {
            context.Response.Headers.Via = "1.1 origin-gateway";
            return context.Response.WriteAsync($"via=[{context.Request.Headers.Via}]\n", context.RequestAborted);
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{port}/ HTTP/{version}\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nVia: 1.1 client-gateway\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain($"via=[1.1 client-gateway, {version} forward-proxy]\n");
        response.Header("Via").ShouldBe("1.1 origin-gateway, 1.1 forward-proxy");
    }

    [Fact]
    public async Task DoesNotForwardHopByHopRequestHeaders()
    {
        await using var origin = await StartOriginAsync(context =>
        {
            var headers = context.Request.Headers;
            return context.Response.WriteAsync(
                $"keep-alive=[{headers.KeepAlive}]\nte=[{headers.TE}]\nproxy-connection=[{headers["Proxy-Connection"]}]\nhttp2-settings=[{headers["HTTP2-Settings"]}]\n",
                context.RequestAborted);
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nKeep-Alive: timeout=5\r\nTE: trailers\r\nProxy-Connection: keep-alive\r\nHTTP2-Settings: AAMAAABkAARAAAAAAAIAAAAA\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain("keep-alive=[]\nte=[]\nproxy-connection=[]\nhttp2-settings=[]\n");
    }

    [Fact]
    public async Task DoesNotForwardContentLengthWithTransferEncoding()
    {
        await using var origin = await StartOriginAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var requestBody = await reader.ReadToEndAsync(context.RequestAborted);
            await context.Response.WriteAsync($"content-length=[{context.Request.Headers.ContentLength}]\nbody={requestBody}\n", context.RequestAborted);
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"POST http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nContent-Length: 100\r\nTransfer-Encoding: chunked\r\n\r\n3\r\npay\r\n0\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain("content-length=[]\n");
        body.ShouldContain("body=pay\n");
    }

    [Fact]
    public async Task ReportsUnreachableOrigins()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var proxy = await ProxyHost.StartAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using var response = await client.GetAsync($"http://127.0.0.1:{closedPort}/", TestContext.Current.CancellationToken);

        ((int)response.StatusCode).ShouldBe(502);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=connection_refused"]);
    }

    [Fact]
    public async Task ReportsInvalidOriginResponsesAsIncomplete()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var proxy = await ProxyHost.StartAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy);
        var responseTask = client.GetAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
        using (var originSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken))
        {
            await ReadHeadAsync(originSocket);
            await RawProxy.SendAsync(originSocket, "NOT-HTTP\r\n\r\n");
        }

        using var response = await responseTask;

        ((int)response.StatusCode).ShouldBe(502);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=http_response_incomplete"]);
    }

    [Fact]
    public async Task ReportsOriginsThatDoNotRespondAsTimedOut()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var proxy = await ProxyHost.StartAsync(o => o.HttpActivityTimeout = TimeSpan.FromMilliseconds(200));
        using var client = ConnectTunnelTests.CreateClient(proxy);
        var responseTask = client.GetAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
        using var originSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        await ReadHeadAsync(originSocket);

        using var response = await responseTask;

        ((int)response.StatusCode).ShouldBe(504);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=connection_timeout"]);
    }

    [Fact]
    public async Task StripsHeadersNamedInConnection()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin.Uri, "/x?hop=1"));
        request.Headers.Connection.Add("X-Hop");
        request.Headers.Add("X-Hop", "secret");
        request.Headers.Add("X-End", "kept");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldContain("header:x-end=kept\n");
        body.ShouldNotContain("header:x-hop", Case.Sensitive);
        response.Headers.GetValues("X-Resp-End").ShouldBe(["1"]);
        response.Headers.Contains("X-Resp-Hop").ShouldBeFalse();
    }

    [Fact]
    public async Task ForwardsChunkedRequestBodies()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"POST http://127.0.0.1:{origin.Port}/upload HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\nTransfer-Encoding: chunked\r\n\r\n3\r\npay\r\n4\r\nload\r\n0\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain("body=payload\n");
    }

    [Fact]
    public async Task DoesNotAdvertiseRequestTrailersItDrops()
    {
        await using var origin = await StartOriginAsync(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var requestBody = await reader.ReadToEndAsync(context.RequestAborted);
            await context.Response.WriteAsync($"trailer=[{context.Request.Headers.Trailer}]\nbody={requestBody}\n", context.RequestAborted);
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"POST http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nTransfer-Encoding: chunked\r\nTrailer: X-T\r\n\r\n3\r\npay\r\n0\r\nX-T: 1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain("trailer=[]\n");
        body.ShouldContain("body=pay\n");
    }

    [Fact]
    public async Task DoesNotAdvertiseResponseTrailersItDrops()
    {
        await using var origin = await StartOriginAsync(async context =>
        {
            context.Response.DeclareTrailer("X-T");
            await context.Response.WriteAsync("payload", context.RequestAborted);
            if (context.Response.SupportsTrailers())
            {
                context.Response.AppendTrailer("X-T", "1");
            }
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nTE: trailers\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        response.Headers.Contains("Trailer").ShouldBeFalse();
        body.ShouldContain("payload");
        body.ShouldNotContain("X-T");
    }

    [Fact]
    public async Task ForwardsWebSocketUpgrades()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var ct = TestContext.Current.CancellationToken;

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{origin.Port}/ws HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(101);
        response.Header("Upgrade").ShouldBe("websocket");
        response.Header("Sec-WebSocket-Accept").ShouldBe("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
        response.Header("Via").ShouldBe("1.1 forward-proxy");
        using var client = WebSocket.CreateFromStream(new NetworkStream(socket), new WebSocketCreationOptions { IsServer = false });
        await client.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, endOfMessage: true, ct);
        var buffer = new byte[64];
        var received = await client.ReceiveAsync(buffer, ct);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("hello");
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task AbsoluteTargetsOverrideTheReceivedHost()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{origin.Port}/ HTTP/1.1\r\nHost: different.example\r\nConnection: close\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain($"host=127.0.0.1:{origin.Port}\n");
    }


    [Fact]
    public async Task PreservesTheOriginalPathAndQueryOnTheWire()
    {
        await using var origin = await StartOriginAsync(context =>
            context.Response.WriteAsync($"raw={context.Features.Get<IHttpRequestFeature>()!.RawTarget}\n"));
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);
        const string path = "/a/%2e%2e/b?x=%7e";

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{port}{path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain($"raw={path}\n");
    }


    [Fact]
    public async Task StripsConnectionOptionsWhenCloseIsAlsoPresent()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{origin.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close, X-Hop\r\nX-Hop: not-for-origin\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldNotContain("header:x-hop", Case.Sensitive);
    }


    [Fact]
    public async Task StripsMixedConnectionOptionsAcrossKeepAliveRequests()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var target = $"http://127.0.0.1:{origin.Port}";

        // Pipelined: a body that looks like a head, a chunked body, then a request that closes the connection.
        await RawProxy.SendAsync(
            socket,
            $"POST {target}/one HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: X-One, keep-alive\r\nX-One: 1\r\nX-Kept: 1\r\nContent-Length: 30\r\n\r\nGET / HTTP/1.1\r\nX-Two: 2\r\n\r\n\r\n"
            + $"POST {target}/two HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\nConnection: X-Two\r\nconnection: keep-alive\r\nX-Two: 2\r\n\r\n3\r\nabc\r\n0\r\n\r\n"
            + $"GET {target}/three HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close, X-Three\r\nX-Three: 3\r\n\r\n");
        var responses = await RawProxy.ReadToEndAsync(socket);

        responses.Split("HTTP/1.1 200 OK").Length.ShouldBe(4, responses);
        responses.ShouldContain("path=/three\n");
        responses.ShouldContain("header:x-kept=1\n");
        responses.ShouldNotContain("header:x-one", Case.Sensitive);
        responses.ShouldNotContain("header:x-two", Case.Sensitive);
        responses.ShouldNotContain("header:x-three", Case.Sensitive);
    }

    [Fact]
    public async Task StripsMixedConnectionOptionsAfterARejectedUpgrade()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var target = $"http://127.0.0.1:{origin.Port}";

        // Without a Sec-WebSocket-Key the origin answers 200, so the proxy's connection stays in HTTP/1.1.
        await RawProxy.SendAsync(socket, $"GET {target}/ws HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: Upgrade, X-One\r\nUpgrade: websocket\r\nX-One: 1\r\n\r\n");
        var first = await RawProxy.ReadResponseAsync(socket);
        first.StatusCode.ShouldBe(200);
        await RawProxy.SendAsync(socket, $"GET {target}/two HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close, X-Two\r\nX-Two: 2\r\n\r\n");
        var responses = await RawProxy.ReadToEndAsync(socket, first.Body);

        responses.ShouldContain("path=/two\n");
        responses.ShouldNotContain("header:x-one", Case.Sensitive);
        responses.ShouldNotContain("header:x-two", Case.Sensitive);
    }

    [Fact]
    public async Task WebSocketUpgradesRespectTheTunnelLimit()
    {
        using var echo = new EchoServer();
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(options => options.MaxConcurrentTunnels = 1);
        using var tunnel = await RawProxy.ConnectAsync(proxy.Port);
        await RawProxy.SendAsync(tunnel, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
        (await RawProxy.ReadResponseAsync(tunnel)).StatusCode.ShouldBe(200);
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{origin.Port}/ws HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        if (response.StatusCode == 101)
        {
            using var websocket = WebSocket.CreateFromStream(new NetworkStream(socket), new WebSocketCreationOptions { IsServer = false });
            await websocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", TestContext.Current.CancellationToken);
        }

        response.StatusCode.ShouldBe(503);
    }


    [Fact]
    public async Task DisposingTheProxyClosesItsPooledOriginConnections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var proxy = await ProxyHost.StartAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy);
        var responseTask = client.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
        using var originSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        await ReadHeadAsync(originSocket);
        await RawProxy.SendAsync(originSocket, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
        (await responseTask).ShouldBe("ok");
        client.Dispose();

        await proxy.DisposeAsync();

        var closed = originSocket.Poll(TimeSpan.FromMilliseconds(300), SelectMode.SelectRead);
        GC.KeepAlive(proxy);
        closed.ShouldBeTrue("The origin-side connection should be closed when the proxy host is disposed.");
    }


    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task DecrementsMaxForwardsOnForwardedTraceAndOptions(string method)
    {
        await using var origin = await StartOriginAsync(context =>
            context.Response.WriteAsync($"max-forwards=[{context.Request.Headers.MaxForwards}]\n", context.RequestAborted));
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"{method} http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nMax-Forwards: 5\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain("max-forwards=[4]\n");
    }

    [Fact]
    public async Task AnswersOptionsWithZeroMaxForwardsWithoutContactingTheOrigin()
    {
        var originRequests = 0;
        await using var origin = await StartOriginAsync(_ =>
        {
            Interlocked.Increment(ref originRequests);
            return Task.CompletedTask;
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"OPTIONS http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nMax-Forwards: 0\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(200);
        response.Header("Content-Length").ShouldBe("0");
        originRequests.ShouldBe(0);
    }

    [Fact]
    public async Task RefusesTraceWithZeroMaxForwardsWithoutEchoingOrContactingTheOrigin()
    {
        var originRequests = 0;
        await using var origin = await StartOriginAsync(_ =>
        {
            Interlocked.Increment(ref originRequests);
            return Task.CompletedTask;
        });
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"TRACE http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nMax-Forwards: 0\r\nCookie: secret=1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(405);
        response.Header("Content-Length").ShouldBe("0");
        body.ShouldBeEmpty();
        originRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData("OPTIONS", "abc")]
    [InlineData("TRACE", "-1")]
    [InlineData("OPTIONS", "1, 2")]
    public async Task ForwardsInvalidMaxForwardsUnchanged(string method, string maxForwards)
    {
        await using var origin = await StartOriginAsync(context =>
            context.Response.WriteAsync($"max-forwards=[{context.Request.Headers.MaxForwards}]\n", context.RequestAborted));
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"{method} http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nMax-Forwards: {maxForwards}\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain($"max-forwards=[{maxForwards}]\n");
    }

    [Fact]
    public async Task DoesNotApplyMaxForwardsToOtherMethods()
    {
        await using var origin = await StartOriginAsync(context =>
            context.Response.WriteAsync($"max-forwards=[{context.Request.Headers.MaxForwards}]\n", context.RequestAborted));
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var port = ProxyHost.GetPort(origin);

        await RawProxy.SendAsync(socket, $"GET http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nMax-Forwards: 0\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);
        var body = await RawProxy.ReadToEndAsync(socket, response.Body);

        response.StatusCode.ShouldBe(200);
        body.ShouldContain("max-forwards=[0]\n");
    }

    private static async Task<WebApplication> StartOriginAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task ReadHeadAsync(Socket socket)
    {
        var bytes = new byte[4096];
        var head = new StringBuilder();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await socket.ReceiveAsync(bytes, cancellation.Token);
            if (read == 0)
            {
                throw new IOException("Connection closed before the request head.");
            }

            head.Append(Encoding.ASCII.GetString(bytes, 0, read));
        }
    }
}
