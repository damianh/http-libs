// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using DamianH.Http.ForwardProxy.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;

namespace DamianH.Http.ForwardProxy;

public class AuthenticationTests
{
    [Fact]
    public async Task ChallengesConnectWithoutCredentials()
    {
        await using var proxy = await StartBasicProxyAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(407);
        response.Header("Proxy-Authenticate").ShouldBe("Basic realm=\"egress\", charset=\"UTF-8\"");
        response.Header("Connection").ShouldBe("close");
    }

    [Fact]
    public async Task RejectsWrongCredentials()
    {
        await using var proxy = await StartBasicProxyAsync();
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"CONNECT example.com:443 HTTP/1.1\r\nProxy-Authorization: Basic {Encode("alice:wrong")}\r\n\r\n");

        (await RawProxy.ReadResponseAsync(socket)).StatusCode.ShouldBe(407);
    }

    [Fact]
    public async Task HttpClientAuthenticatesForConnectAndHttp()
    {
        await using var tlsOrigin = await OriginHost.StartAsync(tls: true);
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await StartBasicProxyAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy, new NetworkCredential("alice", "s3cret:pässword"));

        (await client.GetStringAsync(tlsOrigin.Uri, TestContext.Current.CancellationToken)).ShouldContain("method=GET");
        var body = await client.GetStringAsync(origin.Uri, TestContext.Current.CancellationToken);
        body.ShouldContain("proxy-authorization=\n");
    }

    [Fact]
    public async Task UnauthenticatedHttpRequestsAreChallenged()
    {
        await using var origin = await OriginHost.StartAsync();
        await using var proxy = await StartBasicProxyAsync();
        using var client = ConnectTunnelTests.CreateClient(proxy);

        using var response = await client.GetAsync(origin.Uri, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ProxyAuthenticationRequired);
        response.Headers.ProxyAuthenticate.ToString().ShouldStartWith("Basic realm=\"egress\"");
    }

    [Fact]
    public async Task ConnectAuthenticatesTheConfiguredDefaultScheme()
    {
        using var echo = new EchoServer();
        await using var proxy = await ProxyHost.StartAsync(
            options => options.AuthorizationPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            services => services.AddAuthentication(ProxyBasicDefaults.AuthenticationScheme)
                .AddProxyBasic(options => options.ValidateCredentials = (_, _, _) => ValueTask.FromResult(true)));
        using var socket = await RawProxy.ConnectAsync(proxy.Port);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:password"));

        await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\nProxy-Authorization: Basic {credentials}\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task PolicyCanAuthorizeByDestination()
    {
        using var echo = new EchoServer();
        await using var proxy = await StartBasicProxyAsync(policy => policy.RequireAssertion(context =>
            context.Resource is ForwardProxyDestination { Host: "example.com" } || context.User.Identity?.Name == "admin"));
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, $"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nProxy-Authorization: Basic {Encode("alice:s3cret:pässword")}\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task TranslatesOriginStyleChallenges()
    {
        await using var proxy = await ProxyHost.StartAsync(
            o => o.AuthorizationPolicy = new AuthorizationPolicyBuilder("Bearer").RequireAuthenticatedUser().Build(),
            services => services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, BearerChallengeHandler>("Bearer", null));
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var response = await RawProxy.ReadResponseAsync(socket);

        response.StatusCode.ShouldBe(407);
        response.Header("Proxy-Authenticate").ShouldBe("Bearer realm=\"test\"");
        response.Headers.Contains("WWW-Authenticate").ShouldBeFalse();
    }

    [Fact]
    public async Task ResolvesNamedPolicies()
    {
        await using var proxy = await ProxyHost.StartAsync(
            o => o.AuthorizationPolicyName = "proxy",
            services =>
            {
                services.AddAuthentication().AddProxyBasic(o => o.ValidateCredentials = (_, _, _) => ValueTask.FromResult(false));
                services.AddAuthorizationBuilder().AddPolicy("proxy", p => p.AddAuthenticationSchemes(ProxyBasicDefaults.AuthenticationScheme).RequireAuthenticatedUser());
            });
        using var socket = await RawProxy.ConnectAsync(proxy.Port);

        await RawProxy.SendAsync(socket, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");

        (await RawProxy.ReadResponseAsync(socket)).StatusCode.ShouldBe(407);
    }

    private static Task<ProxyHost> StartBasicProxyAsync(Action<AuthorizationPolicyBuilder>? configurePolicy = null) =>
        ProxyHost.StartAsync(
            o =>
            {
                var policy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme).RequireAuthenticatedUser();
                configurePolicy?.Invoke(policy);
                o.AuthorizationPolicy = policy.Build();
            },
            services => services.AddAuthentication().AddProxyBasic(o =>
            {
                o.Realm = "egress";
                o.ValidateCredentials = (_, user, password) => ValueTask.FromResult(user == "alice" && password == "s3cret:pässword");
            }));

    private static string Encode(string credentials) => Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

    private sealed class BearerChallengeHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 401;
            Response.Headers.WWWAuthenticate = "Bearer realm=\"test\"";
            return Task.CompletedTask;
        }
    }
}
