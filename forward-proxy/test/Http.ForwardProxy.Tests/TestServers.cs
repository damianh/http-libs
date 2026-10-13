// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DamianH.Http.ForwardProxy;

internal static class TestCertificate
{
    public static readonly X509Certificate2 Instance = Create();

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }
}

/// <summary>A forward proxy on an ephemeral loopback port that may reach loopback destinations on any port.</summary>
internal sealed class ProxyHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ProxyHost(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public Uri Uri => new($"http://127.0.0.1:{Port}");

    public IServiceProvider Services => _app.Services;

    public static async Task<ProxyHost> StartAsync(
        Action<ForwardProxyOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        bool tls = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (tls)
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(TestCertificate.Instance);
            }
            else
            {
                listen.Protocols = HttpProtocols.Http1;
            }

            listen.UseForwardProxy();
        }));
        builder.Services.AddForwardProxy(options =>
        {
            options.AllowedNetworks.Add(IPNetwork.Parse("127.0.0.0/8"));
            options.AllowedConnectPorts = null;
            options.AllowedHttpPorts = null;
            configure?.Invoke(options);
        });
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseForwardProxy();
        app.MapGet("/health", () => "proxy-health");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ProxyHost(app, GetPort(app));
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    internal static int GetPort(WebApplication app) =>
        new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
}

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

    public static async Task<OriginHost> StartAsync(bool tls = false)
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
        app.Run(async context =>
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
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new OriginHost(app, ProxyHost.GetPort(app), tls);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>A TCP server that echoes bytes and writes <c>BYE</c> when the client half-closes.</summary>
internal sealed class EchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public EchoServer()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                _ = EchoAsync(socket);
            }
        }
        catch (Exception)
        {
        }
    }

    private static async Task EchoAsync(Socket socket)
    {
        using (socket)
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await socket.ReceiveAsync(buffer)) > 0)
                {
                    await socket.SendAsync(buffer.AsMemory(0, read));
                }

                await socket.SendAsync("BYE"u8.ToArray());
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
        }
    }
}

/// <summary>An <see cref="IWebProxy"/> that never bypasses, unlike <see cref="WebProxy"/> which skips loopback.</summary>
internal sealed class AlwaysProxy(Uri proxy, ICredentials? credentials = null) : IWebProxy
{
    public ICredentials? Credentials { get; set; } = credentials;

    public Uri GetProxy(Uri destination) => proxy;

    public bool IsBypassed(Uri host) => false;
}

/// <summary>Raw socket helpers for sending exact proxy requests.</summary>
internal static class RawProxy
{
    public static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        return socket;
    }

    public static async Task SendAsync(Socket socket, string text) =>
        await socket.SendAsync(Encoding.Latin1.GetBytes(text), TestContext.Current.CancellationToken);

    /// <summary>Reads a response head (through the blank line) and returns it with any body bytes read so far.</summary>
    public static async Task<RawResponse> ReadResponseAsync(Socket socket)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var text = Encoding.Latin1.GetString(received.ToArray());
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                var lines = text[..end].Split("\r\n");
                var headers = lines.Skip(1).Select(l => l.Split(':', 2)).ToLookup(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                return new RawResponse(int.Parse(lines[0].Split(' ')[1]), lines[0], headers, text[(end + 4)..]);
            }

            var read = await socket.ReceiveAsync(buffer, timeout.Token);
            if (read == 0)
            {
                throw new IOException($"Connection closed before the response head was complete: {text}");
            }

            received.AddRange(buffer.AsSpan(0, read));
        }
    }

    /// <summary>Reads until the peer closes the connection.</summary>
    public static async Task<string> ReadToEndAsync(Socket socket, string prefix = "")
    {
        var builder = new StringBuilder(prefix);
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        int read;
        while ((read = await socket.ReceiveAsync(buffer, timeout.Token)) > 0)
        {
            builder.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes.</summary>
    public static async Task<string> ReadExactlyAsync(Socket socket, int count, string prefix = "")
    {
        var builder = new StringBuilder(prefix);
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (builder.Length < count)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(0, Math.Min(buffer.Length, count - builder.Length)), timeout.Token);
            if (read == 0)
            {
                throw new IOException($"Connection closed after {builder.Length} of {count} bytes: {builder}");
            }

            builder.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }

        return builder.ToString();
    }
}

internal sealed record RawResponse(int StatusCode, string StatusLine, ILookup<string, string> Headers, string Body)
{
    public string Header(string name) => string.Join(", ", Headers[name]);
}
