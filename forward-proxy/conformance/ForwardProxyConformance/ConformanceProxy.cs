// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace DamianH.Http.ForwardProxy.Conformance;

/// <summary>
/// Conformance-only host: an open HTTP/1.1 forward proxy on loopback that may reach loopback destinations
/// on any port (the echo origin). Never expose this configuration.
/// </summary>
internal static class ConformanceProxy
{
    public static async Task<WebApplication> StartAsync(int port, string logPath, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(logPath));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            listen.UseForwardProxy();
        }));
        builder.Services.AddForwardProxy(options =>
        {
            options.AllowedNetworks.Add(IPNetwork.Parse("127.0.0.0/8"));
            options.AllowedConnectPorts = null;
            options.AllowedHttpPorts = null;
        });

        var app = builder.Build();
        app.UseForwardProxy();
        await app.StartAsync(cancellationToken);
        return app;
    }

    public static int Port(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new Uri(address).Port;
    }
}

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly StreamWriter _writer = new(path) { AutoFlush = true };

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_writer)
        {
            _writer.Dispose();
        }
    }

    private void Write(string line)
    {
        lock (_writer)
        {
            if (_writer.BaseStream.CanWrite)
            {
                _writer.WriteLine(line);
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Write($"{logLevel}: {category}[{eventId.Id}] {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
    }
}
