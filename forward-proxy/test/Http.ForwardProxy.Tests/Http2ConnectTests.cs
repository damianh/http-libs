// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// .NET's HttpClient cannot send a classic HTTP/2 CONNECT, so these tests use curl's <c>--proxy-http2</c>.
/// Assertions rely on curl's <c>h2-proxy</c> trace output, which is stable across curl versions, rather than
/// its informational messages, which are reworded between releases (e.g. 8.5 vs 8.22).
/// </summary>
public class Http2ConnectTests
{
    [Fact]
    public async Task TunnelsOverHttp2()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(tls: true);

        var (exitCode, output, error) = await CurlAsync(
            "-sS", "-v", "--trace-config", "h2-proxy", "--proxy", $"https://127.0.0.1:{proxy.Port}", "--proxy-insecure",
            "--proxy-http2", "-k", new Uri(origin.Uri, "/h2").ToString());

        exitCode.ShouldBe(0, error);
        output.ShouldContain("path=/h2");
        error.ShouldContain("status: HTTP/2 200");
        error.ShouldContain("CONNECT tunnel established, response 200");
    }

    [Fact]
    public async Task Http1ConnectStillWorksOnTlsListener()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(tls: true);

        var (exitCode, output, error) = await CurlAsync(
            "-sS", "--proxy", $"https://127.0.0.1:{proxy.Port}", "--proxy-insecure", "-k",
            new Uri(origin.Uri, "/h1").ToString());

        exitCode.ShouldBe(0, error);
        output.ShouldContain("path=/h1");
    }

    [Fact]
    public async Task ReportsDeniedTunnelsOverHttp2()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(o => o.AllowedNetworks.Clear(), tls: true);

        var (exitCode, _, error) = await CurlAsync(
            "-sS", "-v", "--trace-config", "h2-proxy", "--proxy", $"https://127.0.0.1:{proxy.Port}", "--proxy-insecure",
            "--proxy-http2", "-k", new Uri(origin.Uri, "/h2").ToString());

        exitCode.ShouldNotBe(0);
        error.ShouldContain("status: HTTP/2 403");
        error.ShouldContain("header: proxy-status: forward-proxy; error=destination_ip_prohibited");
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

        var curl = CurlAsync(
            "-sS", "-v", "--trace-config", "h2-proxy", "--proxy", $"https://127.0.0.1:{proxy.Port}", "--proxy-insecure",
            "--proxy-http2", "--proxytunnel", "--max-time", "10", $"http://127.0.0.1:{port}/");
        using (var originSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken))
        {
            await originSocket.ReceiveAsync(new byte[1024], TestContext.Current.CancellationToken);
            originSocket.LingerState = new LingerOption(true, 0);
        }

        var (exitCode, _, error) = await curl;

        exitCode.ShouldNotBe(0);
        error.ShouldContain("<- FRAME[RST_STREAM, len=4, flags=0, error=10]");
    }

    [Fact]
    public async Task ResetsTheDestinationConnectionWhenTheStreamIsAborted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var proxy = await ProxyHost.StartAsync(tls: true);

        var curl = CurlAsync(
            "-sS", "--proxy", $"https://127.0.0.1:{proxy.Port}", "--proxy-insecure",
            "--proxy-http2", "--proxytunnel", "--max-time", "1", $"http://127.0.0.1:{port}/");
        using var originSocket = await listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        await originSocket.ReceiveAsync(new byte[1024], TestContext.Current.CancellationToken);
        var (exitCode, _, _) = await curl;

        exitCode.ShouldNotBe(0);
        var ex = await Should.ThrowAsync<SocketException>(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await originSocket.ReceiveAsync(new byte[1024], timeout.Token);
        });
        ex.SocketErrorCode.ShouldBe(SocketError.ConnectionReset);
    }

    internal static async Task<(int ExitCode, string Output, string Error)> CurlAsync(params string[] args)
    {
        if (!await SupportsProxyHttp2Async())
        {
            Assert.Skip("curl with --proxy-http2 and --trace-config support is not available.");
        }

        var startInfo = new ProcessStartInfo("curl", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }

    private static async Task<bool> SupportsProxyHttp2Async()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("curl", ["--help", "all"]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var help = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return help.Contains("--proxy-http2", StringComparison.Ordinal)
                && help.Contains("--trace-config", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
