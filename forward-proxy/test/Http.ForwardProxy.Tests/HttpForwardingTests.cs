// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
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

        (await RawProxy.ReadResponseAsync(socket)).StatusCode.ShouldBe(400);
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
    public async Task StripsHeadersNamedInConnection()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy);

        // Kestrel keeps only "close"/"keep-alive" when a request Connection header lists either, so list custom tokens only.
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
}
