// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using DamianH.Http.ForwardProxy;
using DamianH.Http.ForwardProxy.Authentication;
using EstInterception.Proxy;
using EstInterception.ServiceDefaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var interceptedHosts = (builder.Configuration.GetSection("Interception:Hosts").Get<string[]>() ?? ["httpbin.org"])
    .Select(static host => host.ToLowerInvariant())
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var port = builder.Configuration.GetValue("Proxy:Port", 8080);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port, listen =>
{
    listen.Protocols = HttpProtocols.Http1;
    listen.UseForwardProxy();
}));

var proxyUser = builder.Configuration["Proxy:User"] ?? "demo";
var proxyPassword = builder.Configuration["Proxy:Password"] ?? "change-me";
builder.Services.AddAuthentication()
    .AddProxyBasic(options =>
    {
        options.Realm = "est-interception-sample";
        options.ValidateCredentials = (_, user, password) => ValueTask.FromResult(
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(proxyUser))
            & CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(proxyPassword)));
    });

var policy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .Build();
builder.Services.AddForwardProxy(options =>
{
    options.ProxyName = "est-interception-sample";
    options.AuthorizationPolicy = policy;
});

builder.Services.AddSingleton(new HostListSelector(interceptedHosts));
builder.Services.AddSingleton<IForwardProxyInterceptionSelector>(services => services.GetRequiredService<HostListSelector>());
builder.Services.AddSingleton<EstCertificateProvider>();
builder.Services.AddSingleton<IForwardProxyCertificateProvider>(services => services.GetRequiredService<EstCertificateProvider>());
builder.Services.AddForwardProxyTlsInterception();

var app = builder.Build();
app.Use((context, next) =>
{
    if (context.Features.Get<IForwardProxyInterceptionFeature>() is not null)
    {
        context.Request.Headers["X-Sample-Proxy-User"] = context.User.Identity?.Name;
    }

    return next(context);
});

app.UseForwardProxy();
app.MapDefaultHealthChecks();
app.Run();
