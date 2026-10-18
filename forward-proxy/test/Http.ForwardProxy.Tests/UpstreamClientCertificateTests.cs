// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DamianH.Http.ForwardProxy.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using static DamianH.Http.ForwardProxy.TlsInterceptionTests;

namespace DamianH.Http.ForwardProxy;

public class UpstreamClientCertificateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNullSelectorUsesNoCertificate(bool registerSelector)
    {
        await using var origin = await StartOriginAsync();
        var calls = 0;
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
        {
            if (registerSelector)
            {
                services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) =>
                {
                    Interlocked.Increment(ref calls);
                    return ValueTask.FromResult<X509Certificate2?>(null);
                }));
            }
        });
        using var client = CreateClient(proxy);

        (await client.GetStringAsync(origin.Uri, Ct)).ShouldStartWith("none|");
        calls.ShouldBe(registerSelector ? 1 : 0);
    }

    [Fact]
    public async Task IsolatesCertificatesAndNullWhileReusingTheSameIdentity()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        a.Subject.ShouldBe(b.Subject);
        using var aWrapper = new X509Certificate2(a);
        await using var origin = await StartOriginAsync();
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((context, _, _) =>
                ValueTask.FromResult(context.Request.Path.Value switch
                {
                    "/a" => a,
                    "/a-wrapper" => aWrapper,
                    "/b" => b,
                    _ => null,
                }))));
        using var client = CreateClient(proxy);

        var firstA = await client.GetStringAsync(new Uri(origin.Uri, "/a"), Ct);
        var firstB = await client.GetStringAsync(new Uri(origin.Uri, "/b"), Ct);
        var secondA = await client.GetStringAsync(new Uri(origin.Uri, "/a-wrapper"), Ct);
        var noCertificate = await client.GetStringAsync(new Uri(origin.Uri, "/none"), Ct);
        var secondB = await client.GetStringAsync(new Uri(origin.Uri, "/b"), Ct);

        firstA.ShouldStartWith($"{Fingerprint(a)}|");
        firstB.ShouldStartWith($"{Fingerprint(b)}|");
        noCertificate.ShouldStartWith("none|");
        secondA.ShouldBe(firstA);
        secondB.ShouldBe(firstB);
        new[] { firstA.Split('|')[1], firstB.Split('|')[1], noCertificate.Split('|')[1] }.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task ReconnectingCannotResumeAnotherCertificateIdentity()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        X509Certificate2? selected = a;
        await using var origin = await StartOriginAsync(closeConnections: true);
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) => ValueTask.FromResult<X509Certificate2?>(selected))));
        using var client = CreateClient(proxy);
        var connections = new HashSet<string>();
        foreach (var certificate in new[] { a, b, a, null, b, null })
        {
            selected = certificate;
            var body = await client.GetStringAsync(origin.Uri, Ct);
            body.Split('|')[0].ShouldBe(certificate is null ? "none" : Fingerprint(certificate));
            connections.Add(body.Split('|')[1]).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task ScopedSelectionFollowsMiddlewareAndTransformsOnConcurrentHttp2Requests()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        var scopes = new ConcurrentBag<Guid>();
        var downstreamConnections = new ConcurrentBag<string>();
        await using var origin = await StartOriginAsync(requireCertificate: true);
        await using var proxy = await StartInterceptingProxyAsync(
            configureServices: services =>
            {
                services.AddSingleton<IForwardProxyRequestTransform>(new SnapshotTransform(a, b));
                services.AddScoped<IForwardProxyUpstreamClientCertificateSelector>(_ =>
                {
                    var scope = Guid.NewGuid();
                    return new Selector((context, destination, ct) =>
                    {
                        context.Items["middleware"].ShouldBe(true);
                        destination.Port.ShouldBe(origin.Port);
                        destination.Kind.ShouldBe(ForwardProxyRequestKind.Connect);
                        ct.CanBeCanceled.ShouldBeTrue();
                        scopes.Add(scope);
                        downstreamConnections.Add(context.Connection.Id);
                        return ValueTask.FromResult((X509Certificate2?)context.Items["certificate"]);
                    });
                });
            },
            configureApp: app => app.Use((context, next) =>
            {
                context.Items["middleware"] = true;
                return next(context);
            }));
        using var client = CreateClient(proxy, version: HttpVersion.Version20);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            var certificate = index % 2 == 0 ? a : b;
            var body = await client.GetStringAsync(new Uri(origin.Uri, index % 2 == 0 ? "/a" : "/b"), Ct);
            body.ShouldStartWith($"{Fingerprint(certificate)}|");
        }));

        scopes.Count.ShouldBe(12);
        scopes.Distinct().Count().ShouldBe(12);
        downstreamConnections.Distinct().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("public-only")]
    [InlineData("disposed")]
    [InlineData("expired")]
    [InlineData("future")]
    public async Task SelectionFailureDoesNotSendOrDowngrade(string failure)
    {
        using var valid = TestCertificateAuthority.IssueClient();
        using var certificate = failure switch
        {
            "public-only" => X509CertificateLoader.LoadCertificate(valid.RawData),
            "expired" => TestCertificateAuthority.IssueClient(DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1)),
            "future" => TestCertificateAuthority.IssueClient(DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2)),
            _ => new X509Certificate2(valid),
        };
        if (failure == "disposed")
        {
            certificate.Dispose();
        }

        var originRequests = 0;
        await using var origin = await OriginHost.StartAsync(tls: true, handler: _ =>
        {
            Interlocked.Increment(ref originRequests);
            return Task.CompletedTask;
        });
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) =>
                failure == "throw" ? throw new InvalidOperationException("selection failed") : ValueTask.FromResult<X509Certificate2?>(certificate))));
        using var client = CreateClient(proxy);
        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=proxy_internal_error"]);
        originRequests.ShouldBe(0);
        valid.HasPrivateKey.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectorOwnedCancellationIsNotReportedAsTimeout()
    {
        var originRequests = 0;
        await using var origin = await OriginHost.StartAsync(tls: true, handler: _ =>
        {
            Interlocked.Increment(ref originRequests);
            return Task.CompletedTask;
        });
        await using var proxy = await StartInterceptingProxyAsync(
            configure: options => options.ConnectTimeout = TimeSpan.FromMinutes(1),
            configureServices: services =>
                services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, ct) =>
                {
                    ct.IsCancellationRequested.ShouldBeFalse();
                    throw new OperationCanceledException(new CancellationToken(canceled: true));
                })));
        using var client = CreateClient(proxy);
        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=proxy_internal_error"]);
        originRequests.ShouldBe(0);
    }

    [Fact]
    public async Task SelectionTimeoutIsReportedWithoutSending()
    {
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = await StartOriginAsync();
        await using var proxy = await StartInterceptingProxyAsync(
            configure: options => options.ConnectTimeout = TimeSpan.FromMilliseconds(200),
            configureServices: services => services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector(async (_, _, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return null;
                }
                finally
                {
                    cancellation.TrySetResult();
                }
            })));
        using var client = CreateClient(proxy);
        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=connection_timeout"]);
        await cancellation.Task.WaitAsync(Ct);
    }

    [Fact]
    public async Task ClientAbortCancelsSelection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = await StartOriginAsync();
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector(async (_, _, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return null;
                }
                finally
                {
                    canceled.TrySetResult();
                }
            })));
        using var client = CreateClient(proxy);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = client.GetAsync(origin.Uri, abort.Token);
        await entered.Task.WaitAsync(Ct);
        abort.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        await canceled.Task.WaitAsync(Ct);
    }

    [Fact]
    public async Task PlainHttpAndOpaqueConnectDoNotSelect()
    {
        await using var httpOrigin = await OriginHost.StartAsync();
        await using var tlsOrigin = await OriginHost.StartAsync(tls: true);
        await using var proxy = await ProxyHost.StartAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) =>
                throw new InvalidOperationException("must not be called"))));
        using var client = ConnectTunnelTests.CreateClient(proxy);

        (await client.GetStringAsync(httpOrigin.Uri, Ct)).ShouldContain("method=GET");
        (await client.GetStringAsync(tlsOrigin.Uri, Ct)).ShouldContain("method=GET");
    }

    [Fact]
    public async Task CertificatePoolStillValidatesTheDestinationCertificate()
    {
        using var certificate = TestCertificateAuthority.IssueClient();
        await using var origin = await StartOriginAsync(requireCertificate: true);
        await using var proxy = await StartInterceptingProxyAsync(trustOrigin: false, configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) => ValueTask.FromResult<X509Certificate2?>(certificate))));
        using var client = CreateClient(proxy);
        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=tls_certificate_error"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CertificatePoolUsesThePolicyCheckingDialerAndItsTimeout(bool timeout)
    {
        using var certificate = TestCertificateAuthority.IssueClient();
        var resolutions = 0;
        await using var proxy = await StartInterceptingProxyAsync(configure: options =>
        {
            options.AllowedNetworks.Clear();
            options.ConnectTimeout = TimeSpan.FromSeconds(2);
        }, configureServices: services =>
        {
            services.AddSingleton(sp => new ProxyDialer(
                sp.GetRequiredService<DestinationPolicy>(),
                // Let the dialer's deadline win, rather than racing the handler's connect deadline.
                Options.Create(new ForwardProxyOptions { ConnectTimeout = TimeSpan.FromMilliseconds(50) }),
                sp.GetRequiredService<ILogger<ProxyDialer>>())
            {
                ResolveHostAsync = async (_, ct) =>
                {
                    Interlocked.Increment(ref resolutions);
                    if (timeout)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }

                    return [IPAddress.Loopback];
                },
            });
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) =>
                ValueTask.FromResult<X509Certificate2?>(certificate)));
        });
        using var client = CreateClient(proxy);
        using var response = await client.GetAsync("https://upstream.example/", Ct);

        response.StatusCode.ShouldBe(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.Forbidden);
        response.Headers.GetValues("Proxy-Status").ShouldBe([$"forward-proxy; error={(timeout ? "dns_timeout" : "destination_ip_prohibited")}"]);
        resolutions.ShouldBe(1);
    }

    [Fact]
    public async Task RejectedClientCertificateDoesNotDowngrade()
    {
        using var certificate = TestCertificateAuthority.IssueClient();
        var originRequests = 0;
        await using var origin = await OriginHost.StartAsync(tls: true, handler: _ =>
        {
            Interlocked.Increment(ref originRequests);
            return Task.CompletedTask;
        }, configureTls: options =>
        {
            options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            options.ClientCertificateValidation = (_, _, _) => false;
        });
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((_, _, _) => ValueTask.FromResult<X509Certificate2?>(certificate))));
        using var client = CreateClient(proxy);
        using var response = await client.GetAsync(origin.Uri, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Headers.Contains("Proxy-Status").ShouldBeTrue();
        originRequests.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingResponseRetainsLeaseUntilBodyCompletes(bool eventStream)
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = await OriginHost.StartAsync(tls: true, handler: async context =>
        {
            if (context.Request.Path == "/hold")
            {
                context.Response.ContentType = eventStream ? "text/event-stream" : "text/plain";
                await context.Response.WriteAsync(new string('x', 32_768), Ct);
                await context.Response.Body.FlushAsync(Ct);
                await release.Task.WaitAsync(context.RequestAborted);
            }

            await context.Response.WriteAsync("done", Ct);
        });
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
        {
            services.Configure<ForwardProxyTlsInterceptionOptions>(o =>
            {
                o.MaxUpstreamClientCertificatePools = 1;
                o.UpstreamClientCertificatePoolIdleTimeout = TimeSpan.FromMilliseconds(20);
            });
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((context, _, _) =>
                ValueTask.FromResult<X509Certificate2?>(context.Request.Path == "/hold" ? a : b)));
        });
        using var client = CreateClient(proxy);
        try
        {
            using var held = await client.GetAsync(new Uri(origin.Uri, "/hold"), HttpCompletionOption.ResponseHeadersRead, Ct);
            await Task.Delay(60, Ct);
            using var blocked = await client.GetAsync(new Uri(origin.Uri, "/other"), Ct);
            blocked.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            blocked.Headers.GetValues("Proxy-Status").ShouldBe(["forward-proxy; error=connection_limit_reached"]);
            release.TrySetResult();
            (await held.Content.ReadAsStringAsync(Ct)).ShouldEndWith("done");
            (await client.GetStringAsync(new Uri(origin.Uri, "/other"), Ct)).ShouldBe("done");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task WebSocketRetainsItsCertificatePoolLease()
    {
        using var a = TestCertificateAuthority.IssueClient();
        using var b = TestCertificateAuthority.IssueClient();
        await using var origin = await OriginHost.StartAsync(tls: true, configureTls: options =>
        {
            options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            options.ClientCertificateValidation = (certificate, _, _) => TestCertificateAuthority.IsIssuedByRoot(certificate);
        });
        await using var proxy = await StartInterceptingProxyAsync(configureServices: services =>
        {
            services.Configure<ForwardProxyTlsInterceptionOptions>(o => o.MaxUpstreamClientCertificatePools = 1);
            services.AddSingleton<IForwardProxyUpstreamClientCertificateSelector>(new Selector((context, _, _) =>
                ValueTask.FromResult<X509Certificate2?>(context.Request.Path == "/ws" ? a : b)));
        });
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = new AlwaysProxy(proxy.Uri);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => TestCertificateAuthority.IsIssuedByRoot(certificate);
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{origin.Port}/ws"), Ct);
        using var client = CreateClient(proxy);
        using var blocked = await client.GetAsync(origin.Uri, Ct);
        blocked.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, Ct);
        var buffer = new byte[32];
        var result = await socket.ReceiveAsync(buffer, Ct);
        System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count).ShouldBe("hello");
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Ct);
    }

    private static Task<OriginHost> StartOriginAsync(bool requireCertificate = false, bool closeConnections = false) =>
        OriginHost.StartAsync(tls: true, handler: async context =>
        {
            if (closeConnections)
            {
                context.Response.Headers.Connection = "close";
            }

            var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            await context.Response.WriteAsync($"{(certificate is null ? "none" : Fingerprint(certificate))}|{context.Connection.Id}", context.RequestAborted);
        }, configureTls: options =>
        {
            options.ClientCertificateMode = requireCertificate ? ClientCertificateMode.RequireCertificate : ClientCertificateMode.AllowCertificate;
            options.ClientCertificateValidation = (certificate, _, _) => TestCertificateAuthority.IsIssuedByRoot(certificate);
        });

    private static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private sealed class Selector(Func<HttpContext, ForwardProxyDestination, CancellationToken, ValueTask<X509Certificate2?>> select)
        : IForwardProxyUpstreamClientCertificateSelector
    {
        public ValueTask<X509Certificate2?> SelectAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken) =>
            select(context, destination, cancellationToken);
    }

    private sealed class SnapshotTransform(X509Certificate2 a, X509Certificate2 b) : IForwardProxyRequestTransform
    {
        public ValueTask TransformRequestAsync(HttpContext context, ForwardProxyDestination destination, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            context.Items["certificate"] = context.Request.Path == "/a" ? a : b;
            return ValueTask.CompletedTask;
        }
    }
}
