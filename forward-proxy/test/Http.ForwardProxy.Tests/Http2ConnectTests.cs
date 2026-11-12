// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// .NET's HttpClient cannot send a classic HTTP/2 CONNECT, so most tests use curl's <c>--proxy-http2</c>.
/// Assertions rely on curl's <c>h2-proxy</c> trace output, which is stable across curl versions, rather than
/// its informational messages, which are reworded between releases (e.g. 8.5 vs 8.22).
/// The stream-abort test writes the small set of HTTP/2 frames it needs so it can issue a real <c>RST_STREAM</c>.
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
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port, TestContext.Current.CancellationToken);
        using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols = [SslApplicationProtocol.Http2],
        }, TestContext.Current.CancellationToken);

        await tls.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await WriteHttp2FrameAsync(tls, type: 0x04, flags: 0, streamId: 0, ReadOnlyMemory<byte>.Empty);

        using var headerBlock = new MemoryStream();
        headerBlock.WriteByte(0x02);
        headerBlock.WriteByte(7);
        headerBlock.Write("CONNECT"u8);
        var authority = Encoding.ASCII.GetBytes($"127.0.0.1:{port}");
        headerBlock.WriteByte(0x01);
        headerBlock.WriteByte((byte)authority.Length);
        headerBlock.Write(authority);
        await WriteHttp2FrameAsync(tls, type: 0x01, flags: 0x04, streamId: 1, headerBlock.ToArray());

        while (true)
        {
            var frame = await ReadHttp2FrameAsync(tls, TestContext.Current.CancellationToken);
            if (frame.Type == 0x04 && (frame.Flags & 0x01) == 0)
            {
                await WriteHttp2FrameAsync(tls, type: 0x04, flags: 0x01, streamId: 0, ReadOnlyMemory<byte>.Empty);
            }

            if (frame.Type == 0x01 && frame.StreamId == 1)
            {
                while ((frame.Flags & 0x04) == 0)
                {
                    frame = await ReadHttp2FrameAsync(tls, TestContext.Current.CancellationToken);
                    frame.Type.ShouldBe((byte)0x09);
                    frame.StreamId.ShouldBe(1);
                }

                break;
            }
        }

        var originAccept = listener.AcceptSocketAsync(TestContext.Current.CancellationToken);
        var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n");
        await WriteHttp2FrameAsync(tls, type: 0x00, flags: 0, streamId: 1, request);
        using var originSocket = await originAccept;
        (await originSocket.ReceiveAsync(new byte[1024], TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);

        var resetError = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(resetError, 0x08);
        await WriteHttp2FrameAsync(tls, type: 0x03, flags: 0, streamId: 1, resetError);

        var ex = await Should.ThrowAsync<SocketException>(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await originSocket.ReceiveAsync(new byte[1024], timeout.Token);
        });
        ex.SocketErrorCode.ShouldBe(SocketError.ConnectionReset);
    }

    private static async Task WriteHttp2FrameAsync(Stream stream, byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload)
    {
        var header = new byte[9];
        header[0] = (byte)(payload.Length >> 16);
        header[1] = (byte)(payload.Length >> 8);
        header[2] = (byte)payload.Length;
        header[3] = type;
        header[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), streamId);
        await stream.WriteAsync(header, TestContext.Current.CancellationToken);
        await stream.WriteAsync(payload, TestContext.Current.CancellationToken);
    }

    private static async Task<Http2Frame> ReadHttp2FrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[9];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new(header[3], header[4], BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & 0x7fffffff);
    }

    private readonly record struct Http2Frame(byte Type, byte Flags, int StreamId);

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
