// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using DamianH.Http.ForwardProxy;
using DamianH.Http.ForwardProxy.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// Demo credentials only. Load real credentials from a secret store.
var expectedUser = "demo"u8.ToArray();
var expectedPassword = "change-me"u8.ToArray();

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(8080, listen =>
{
    listen.Protocols = HttpProtocols.Http1;
    listen.UseForwardProxy();
}));

builder.Services
    .AddAuthentication()
    .AddProxyBasic(options =>
    {
        options.Realm = "sample-proxy";
        options.ValidateCredentials = (_, user, password) => ValueTask.FromResult(
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user), expectedUser)
            & CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), expectedPassword));
    });

builder.Services.AddForwardProxy(options =>
{
    options.ProxyName = "sample-proxy";
    options.AuthorizationPolicy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
    options.DeniedHosts.Add("*.internal.example");
});

var app = builder.Build();

app.UseForwardProxy();
app.MapGet("/healthz", () => "ok");

app.Run();
