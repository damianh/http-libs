// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DamianH.Http.ForwardProxy;

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
        builder.Host.UseDefaultServiceProvider(o =>
        {
            o.ValidateScopes = true;
            o.ValidateOnBuild = true;
        });
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
