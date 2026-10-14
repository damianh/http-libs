// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using DamianH.Http.ForwardProxy;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Conformance-only host: an open HTTP/1.1 forward proxy on loopback that may reach loopback
// destinations on any port (the HTTP Garden echo origin). Never expose this configuration.
var port = 0;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length)
    {
        port = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
}

var builder = WebApplication.CreateSlimBuilder();
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
app.MapGet("/proxy-health", () => Results.Json(new { ready = true, processId = Environment.ProcessId }));
app.Run();
