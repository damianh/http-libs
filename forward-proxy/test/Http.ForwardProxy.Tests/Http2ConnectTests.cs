// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// .NET's HttpClient cannot send a classic HTTP/2 CONNECT, so these tests use <see cref="Http2ConnectClient"/>.
/// HTTP requests inside a tunnel are sent by an HttpClient whose connection is the tunnel stream.
/// </summary>
public class Http2ConnectTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TunnelsOverHttp2()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(tls: true);

        await using var tunnel = await Http2ConnectClient.ConnectAsync(proxy.Port, $"127.0.0.1:{origin.Port}", Ct);
        tunnel.StatusCode.ShouldBe(StatusCodes.Status200OK);
        using var client = tunnel.CreateHttpClient((certificate, _) => IsTestCertificate(certificate));
        var body = await client.GetStringAsync(new Uri(origin.Uri, "/h2"), Ct);

        body.ShouldContain("path=/h2");
    }

    [Fact]
    public async Task Http1ConnectStillWorksOnTlsListener()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(tls: true);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new AlwaysProxy(new Uri($"https://127.0.0.1:{proxy.Port}")),
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => IsTestCertificate(certificate) },
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        var body = await client.GetStringAsync(new Uri(origin.Uri, "/h1"), Ct);

        body.ShouldContain("path=/h1");
    }

    [Fact]
    public async Task ReportsDeniedTunnelsOverHttp2()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(o => o.AllowedNetworks.Clear(), tls: true);

        await using var tunnel = await Http2ConnectClient.ConnectAsync(proxy.Port, $"127.0.0.1:{origin.Port}", Ct);

        tunnel.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        tunnel.GetHeader("proxy-status").ShouldBe("forward-proxy; error=destination_ip_prohibited");
    }

    [Fact]
    public async Task PassesExtendedConnectToTheApplication()
    {
        await using var proxy = await ProxyHost.StartAsync(tls: true, configureApp: app => app.Map("/chat", (HttpContext context) =>
            Results.Text(
                $"method={context.Request.Method} protocol={context.Features.Get<IHttpExtendedConnectFeature>()?.Protocol}",
                statusCode: StatusCodes.Status418ImATeapot)));
        using var client = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        using var request = new HttpRequestMessage(HttpMethod.Connect, $"https://127.0.0.1:{proxy.Port}/chat")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Protocol = "websocket";

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe((HttpStatusCode)StatusCodes.Status418ImATeapot);
        response.Headers.Contains("Proxy-Status").ShouldBeFalse();
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldBe("method=CONNECT protocol=websocket");
    }

    [Fact]
    public async Task ResetsTheStreamWithConnectErrorWhenTheDestinationResets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var proxy = await ProxyHost.StartAsync(tls: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);

        await using var tunnel = await Http2ConnectClient.ConnectAsync(proxy.Port, $"127.0.0.1:{port}", Ct);
        tunnel.StatusCode.ShouldBe(StatusCodes.Status200OK);
        using (var originSocket = await listener.AcceptSocketAsync(timeout.Token))
        {
            await tunnel.Tunnel.WriteAsync("hello"u8.ToArray(), timeout.Token);
            (await originSocket.ReceiveAsync(new byte[1024], timeout.Token)).ShouldBeGreaterThan(0);
            originSocket.LingerState = new LingerOption(true, 0);
        }

        (await tunnel.Reset.WaitAsync(timeout.Token)).ShouldBe(Http2ConnectClient.ConnectError);
    }

    [Fact]
    public async Task ResetsTheDestinationConnectionWhenTheStreamIsAborted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientAborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var proxy = await ProxyHost.StartAsync(
            tls: true,
            configureApp: app => app.Use(async (context, next) =>
            {
                context.RequestAborted.Register(() => clientAborted.TrySetResult());
                await next();
            }));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Timeout);

        await using var tunnel = await Http2ConnectClient.ConnectAsync(proxy.Port, $"127.0.0.1:{port}", Ct);
        tunnel.StatusCode.ShouldBe(StatusCodes.Status200OK);
        using var originSocket = await listener.AcceptSocketAsync(timeout.Token);
        var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");
        await tunnel.Tunnel.WriteAsync(request, timeout.Token);
        var receivedRequest = new byte[request.Length];
        for (var received = 0; received < receivedRequest.Length;)
        {
            var count = await originSocket.ReceiveAsync(receivedRequest.AsMemory(received), timeout.Token);
            count.ShouldBeGreaterThan(0);
            received += count;
        }

        receivedRequest.ShouldBe(request);

        await tunnel.ResetAsync(Http2ConnectClient.CancelError, timeout.Token);
        await clientAborted.Task.WaitAsync(timeout.Token);

        var ex = await Should.ThrowAsync<SocketException>(async () =>
            await originSocket.ReceiveAsync(new byte[1024], timeout.Token));
        ex.SocketErrorCode.ShouldBe(SocketError.ConnectionReset);
    }

    private static bool IsTestCertificate(X509Certificate? certificate) =>
        certificate?.GetCertHashString() == TestCertificate.Instance.GetCertHashString();
}
