// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using DamianH.Http.ForwardProxy;
using DamianH.Http.ForwardProxy.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ForwardProxySample;

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
        options.ValidateCredentials = (_, user, password) =>
        {
            var userBytes = Encoding.UTF8.GetBytes(user);
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            var userMatches = CryptographicOperations.FixedTimeEquals(userBytes, expectedUser);
            var passwordMatches = CryptographicOperations.FixedTimeEquals(passwordBytes, expectedPassword);

            // Non-short-circuiting & so both comparisons always run.
            var valid = userMatches & passwordMatches;
            return ValueTask.FromResult(valid);
        };
    });

var proxyPolicy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .Build();

builder.Services.AddForwardProxy(options =>
{
    options.ProxyName = "sample-proxy";
    options.AuthorizationPolicy = proxyPolicy;
    options.DeniedHosts.Add("*.internal.example");
});

// Optional TLS interception, e.g. --Interception:Hosts:0=httpbin.org
var interceptedHosts = builder.Configuration.GetSection("Interception:Hosts").Get<string[]>() ?? [];
if (interceptedHosts.Length > 0)
{
    builder.Services.AddSingleton(new HostListSelector(interceptedHosts.Select(h => h.ToLowerInvariant()).ToHashSet()));
    builder.Services.AddSingleton<EphemeralCaCertificateProvider>();
    builder.Services.AddSingleton<IForwardProxyInterceptionSelector>(sp => sp.GetRequiredService<HostListSelector>());
    builder.Services.AddSingleton<IForwardProxyCertificateProvider>(sp => sp.GetRequiredService<EphemeralCaCertificateProvider>());
    builder.Services.AddForwardProxyTlsInterception();
}

var app = builder.Build();

if (interceptedHosts.Length > 0)
{
    // Clients must trust this CA, e.g. curl --cacert <path>.
    var caPath = Path.Combine(Path.GetTempPath(), "forward-proxy-sample-ca.pem");
    File.WriteAllText(caPath, app.Services.GetRequiredService<EphemeralCaCertificateProvider>().CaCertificatePem);
    app.Logger.LogInformation("Intercepting {Hosts}. Interception CA certificate written to {Path}", interceptedHosts, caPath);

    // Runs for requests decrypted from intercepted tunnels before they are forwarded.
    app.Use((context, next) =>
    {
        if (context.Features.Get<IForwardProxyInterceptionFeature>() is not null)
        {
            context.Request.Headers["X-Sample-Proxy-User"] = context.User.Identity?.Name;
        }

        return next(context);
    });
}

app.UseForwardProxy();
app.MapGet("/healthz", () => "ok");

app.Run();
