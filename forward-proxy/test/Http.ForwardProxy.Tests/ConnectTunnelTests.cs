// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;
using System.IO.Pipelines;
using System.Net.Sockets;
using DamianH.Http.ForwardProxy.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class ConnectTunnelTests
{
    [Fact]
    public async Task TunnelsBytesIncludingEarlyDataAndPropagatesHalfClose()
    {
        using var echo = new EchoServer();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\n\r\nearly");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusLine.ShouldBe("HTTP/1.1 200 Connection established");
        var echoed = await RawProxy.ReadExactlyAsync(socket, 5, response.Body);
        echoed.ShouldBe("early");

        await RawProxy.SendAsync(socket, "ping");
        (await RawProxy.ReadExactlyAsync(socket, 4)).ShouldBe("ping");

        socket.Shutdown(SocketShutdown.Send);
        (await RawProxy.ReadToEndAsync(socket)).ShouldBe("BYE");
    }

    [Fact]
    public async Task HttpClientTunnelsHttpsThroughConnect()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync();
        using var client = CreateClient(proxy);

        var body = await client.GetStringAsync(new Uri(origin.Uri, "/secure?x=1"), TestContext.Current.CancellationToken);

        body.ShouldContain("path=/secure?x=1");
        body.ShouldContain("via=\n");
    }

    [Fact]
    public async Task BlocksLoopbackByDefault()
    {
        await using var proxy = await ProxyHost.StartAsync(o =>
        {
            o.AllowedNetworks.Clear();
            o.ProxyName = "egress";
        });
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT localhost:443 HTTP/1.1\r\nHost: localhost:443\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(403);
        response.Header("Proxy-Status").ShouldBe("egress; error=destination_ip_prohibited");
        response.Header("Connection").ShouldBe("close");
        (await RawProxy.ReadToEndAsync(socket)).ShouldBeEmpty();
    }

    [Fact]
    public async Task BlocksDisallowedPorts()
    {
        await using var proxy = await ProxyHost.StartAsync(o => o.AllowedConnectPorts = new HashSet<int> { 443 });
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT 127.0.0.1:22 HTTP/1.1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(403);
        response.Header("Proxy-Status").ShouldBe("forward-proxy; error=http_request_denied");
    }

    [Fact]
    public async Task ReportsRefusedConnections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{closedPort} HTTP/1.1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(502);
        response.Header("Proxy-Status").ShouldBe("forward-proxy; error=connection_refused");
    }

    [Theory]
    [InlineData("CONNECT example.com HTTP/1.1\r\n\r\n", 400)]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nContent-Length: 4\r\n\r\nbody", 400)]
    [InlineData("CONNECT example.com:443 HTTP/1.1\r\nHost: x\r\n folded\r\n\r\n", 400)]
    [InlineData("CONNECT http://example.com/ HTTP/1.1\r\n\r\n", 400)]
    public async Task RejectsMalformedRequests(string request, int expectedStatus)
    {
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, request);
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(expectedStatus);
        (await RawProxy.ReadToEndAsync(socket)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RejectsOversizedRequestHeads()
    {
        await using var proxy = await ProxyHost.StartAsync(o => o.MaxConnectRequestHeadersSize = 1024);
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT example.com:443 HTTP/1.1\r\nX-Big: " + new string('a', 2048));
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(431);
    }

    [Fact]
    public async Task ClosesConnectionsThatNeverCompleteTheHead()
    {
        await using var proxy = await ProxyHost.StartAsync(o => o.ConnectRequestHeadersTimeout = TimeSpan.FromMilliseconds(200));
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT example.com:443 HTTP/1.1\r\n");

        (await RawProxy.ReadToEndAsync(socket)).ShouldBeEmpty();
    }

    [Fact]
    public async Task OtherRequestsPassThroughToTheApplication()
    {
        await using var proxy = await ProxyHost.StartAsync();
        using var client = new HttpClient { BaseAddress = proxy.Uri };

        (await client.GetStringAsync("/health", TestContext.Current.CancellationToken)).ShouldBe("proxy-health");
        (await client.GetStringAsync("/health", TestContext.Current.CancellationToken)).ShouldBe("proxy-health");
    }

    [Fact]
    public async Task ConnectAfterAnotherRequestIsNotImplemented()
    {
        await using var proxy = await ProxyHost.StartAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n");
        var first = await RawProxy.ReadResponseAsync(socket);
        first.StatusCode.ShouldBe(200);
        await RawProxy.ReadExactlyAsync(socket, "proxy-health".Length, first.Body);

        await RawProxy.SendAsync(socket, "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
        var second = await RawProxy.ReadResponseAsync(socket);

        second.StatusCode.ShouldBe(501);
    }

    [Fact]
    public async Task ClosesIdleTunnels()
    {
        using var echo = new EchoServer();
        await using var proxy = await ProxyHost.StartAsync(o => o.TunnelIdleTimeout = TimeSpan.FromMilliseconds(200));
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
        (await RawProxy.ReadResponseAsync(socket)).StatusCode.ShouldBe(200);

        (await RawProxy.ReadToEndAsync(socket)).ShouldBeEmpty();
    }

    [Fact]
    public async Task LimitsConcurrentTunnels()
    {
        using var echo = new EchoServer();
        await using var proxy = await ProxyHost.StartAsync(o => o.MaxConcurrentTunnels = 1);
        using var first = await RawProxy.ConnectAsync(proxy.Port);
        await RawProxy.SendAsync(first, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
        (await RawProxy.ReadResponseAsync(first)).StatusCode.ShouldBe(200);

        using var second = await RawProxy.ConnectAsync(proxy.Port);
        await RawProxy.SendAsync(second, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
        var rejected = await RawProxy.ReadResponseAsync(second);
        rejected.StatusCode.ShouldBe(503);
        rejected.Header("Proxy-Status").ShouldBe("forward-proxy; error=connection_limit_reached");

        first.Shutdown(SocketShutdown.Send);
        (await RawProxy.ReadToEndAsync(first)).ShouldBe("BYE");
        using var third = await RawProxy.ConnectAsync(proxy.Port);
        await RawProxy.SendAsync(third, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
        (await RawProxy.ReadResponseAsync(third)).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task CustomFiltersCanDenyDestinations()
    {
        using var echo = new EchoServer();
        await using var proxy = await ProxyHost.StartAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyDestinationFilter>(new DelegateFilter((_, d) => d.Port != echo.Port)));
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(403);
        response.Header("Proxy-Status").ShouldBe("forward-proxy; error=http_request_denied");
    }

    [Fact]
    public async Task IdleTunnelDoesNotReserveAResponseBufferBeforeDataArrives()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await upstream.ConnectAsync(listener.LocalEndpoint, TestContext.Current.CancellationToken);
        using var origin = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var input = new Pipe();
        var output = new Pipe();
        var writer = new TrackingPipeWriter(output.Writer);

        var pump = TunnelPump.RunAsync(input.Reader, writer, upstream, TimeSpan.FromMinutes(1), cancellation.Token);
        var reservedBeforeData = writer.ReservedBytes;
        await cancellation.CancelAsync();
        await pump;
        await input.Writer.CompleteAsync();
        await input.Reader.CompleteAsync();
        await output.Writer.CompleteAsync();
        await output.Reader.CompleteAsync();

        reservedBeforeData.ShouldBe(0);
    }

    internal static HttpClient CreateClient(ProxyHost proxy, ICredentials? credentials = null, Uri? proxyUri = null) =>
        new(new SocketsHttpHandler
        {
            Proxy = new AlwaysProxy(proxyUri ?? proxy.Uri, credentials),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });

    internal sealed class DelegateFilter(Func<HttpContext, ForwardProxyDestination, bool> allow) : IForwardProxyDestinationFilter
    {
        public ValueTask<bool> IsAllowedAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken) =>
            ValueTask.FromResult(allow(context, destination));
    }

    private sealed class TrackingPipeWriter(PipeWriter inner) : PipeWriter
    {
        public int ReservedBytes { get; private set; }

        public override void Advance(int bytes) => inner.Advance(bytes);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            var memory = inner.GetMemory(sizeHint);
            ReservedBytes = memory.Length;
            return memory;
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            var span = inner.GetSpan(sizeHint);
            ReservedBytes = span.Length;
            return span;
        }
    }
}
