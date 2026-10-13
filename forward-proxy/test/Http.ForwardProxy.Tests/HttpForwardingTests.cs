// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
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
}
