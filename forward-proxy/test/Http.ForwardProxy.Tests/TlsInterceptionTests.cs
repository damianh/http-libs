// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DamianH.Http.ForwardProxy.Authentication;
using DamianH.Http.ForwardProxy.Internal.Interception;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class TlsInterceptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InterceptsConnectAndRunsThePipeline()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(configureApp: app => app.Use((context, next) =>
        {
            context.Request.Headers.Authorization = "Bearer injected";
            context.Request.Headers["X-Protocol"] = context.Request.Protocol;
            return next(context);
        }));
        var trusted = false;
        var errors = SslPolicyErrors.None;
        using var client = CreateClient(proxy, (certificate, e) =>
        {
            trusted = TestCertificateAuthority.IsIssuedByRoot(certificate);
            errors = e;
        });

        var body = await client.GetStringAsync(new Uri(origin.Uri, "/secure?x=1"), Ct);

        body.ShouldContain("path=/secure?x=1");
        body.ShouldContain($"host=127.0.0.1:{origin.Port}");
        body.ShouldContain("authorization=Bearer injected");
        body.ShouldContain("header:x-protocol=HTTP/1.1");
        body.ShouldContain("via=1.1 forward-proxy");
        trusted.ShouldBeTrue();
        (errors & SslPolicyErrors.RemoteCertificateNameMismatch).ShouldBe(SslPolicyErrors.None);
    }

    [Fact]
    public async Task ServesHttp2InsideTheTunnel()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(configureApp: app => app.Use((context, next) =>
        {
            context.Request.Headers["X-Protocol"] = context.Request.Protocol;
            return next(context);
        }));
        using var client = CreateClient(proxy, version: HttpVersion.Version20);

        using var response = await client.GetAsync(new Uri(origin.Uri, "/h2"), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        response.Version.ShouldBe(HttpVersion.Version20);
        body.ShouldContain("path=/h2");
        body.ShouldContain("header:x-protocol=HTTP/2");
    }

    [Fact]
    public async Task InterceptsHttp2Connect()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(tls: true, configureApp: app => app.Use((context, next) =>
        {
            context.Request.Headers.Authorization = "Bearer injected";
            context.Request.Headers["X-Protocol"] = context.Request.Protocol;
            return next(context);
        }));

        var caPath = Path.Combine(Path.GetTempPath(), $"forward-proxy-test-ca-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(caPath, TestCertificateAuthority.Root.ExportCertificatePem(), Ct);
        (int ExitCode, string Output, string Error) result;
        try
        {
            result = await Http2ConnectTests.CurlAsync(
                "-sS", "-v", "--trace-config", "h2-proxy", "--proxy", $"https://127.0.0.1:{proxy.Port}", "--proxy-insecure",
                "--proxy-http2", "--cacert", caPath, new Uri(origin.Uri, "/h2").ToString());
        }
        finally
        {
            File.Delete(caPath);
        }

        var (exitCode, output, error) = result;

        exitCode.ShouldBe(0, error);
        error.ShouldContain("CONNECT tunnel established, response 200");
        output.ShouldContain("path=/h2");
        output.ShouldContain("authorization=Bearer injected");
        output.ShouldContain("header:x-protocol=HTTP/");
    }

    [Fact]
    public async Task TransformsEditRequestAndResponseHeaders()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services => services.AddSingleton<IForwardProxyRequestTransform, AuthorizationTransform>());
        using var client = CreateClient(proxy);

        using var response = await client.GetAsync(new Uri(origin.Uri, "/transform"), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldContain("authorization=Bearer token-for-127.0.0.1");
        response.Headers.GetValues("X-Transformed").ShouldBe(["Connect"]);
    }

    [Fact]
    public async Task TransformsApplyToHttpForwarding()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await ProxyHost.StartAsync(configureServices: services => services.AddSingleton<IForwardProxyRequestTransform, AuthorizationTransform>());
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using var response = await client.GetAsync(origin.Uri, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        body.ShouldContain("authorization=Bearer token-for-127.0.0.1");
        response.Headers.GetValues("X-Transformed").ShouldBe(["Http"]);
    }

    [Fact]
    public async Task FailingTransformIsAProxyError()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services => services.AddSingleton<IForwardProxyRequestTransform, ThrowingTransform>());
        using var client = CreateClient(proxy);

        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=proxy_internal_error"]);
    }

    [Fact]
    public async Task StreamsServerSentEvents()
    {
        var release = new TaskCompletionSource();
        await using var origin = await OriginHost.StartAsync(tls: true, handler: async context =>
        {
            context.Response.Headers["Mcp-Session-Id"] = "session-1";
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                $"data: {context.Request.Headers.Authorization} {context.Request.Headers["Mcp-Session-Id"]}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: second\n\n", context.RequestAborted);
        });
        await using var proxy = await StartInterceptingProxyAsync(configureApp: app => app.Use((context, next) =>
        {
            context.Request.Headers.Authorization = "Bearer injected";
            return next(context);
        }));
        using var client = CreateClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Uri, "/mcp"))
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Mcp-Session-Id", "session-1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));

        response.Headers.GetValues("Mcp-Session-Id").ShouldBe(["session-1"]);
        (await reader.ReadLineAsync(Ct)).ShouldBe("data: Bearer injected session-1");
        release.SetResult();
        await reader.ReadLineAsync(Ct);
        (await reader.ReadLineAsync(Ct)).ShouldBe("data: second");
    }

    [Fact]
    public async Task RelaysWebSocketsInsideTheTunnel()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync();
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = new AlwaysProxy(proxy.Uri);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => TestCertificateAuthority.IsIssuedByRoot(certificate);

        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{origin.Port}/ws"), Ct);
        await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, Ct);
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, Ct);

        Encoding.UTF8.GetString(buffer, 0, received.Count).ShouldBe("hello");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Fact]
    public async Task CountsAWebSocketInsideTheTunnelAsOneTunnel()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync();
        var meterFactory = proxy.Services.GetRequiredService<IMeterFactory>();
        long activeTunnels = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == meterFactory && instrument.Name == "forward_proxy.tunnels.active")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref activeTunnels, value));
        listener.Start();
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = new AlwaysProxy(proxy.Uri);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => TestCertificateAuthority.IsIssuedByRoot(certificate);

        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{origin.Port}/ws"), Ct);
        await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, Ct);
        await socket.ReceiveAsync(new byte[64], Ct);

        Interlocked.Read(ref activeTunnels).ShouldBe(1);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    [Fact]
    public async Task TunnelsWhenNotSelected()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        var selected = new ConcurrentQueue<ForwardProxyDestination>();
        await using var proxy = await StartInterceptingProxyAsync(select: (_, destination) =>
        {
            selected.Enqueue(destination);
            return false;
        });
        var intercepted = true;
        using var client = CreateClient(proxy, (certificate, _) => intercepted = TestCertificateAuthority.IsIssuedByRoot(certificate));

        (await client.GetStringAsync(origin.Uri, Ct)).ShouldContain("authorization=\n");

        intercepted.ShouldBeFalse();
        selected.ShouldBe([new ForwardProxyDestination("127.0.0.1", origin.Port, ForwardProxyRequestKind.Connect)]);
    }

    [Fact]
    public async Task TunnelsWhenNoSelectorIsRegistered()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(configureServices: services => services
            .AddForwardProxyTlsInterception()
            .AddSingleton<IForwardProxyCertificateProvider>(new DelegateCertificateProvider(d => TestCertificateAuthority.GetCertificate(d.Host))));
        var intercepted = true;
        using var client = CreateClient(proxy, (certificate, _) => intercepted = TestCertificateAuthority.IsIssuedByRoot(certificate));

        (await client.GetStringAsync(origin.Uri, Ct)).ShouldContain("method=GET");

        intercepted.ShouldBeFalse();
    }

    [Fact]
    public async Task FailsTheHandshakeWithoutACertificate()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(certificates: _ => null);
        using var client = CreateClient(proxy);

        await Should.ThrowAsync<HttpRequestException>(() => client.GetStringAsync(origin.Uri, Ct));
    }

    [Fact]
    public async Task RejectsRequestsForAnotherHost()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync();
        using var client = CreateClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Uri);
        request.Headers.Host = "example.com";

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.MisdirectedRequest);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=http_request_denied"]);
    }

    [Fact]
    public async Task RejectsUntrustedDestinationCertificates()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(trustOrigin: false);
        using var client = CreateClient(proxy);

        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=tls_certificate_error"]);
    }

    [Fact]
    public async Task ReportsDestinationTlsFailures()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await StartInterceptingProxyAsync();
        using var client = CreateClient(proxy);

        using var response = await client.GetAsync($"https://127.0.0.1:{origin.Port}/", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=tls_protocol_error"]);
    }

    [Fact]
    public async Task AppliesDestinationPolicyBeforeInterception()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        var selected = false;
        await using var proxy = await StartInterceptingProxyAsync(
            select: (_, _) => selected = true,
            configure: o => o.AllowedNetworks.Clear());
        using var client = CreateClient(proxy);

        await Should.ThrowAsync<HttpRequestException>(() => client.GetStringAsync(origin.Uri, Ct));

        selected.ShouldBeFalse();
    }

    [Fact]
    public async Task ExposesTheProxyUserToThePipeline()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync(
            configure: o => o.AuthorizationPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            configureServices: services => services.AddAuthentication(ProxyBasicDefaults.AuthenticationScheme)
                .AddProxyBasic(o => o.ValidateCredentials = (_, _, _) => ValueTask.FromResult(true)),
            configureApp: app => app.Use((context, next) =>
            {
                context.Request.Headers["X-User"] = context.User.Identity?.Name;
                return next(context);
            }));
        using var client = CreateClient(proxy, credentials: new NetworkCredential("alice", "password"));

        var body = await client.GetStringAsync(origin.Uri, Ct);

        body.ShouldContain("header:x-user=alice");
    }

    [Fact]
    public async Task ClosesIdleInterceptedTunnels()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        var tunnels = 0;
        await using var proxy = await StartInterceptingProxyAsync(
            select: (_, _) => Interlocked.Increment(ref tunnels) > 0,
            configure: o => o.TunnelIdleTimeout = TimeSpan.FromMilliseconds(200));
        using var client = CreateClient(proxy);

        await client.GetStringAsync(origin.Uri, Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await client.GetStringAsync(origin.Uri, Ct);

        tunnels.ShouldBe(2);
    }

    [Fact]
    public async Task RecordsInterceptionMetrics()
    {
        await using var origin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await StartInterceptingProxyAsync();
        var meterFactory = proxy.Services.GetRequiredService<IMeterFactory>();
        var measurements = new ConcurrentQueue<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == meterFactory && instrument.Name == "forward_proxy.requests")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? kind = null, outcome = null;
            foreach (var tag in tags)
            {
                kind = tag.Key == "forward_proxy.kind" ? (string?)tag.Value : kind;
                outcome = tag.Key == "forward_proxy.outcome" ? (string?)tag.Value : outcome;
            }

            measurements.Enqueue($"{kind}:{outcome}");
        });
        listener.Start();
        using var client = CreateClient(proxy);

        await client.GetStringAsync(origin.Uri, Ct);
        await client.GetStringAsync(origin.Uri, Ct);

        measurements.ShouldBe(["connect:intercepted", "intercepted:allowed", "intercepted:allowed"]);
    }

    [Fact]
    public async Task RequiresACertificateProvider()
    {
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => ProxyHost.StartAsync(
            configureServices: services => services.AddForwardProxyTlsInterception()));

        exception.Message.ShouldContain(nameof(IForwardProxyCertificateProvider));
    }

    [Theory]
    [InlineData(HttpProtocols.Http3, 10)]
    [InlineData(HttpProtocols.Http1AndHttp2AndHttp3, 10)]
    [InlineData(HttpProtocols.Http1AndHttp2, 0)]
    [InlineData(HttpProtocols.Http1AndHttp2, -1)]
    public void ValidatesOptions(HttpProtocols protocols, int handshakeTimeoutSeconds)
    {
        var options = new ForwardProxyTlsInterceptionOptions { ClientProtocols = protocols, HandshakeTimeout = TimeSpan.FromSeconds(handshakeTimeoutSeconds) };

        new TlsInterceptionOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void DefaultOptionsAreValid() =>
        new TlsInterceptionOptionsValidator().Validate(null, new ForwardProxyTlsInterceptionOptions()).Succeeded.ShouldBeTrue();

    private static Task<ProxyHost> StartInterceptingProxyAsync(
        Func<HttpContext, ForwardProxyDestination, bool>? select = null,
        Func<ForwardProxyDestination, SslStreamCertificateContext?>? certificates = null,
        Action<ForwardProxyOptions>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        bool trustOrigin = true,
        bool tls = false) =>
        ProxyHost.StartAsync(
            configure,
            services =>
            {
                services.AddForwardProxyTlsInterception(o =>
                {
                    if (trustOrigin)
                    {
                        o.DestinationCertificateValidation = (_, certificate, _, _) =>
                            certificate?.GetCertHashString() == TestCertificate.Instance.GetCertHashString();
                    }
                });
                services.AddSingleton<IForwardProxyInterceptionSelector>(new DelegateSelector(select ?? ((_, _) => true)));
                services.AddSingleton<IForwardProxyCertificateProvider>(new DelegateCertificateProvider(certificates ?? (d => TestCertificateAuthority.GetCertificate(d.Host))));
                configureServices?.Invoke(services);
            },
            tls,
            configureApp);

    private static HttpClient CreateClient(
        ProxyHost proxy,
        Action<X509Certificate?, SslPolicyErrors>? onCertificate = null,
        Version? version = null,
        ICredentials? credentials = null) =>
        new(new SocketsHttpHandler
        {
            Proxy = new AlwaysProxy(proxy.Uri, credentials),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    onCertificate?.Invoke(certificate, errors);
                    return true;
                },
            },
        })
        {
            DefaultRequestVersion = version ?? HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    private sealed class DelegateSelector(Func<HttpContext, ForwardProxyDestination, bool> select) : IForwardProxyInterceptionSelector
    {
        public ValueTask<bool> ShouldInterceptAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken) =>
            ValueTask.FromResult(select(context, destination));
    }

    private sealed class DelegateCertificateProvider(Func<ForwardProxyDestination, SslStreamCertificateContext?> certificates) : IForwardProxyCertificateProvider
    {
        public ValueTask<SslStreamCertificateContext?> GetCertificateAsync(ForwardProxyDestination destination, CancellationToken cancellationToken) =>
            ValueTask.FromResult(certificates(destination));
    }

    private sealed class AuthorizationTransform : IForwardProxyRequestTransform
    {
        public ValueTask TransformRequestAsync(HttpContext context, ForwardProxyDestination destination, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"token-for-{destination.Host}");
            return ValueTask.CompletedTask;
        }

        public ValueTask TransformResponseAsync(HttpContext context, ForwardProxyDestination destination, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            response.Headers.Add("X-Transformed", destination.Kind.ToString());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingTransform : IForwardProxyRequestTransform
    {
        public ValueTask TransformRequestAsync(HttpContext context, ForwardProxyDestination destination, HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Transform failed.");
    }
}
